using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mosquito.Cloud.Api;

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mosquito-cloud-tests-{Guid.NewGuid():N}");
try
{
    var tokenService = new OperatorTokenService(Options.Create(new AuthOptions
    {
        Username = "operator",
        Password = "correct-password",
        SigningKey = "unit-test-signing-key-at-least-32-characters"
    }));
    Assert(tokenService.Login(new LoginRequest("operator", "wrong")) is null, "wrong password rejected");
    var login = tokenService.Login(new LoginRequest("operator", "correct-password"));
    Assert(login is not null && tokenService.Validate(login.AccessToken), "valid operator token accepted");
    Assert(!tokenService.Validate($"{login!.AccessToken}tampered"), "tampered token rejected");

    var storage = new LocalObjectStorage(Options.Create(new StorageOptions
    {
        LocalRoot = temporaryRoot,
        PublicBaseUrl = "http://127.0.0.1:5080",
        TokenSigningKey = "unit-test-storage-key-at-least-32-characters"
    }));

    // The same workflow runs against the in-memory and the SQLite repositories.
    var database = SqliteDatabase.FromOptions($"Data Source={Path.Combine(temporaryRoot, "smoke.db")}", temporaryRoot);
    var backends = new (string Name, ICaptureRepository Captures, IDeviceRepository Devices)[]
    {
        ("InMemory", new InMemoryCaptureRepository(), new InMemoryDeviceRepository()),
        ("Sqlite", new SqliteCaptureRepository(database), new SqliteDeviceRepository(database))
    };
    foreach (var (name, repository, deviceRepository) in backends)
    {
        await repository.InitializeAsync(CancellationToken.None);
        await deviceRepository.InitializeAsync(CancellationToken.None);
        var deviceOptions = Options.Create(new DeviceOptions { OnlineWindowSeconds = 180, CommandTtlMinutes = 10, MaxLongPollSeconds = 2 });
        // A reference point near the samples lets the engine annotate every candidate with its error.
        var locationOptions = Options.Create(new LocationOptions
        {
            ReferenceLatitude = 31.2312,
            ReferenceLongitude = 121.4742,
            ReferenceCoordinateSystem = "WGS84",
            HistoryMinSamples = 1
        });
        var locationEngine = new LocationEngine(deviceRepository, Array.Empty<ICellLocator>(), locationOptions, NullLogger<LocationEngine>.Instance);
        var deviceService = new DeviceService(deviceRepository, locationEngine, deviceOptions);
        var captures = new CaptureService(repository, storage, deviceService);

        // Device registers through a heartbeat carrying a WGS84 LBS location.
        var heartbeat = await deviceService.HeartbeatAsync(new HeartbeatRequest(
            "MQ-SH-001", "MOSQUITO-T113-DEV", "dev-v5.6.4-client-direct", 120.5,
            new EnvironmentReading("PASS", "NONE", 2962, 4712, true, 1000),
            new PowerReading("WARN", "POWER_FAULT_ACTIVE", 4124, "CHARGE_DONE", true, 4900, 0, "0x80", 1010),
            new GeoSample(31.2304, 121.4737, null, null, "WGS84") { Source = "LBS" }), CancellationToken.None);
        Assert(heartbeat.LastSeenAtUtc is not null && heartbeat.LastLocation?.CoordinateSystem == "WGS84", $"{name}: heartbeat registers device with raw WGS84");
        var view = deviceService.ToView(heartbeat);
        Assert(view.Online && view.LastLocation?.CoordinateSystem == "GCJ02" && view.LastLocationWgs84?.CoordinateSystem == "WGS84", $"{name}: operator view converts to GCJ02 and keeps WGS84");
        var shift = Math.Abs(view.LastLocation!.Latitude - 31.2304) + Math.Abs(view.LastLocation.Longitude - 121.4737);
        Assert(shift is > 0.001 and < 0.02, $"{name}: GCJ02 offset magnitude is plausible ({shift:F5} deg)");

        // Operator queues a capture command, the device long-polls it, then acknowledges via capture completion.
        Assert(await deviceService.WaitForCommandAsync("MQ-SH-001", 0, CancellationToken.None) is null, $"{name}: no pending command yet");
        var command = await deviceService.CreateCommandAsync("MQ-SH-001", new CreateCommandRequest("capture", 500), "operator", CancellationToken.None);
        Assert(command.Status == CommandStatus.Pending && command.Focus == 500, $"{name}: command created pending");
        var dispatched = await deviceService.WaitForCommandAsync("MQ-SH-001", 1, CancellationToken.None);
        Assert(dispatched?.Id == command.Id && dispatched.Status == CommandStatus.Dispatched, $"{name}: device receives the command once");
        Assert(await deviceService.WaitForCommandAsync("MQ-SH-001", 0, CancellationToken.None) is null, $"{name}: dispatched command is not handed out twice");
        var rejectedUnknown = false;
        try { await deviceService.CreateCommandAsync("MQ-UNKNOWN", new CreateCommandRequest("capture", null), "operator", CancellationToken.None); }
        catch (KeyNotFoundException) { rejectedUnknown = true; }
        Assert(rejectedUnknown, $"{name}: commands for unregistered devices are rejected");
        // Fast site learning command: typed Learn, carries rounds, dispatched like a capture.
        var learn = await deviceService.CreateCommandAsync("MQ-SH-001", new CreateCommandRequest("learn", null) { Rounds = 4 }, "operator", CancellationToken.None);
        Assert(learn.Type == CommandType.Learn && learn.Rounds == 4 && learn.Focus is null, $"{name}: learn command created with rounds");
        var learnDispatched = await deviceService.WaitForCommandAsync("MQ-SH-001", 0, CancellationToken.None);
        Assert(learnDispatched?.Id == learn.Id && learnDispatched.Type == CommandType.Learn, $"{name}: learn command dispatched to the device");
        await deviceService.AcknowledgeAsync(learn.Id, new CommandAckRequest("Completed", null, null), CancellationToken.None);
        var badType = false;
        try { await deviceService.CreateCommandAsync("MQ-SH-001", new CreateCommandRequest("reboot", null), "operator", CancellationToken.None); }
        catch (CaptureValidationException) { badType = true; }
        Assert(badType, $"{name}: unknown command types are rejected");

        var photo = "fake-jpeg-content"u8.ToArray();
        var metadata = "metadata_version=3\nrecord_status=COMPLETE\n"u8.ToArray();
        var id = Guid.NewGuid();
        var create = await captures.CreateAsync(new CreateCaptureRequest(
            id,
            "MOSQUITO-T113-DEV",
            "BOARD_4G",
            DateTimeOffset.UtcNow,
            123.45,
            false,
            3,
            "COMPLETE",
            Sha256(photo),
            Sha256(metadata),
            new EnvironmentReading("PASS", "NONE", 3016, 4608, true, 123000),
            new PowerReading("PASS", "NONE", 4124, "CHARGE_DONE", true, 4900, 0, "0x00", 123100))
        {
            DeviceId = "MQ-SH-001",
            CommandId = command.Id,
            Location = new GeoSample(31.2310, 121.4740, DateTimeOffset.UtcNow, null, "WGS84") { Source = "LBS" }
        }, CancellationToken.None);

        await UploadAsync(storage, create.PhotoUpload, photo);
        await UploadAsync(storage, create.MetadataUpload, metadata);
        var complete = await captures.CompleteAsync(
            id,
            new CompleteCaptureRequest(photo.Length, metadata.Length),
            CancellationToken.None);
        Assert(complete.Status == CaptureStatus.Complete, $"{name}: complete capture state transition");
        Assert(complete.PhotoBytes == photo.Length, $"{name}: photo byte count recorded");
        Assert(complete.ReceivedAtUtc is not null && complete.TriggerSource == "REMOTE_COMMAND" && complete.TimeSource == "SERVER", $"{name}: v2 fields populated");
        Assert(CaptureService.ForOperator(complete).Location?.CoordinateSystem == "GCJ02" && complete.Location?.CoordinateSystem == "WGS84", $"{name}: capture location converted only for operators");
        var detail = await captures.GetDetailAsync(id, CancellationToken.None);
        Assert(detail?.PhotoDownloadUrl is not null, $"{name}: completed capture has a signed download URL");
        var finished = await deviceService.GetCommandAsync(command.Id, CancellationToken.None);
        Assert(finished?.Status == CommandStatus.Completed && finished.CaptureId == id, $"{name}: capture completion closes the originating command");
        var registered = await deviceRepository.GetDeviceAsync("MQ-SH-001", CancellationToken.None);
        Assert(registered?.LatestCaptureId == id && registered.LastLocation?.Latitude == 31.2310, $"{name}: device registry tracks the latest capture and location");
        var queried = await repository.QueryAsync(null, "MQ-SH-001", null, null, null, null, CancellationToken.None);
        Assert(queried.Count == 1 && queried[0].Id == id, $"{name}: query by stable deviceId");

        var idempotent = await captures.CreateAsync(new CreateCaptureRequest(
            id,
            "MOSQUITO-T113-DEV",
            "BOARD_4G",
            complete.CapturedAtUtc,
            complete.BoardUptimeSeconds,
            complete.BoardTimeValid,
            3,
            "COMPLETE",
            complete.PhotoSha256,
            complete.MetadataSha256,
            complete.Environment,
            complete.Power),
            CancellationToken.None);
        Assert(idempotent.CaptureId == id, $"{name}: same capture manifest is idempotent");

        // A failed acknowledgement and an expired command.
        var failing = await deviceService.CreateCommandAsync("MQ-SH-001", new CreateCommandRequest("capture", null), "operator", CancellationToken.None);
        await deviceService.WaitForCommandAsync("MQ-SH-001", 0, CancellationToken.None);
        var failed = await deviceService.AcknowledgeAsync(failing.Id, new CommandAckRequest("Failed", null, "camera busy"), CancellationToken.None);
        Assert(failed.Status == CommandStatus.Failed && failed.Error == "camera busy", $"{name}: failed acknowledgement recorded");
        var stale = await deviceService.CreateCommandAsync("MQ-SH-001", new CreateCommandRequest("capture", null), "operator", CancellationToken.None);
        await deviceRepository.UpdateCommandAsync(stale with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None);
        Assert(await deviceService.WaitForCommandAsync("MQ-SH-001", 0, CancellationToken.None) is null, $"{name}: expired command is not dispatched");
        Assert((await deviceService.GetCommandAsync(stale.Id, CancellationToken.None))?.Status == CommandStatus.Expired, $"{name}: expired command marked");

        // Clear the local objects so the next backend can upload the same keys again.
        Directory.Delete(Path.Combine(temporaryRoot, "captures"), true);
    }

    var rejected = false;
    try
    {
        var invalidTarget = storage.CreateUploadTarget("captures/tampered.bin", Sha256("fake"u8.ToArray()), "application/octet-stream");
        await UploadAsync(storage, invalidTarget, "different"u8.ToArray());
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }
    Assert(rejected, "tampered upload rejected by SHA-256");

    // ---- GeoConvert round-trip and the multi-cell location engine (no external locators). ----
    var (gLat, gLon) = GeoConvert.Wgs84ToGcj02(31.2304, 121.4737);
    var (backLat, backLon) = GeoConvert.Gcj02ToWgs84(gLat, gLon);
    Assert(Math.Abs(backLat - 31.2304) < 1e-6 && Math.Abs(backLon - 121.4737) < 1e-6, "GCJ02 round-trip returns to WGS84 within a metre");
    var straight = GeoConvert.DistanceMeters(31.2304, 121.4737, 31.2404, 121.4737);
    Assert(straight is > 1100 and < 1120, $"0.01 deg latitude is about 1.11 km ({straight:F0} m)");

    {
        var engineRepo = new InMemoryDeviceRepository();
        await engineRepo.InitializeAsync(CancellationToken.None);
        var engineOptions = Options.Create(new LocationOptions
        {
            ReferenceLatitude = 31.2312,
            ReferenceLongitude = 121.4742,
            ReferenceCoordinateSystem = "WGS84",
            HistoryMinSamples = 1
        });
        var engine = new LocationEngine(engineRepo, Array.Empty<ICellLocator>(), engineOptions, NullLogger<LocationEngine>.Instance);
        var cells = new[]
        {
            new CellObservation(true, 460, 0, 6334, 140542123, 351, 39148, -89, -5.5),
            new CellObservation(false, 460, 0, 6334, 140541985, 37, 38950, -95, -8.0)
        };
        var reported = new GeoSample(31.2318, 121.4750, DateTimeOffset.UtcNow, null, "WGS84") { Source = "LBS", Cells = cells };
        var outcome = await engine.ProcessAsync("MQ-ENGINE", reported, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert(outcome.Candidates.Any(c => c.Source == LocationSources.Fused), "engine produces a fused candidate from cell-based estimates");
        Assert(outcome.Candidates.All(c => c.CoordinateSystem == "WGS84"), "engine stores every candidate as WGS84");
        Assert(outcome.Chosen.ReferenceErrorMeters is not null, "engine annotates the chosen candidate with its reference error");
        Assert(LocationEngine.ShouldReplace(null, outcome.Chosen), "a first fix always replaces an empty registry position");
        var gnss = new GeoSample(31.2312, 121.4742, DateTimeOffset.UtcNow, null, "WGS84") { Source = "GNSS" };
        Assert(LocationEngine.ShouldReplace(outcome.Chosen, gnss), "a GNSS fix always replaces a cell-based position");
        Assert(!LocationEngine.ShouldReplace(gnss, reported with { Source = "LBS" }), "a fresh GNSS position is not demoted by a later LBS echo");
        var eval = await engine.EvaluateAsync("MQ-ENGINE", 24, CancellationToken.None);
        Assert(eval.HasReference && eval.Sources.Count > 0, "evaluation reports per-source error statistics");

        // Self-learned cell table: cell A was serving at P1 in the past; now cell B is serving (answer P2)
        // but A is the strongest cell we already know, so the learned estimate must return P1.
        var cellA = new CellObservation(true, 460, 1, 100, 111111, 10, 1894, -80, -8.0);
        var cellB = new CellObservation(true, 460, 1, 100, 222222, 20, 1552, -95, -12.0);
        await engine.ProcessAsync("MQ-LEARN", new GeoSample(31.2400, 121.4800, DateTimeOffset.UtcNow.AddMinutes(-30), null, "WGS84")
            { Source = "LBS", Cells = [cellA] }, DateTimeOffset.UtcNow.AddMinutes(-30), CancellationToken.None);
        var learnedOutcome = await engine.ProcessAsync("MQ-LEARN", new GeoSample(31.2500, 121.4900, DateTimeOffset.UtcNow, null, "WGS84")
            { Source = "LBS", Cells = [cellB, cellA with { Serving = false, RsrpDbm = -70 }] }, DateTimeOffset.UtcNow, CancellationToken.None);
        var learnedFix = learnedOutcome.Candidates.FirstOrDefault(c => c.Source == LocationSources.CellLearned);
        Assert(learnedFix is not null && Math.Abs(learnedFix.Latitude - 31.2400) < 1e-6 && Math.Abs(learnedFix.Longitude - 121.4800) < 1e-6,
            "learned-cell locator picks the strongest known cell's learned position");
        Assert(learnedOutcome.Chosen.Source is LocationSources.CellLearnedMedian or LocationSources.CellLearned,
            "auto policy prefers the learned-cell estimate over the raw single-cell answer");

        // Site change: three fixes at one place, then the trap is moved ~20 km. The trailing median must
        // not drag the new position back to the old site.
        var siteEngineRepo = new InMemoryDeviceRepository();
        await siteEngineRepo.InitializeAsync(CancellationToken.None);
        var siteEngine = new LocationEngine(siteEngineRepo, Array.Empty<ICellLocator>(),
            Options.Create(new LocationOptions { HistoryMinSamples = 1 }), NullLogger<LocationEngine>.Instance);
        for (var i = 0; i < 3; i++)
        {
            await siteEngine.ProcessAsync("MQ-MOVE", new GeoSample(31.2900, 121.5500, DateTimeOffset.UtcNow.AddMinutes(-30 + i), null, "WGS84") { Source = "LBS" },
                DateTimeOffset.UtcNow.AddMinutes(-30 + i), CancellationToken.None);
        }
        var moved = await siteEngine.ProcessAsync("MQ-MOVE", new GeoSample(31.1950, 121.3250, DateTimeOffset.UtcNow, null, "WGS84") { Source = "LBS" },
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert(GeoConvert.DistanceMeters(moved.Chosen.Latitude, moved.Chosen.Longitude, 31.1950, 121.3250) < 50,
            "after moving to a new site the chosen position follows the new site, not the old median");
    }

    // ---- Mosquito / egg detection orchestration (detector replaced by a fake runner) ----
    {
        var repository = new InMemoryCaptureRepository();
        await repository.InitializeAsync(CancellationToken.None);
        var captures = new CaptureService(repository, storage);
        var photo = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 };
        var metadata = "METADATA_VERSION=3\n"u8.ToArray();
        var created = await captures.CreateAsync(new CreateCaptureRequest(
            Guid.NewGuid(), "MOSQUITO-T113-DEV", "BOARD_4G", DateTimeOffset.UtcNow.AddMinutes(-1), 100, true, 3, "COMPLETE",
            Sha256(photo), Sha256(metadata), null, null) { DeviceId = "MQ-SH-001" }, CancellationToken.None);
        await UploadAsync(storage, created.PhotoUpload, photo);
        await UploadAsync(storage, created.MetadataUpload, metadata);
        await captures.CompleteAsync(created.CaptureId, new CompleteCaptureRequest(photo.Length, metadata.Length), CancellationToken.None);

        var analysisOptions = Options.Create(new AnalysisOptions { ModelVersion = "fake_r1", Confidence = 0.25, TimeoutSeconds = 60 });
        var runner = new FakeEggRunner();
        var analysis = new EggAnalysisService(repository, storage, runner, analysisOptions, NullLogger<EggAnalysisService>.Instance);

        var pendingCapture = await captures.CreateAsync(new CreateCaptureRequest(
            Guid.NewGuid(), "MOSQUITO-T113-DEV", "BOARD_4G", DateTimeOffset.UtcNow, 100, true, 3, "COMPLETE",
            Sha256(photo), Sha256(metadata), null, null), CancellationToken.None);
        var analysisRejected = false;
        try { await analysis.StartAsync(pendingCapture.CaptureId, false, "demo", CancellationToken.None); }
        catch (CaptureValidationException) { analysisRejected = true; }
        Assert(analysisRejected, "analysis refuses a capture whose photo is not uploaded yet");

        var started = await analysis.StartAsync(created.CaptureId, false, "demo", CancellationToken.None);
        Assert(started.Analysis?.Status == AnalysisStatus.Running && started.AnnotatedDownloadUrl is null, "start marks the capture Running without an annotated URL");
        var again = await analysis.StartAsync(created.CaptureId, false, "demo", CancellationToken.None);
        Assert(again.Analysis?.Status == AnalysisStatus.Running && runner.Calls <= 1, "a second start while running does not launch a second detector run");
        await analysis.WaitAsync(created.CaptureId);
        var done = await analysis.GetAsync(created.CaptureId, CancellationToken.None);
        Assert(done?.Analysis is { Status: AnalysisStatus.Completed, EggCount: 3, MosquitoCount: 1, Detections.Length: 4 } &&
               done.Analysis.ModelVersion == "fake_r1" && done.AnnotatedDownloadUrl is not null,
            "completed analysis carries counts, boxes and an annotated download URL");
        var annotatedKey = EggAnalysisService.AnnotatedKey((await repository.GetAsync(created.CaptureId, CancellationToken.None))!.PhotoObjectKey, "fake_r1");
        Assert(done!.Analysis!.AnnotatedObjectKey == annotatedKey && (await storage.GetInfoAsync(annotatedKey, CancellationToken.None)).Exists,
            "annotated JPEG is stored next to the photo");
        var detail = await captures.GetDetailAsync(created.CaptureId, CancellationToken.None);
        Assert(detail?.AnnotatedDownloadUrl is not null && detail.Capture.Analysis?.EggCount == 3, "capture detail exposes the analysis and the annotated URL");
        var cached = await analysis.StartAsync(created.CaptureId, false, "demo", CancellationToken.None);
        Assert(cached.Analysis?.Status == AnalysisStatus.Completed && runner.Calls == 1, "start without force returns the stored result");

        runner.FailNext = "python exploded";
        var forced = await analysis.StartAsync(created.CaptureId, true, "demo", CancellationToken.None);
        Assert(forced.Analysis?.Status == AnalysisStatus.Running, "force starts a new run");
        await analysis.WaitAsync(created.CaptureId);
        var failed = await analysis.GetAsync(created.CaptureId, CancellationToken.None);
        Assert(failed?.Analysis is { Status: AnalysisStatus.Failed, Error: "python exploded" } && failed.AnnotatedDownloadUrl is null && runner.Calls == 2,
            "a detector failure is recorded with its message");
        var retried = await analysis.StartAsync(created.CaptureId, false, "demo", CancellationToken.None);
        Assert(retried.Analysis?.Status == AnalysisStatus.Running, "a failed analysis can be started again without force");
        await analysis.WaitAsync(created.CaptureId);
        Assert((await analysis.GetAsync(created.CaptureId, CancellationToken.None))?.Analysis?.Status == AnalysisStatus.Completed, "retry after failure completes");
    }

    Console.WriteLine("PASS: operator authentication");
    Console.WriteLine("PASS: signed local upload and SHA-256 verification");
    Console.WriteLine("PASS: heartbeat registry, GCJ02 conversion and command lifecycle (InMemory + Sqlite)");
    Console.WriteLine("PASS: capture create/upload/complete/detail workflow with deviceId, commandId and location");
    Console.WriteLine("PASS: idempotent retry, failed/expired commands and tamper rejection");
    Console.WriteLine("PASS: GCJ02 round-trip, distance, multi-cell fusion, source selection and evaluation");
    Console.WriteLine("PASS: egg analysis orchestration (running/completed/failed, cached result, force re-run, annotated object)");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static async Task UploadAsync(LocalObjectStorage storage, UploadTarget target, byte[] bytes)
{
    var token = Uri.UnescapeDataString(new Uri(target.Url).Segments[^1]);
    await using var stream = new MemoryStream(bytes, writable: false);
    await storage.AcceptLocalUploadAsync(token, stream, CancellationToken.None);
}

static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Assertion failed: {message}");
    }
}

// Stands in for ai/analyze.py: writes a tiny "annotated" file and returns fixed boxes.
sealed class FakeEggRunner : IEggAnalysisRunner
{
    public int Calls { get; private set; }
    public string? FailNext { get; set; }
    public async Task<EggAnalysisResult> RunAsync(string imagePath, string annotatedImagePath, CancellationToken cancellationToken)
    {
        Calls++;
        Assert(File.Exists(imagePath), "runner receives the photo on disk");
        if (FailNext is { } error) { FailNext = null; throw new InvalidOperationException(error); }
        await File.WriteAllBytesAsync(annotatedImagePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, cancellationToken);
        return new EggAnalysisResult("fake_r1", 3, 1, 3264, 2448, 80, 1234,
        [
            new EggDetection("egg", 0.9, 10, 10, 40, 40),
            new EggDetection("egg", 0.8, 100, 10, 130, 40),
            new EggDetection("egg", 0.7, 200, 10, 230, 40),
            new EggDetection("mosquito", 0.6, 500, 500, 900, 900)
        ]);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
    }
}
