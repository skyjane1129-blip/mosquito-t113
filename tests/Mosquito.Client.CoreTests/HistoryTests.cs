using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mosquito.Client.Core;

internal static class HistoryTests
{
    public static async Task RunAsync(Action<bool, string> assert, string root, OutboxRepository outbox, CaptureArtifact artifact)
    {
        var day = HistoryQuery.Dates(null, null, new(2026, 9, 7), new(2026, 9, 7));
        assert(day.From == DateTimeOffset.Parse("2026-09-06T16:00:00Z") && day.ToExclusive == DateTimeOffset.Parse("2026-09-07T16:00:00Z"), "Beijing midnight independent of Windows timezone");
        var records = Enumerable.Range(0, 553).Select(i => Record(i, day.From.AddSeconds(i))).ToArray();
        assert(day.Matches(records[0]) && !day.Matches(records[0] with { CapturedAtUtc = day.ToExclusive }), "inclusive day start / exclusive following midnight");
        var requests = new List<string>();
        var api = Client(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var query = ParseQuery(request.RequestUri);
            var offset = query.TryGetValue("cursor", out var cursor) ? int.Parse(cursor) : 0;
            assert(query["limit"] == "50", "50 records per API page");
            if (offset > 0) assert(query["snapshot"] == "stable", "server snapshot preserved across pages");
            return Json(new CapturePage(records.Skip(offset).Take(50).ToArray(), offset + 50 < records.Length ? (offset + 50).ToString() : null, "stable", true));
        });
        await Login(api);
        var result = await api.QueryHistoryAsync(day, default);
        assert(result.Items.Count == 553 && result.IsComplete && requests.Count == 12, "fetch all records beyond legacy 500 limit");
        requests.Clear(); var peek = await api.PeekHistoryAsync(day, default);
        assert(peek.Count == 50 && requests.Count == 1, "background notification check only fetches first page");
        var csvPath = Path.Combine(root, "all-pages.csv"); await CsvExporter.ExportAsync(csvPath, result.Items, default);
        assert((await File.ReadAllLinesAsync(csvPath)).Length == 554, "CSV contains entire filtered snapshot across pages");
        var session = new HistorySession(); session.Apply(day, result); session.Move(4); session.SelectedId = session.Rows.First().Id;
        var selected = session.SelectedId; session.HasNewRecords = true;
        assert(session.Page == 4 && session.SelectedId == selected && session.Rows.Count() == 50, "notification does not replace history page or selection");
        session.Apply(day, result);
        assert(session.Page == 4 && session.SelectedId == selected, "explicit refresh retains selection and page");
        session.Apply(day with { Device = "different" }, new([], true, null));
        assert(session.Page == 0 && session.SelectedId is null, "new query resets incompatible selection");

        var legacy = Client(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v2") ? new(HttpStatusCode.NotFound) : Json(records.Take(500).ToArray()));
        await Login(legacy); var capped = await legacy.QueryHistoryAsync(day, default);
        assert(!capped.IsComplete && capped.Warning!.Contains("500"), "legacy cap warns incomplete and prevents full export");
        var smallLegacy = Client(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v2") ? new(HttpStatusCode.NotImplemented) : Json(records.Take(12).ToArray()));
        await Login(smallLegacy); assert((await smallLegacy.QueryHistoryAsync(day, default)).IsComplete, "old API still works for complete small results");

        var broken = Client(_ => Json(new CapturePage(records.Take(1).ToArray(), "repeat", "fixed", true)));
        await Login(broken); var rejected = false;
        try { await broken.QueryHistoryAsync(day, default); } catch (InvalidDataException) { rejected = true; }
        assert(rejected, "repeated cursor rejected without infinite loop");

        var at = DateTimeOffset.Parse("2026-09-07T03:07:00Z"); // 11:07, deliberately not a clock-aligned hour
        ReportCheck Check(ReportState state, DateTimeOffset checkedAt, DateTimeOffset? received = null, string? status = null) =>
            new("slot-1", "MQ-001", at, state, true, at.AddDays(-1), true, received is null ? null : records[0].Id, received, status)
            { CheckedAtUtc = checkedAt, ScheduleAnchorUtc = at.AddDays(-1), IntervalMinutes = 60 };
        assert(Check(ReportState.NotDue, at.AddTicks(-1)).EffectiveState == ReportState.NotDue, "not due before slot");
        assert(Check(ReportState.Waiting, at).EffectiveState == ReportState.Waiting, "waiting starts at slot");
        assert(Check(ReportState.Waiting, at.AddMinutes(15).AddTicks(-1)).EffectiveState == ReportState.Waiting, "inside grace period");
        assert(Check(ReportState.Overdue, at.AddMinutes(15)).EffectiveState == ReportState.Overdue, "exact 15 minute boundary overdue");
        assert(Check(ReportState.OnTime, at.AddMinutes(20), at.AddMinutes(15).AddTicks(-1), "Partial").EffectiveState == ReportState.OnTime, "partial record before deadline is received");
        var late = Check(ReportState.Late, at.AddMinutes(20), at.AddMinutes(15), "Complete");
        assert(late.EffectiveState == ReportState.Late && late.CanViewRecord, "exact deadline arrival is late and links photo");
        assert(Check(ReportState.OnTime, at.AddMinutes(20), at.AddMinutes(10), "Uploading").EffectiveState == ReportState.Pending, "unfinished upload is not a usable receipt");
        assert((late with { ScheduleConfirmed = false }).EffectiveState == ReportState.Pending, "unknown schedule never raises missing alarm");
        assert((late with { AssociationReliable = false }).EffectiveState == ReportState.Pending, "uncertain slot association stays pending");
        assert((late with { ScheduleEffectiveAtUtc = at.AddDays(1) }).EffectiveState == ReportState.Pending, "no checks before activation");
        assert((late with { ScheduleAnchorUtc = at.AddMinutes(2) }).EffectiveState == ReportState.Pending, "invalid schedule anchor rejected");
        var older = Check(ReportState.Overdue, at.AddHours(2)) with { SlotId = "older", ExpectedAtUtc = at.AddHours(-1) };
        var checksApi = Client(_ => Json(new ReportPage([older, late, late], null, "checks", at.AddHours(2), true)));
        await Login(checksApi); var snapshot = await checksApi.QueryReportChecksAsync(day, default);
        assert(snapshot.Items.Count == 2 && snapshot.Items.Single(x => x.SlotId == "older").EffectiveState == ReportState.Overdue, "dedup late receipt, later upload cannot fill older gap");
        var unsupported = await legacy.QueryReportChecksAsync(day, default);
        assert(!unsupported.Supported && unsupported.Items.Count == 0, "unsupported checks do not fabricate hourly slots");
        var conflictApi = Client(_ => Json(new ReportPage([late, late with { SlotId = "other", ExpectedAtUtc = at.AddHours(-1) }], null, "checks", at.AddHours(2), true)));
        await Login(conflictApi); assert((await conflictApi.QueryReportChecksAsync(day, default)).Items.All(x => x.EffectiveState == ReportState.Pending), "same capture cannot satisfy two slots");

        await outbox.MarkAsync(artifact, CaptureState.Failed, "network unavailable", true, default);
        var local = await LocalHistory.QueryAsync(outbox, HistoryQuery.Dates(null, null, artifact.HostCapturedAtUtc.ToOffset(BeijingTime.Offset).Date, artifact.HostCapturedAtUtc.ToOffset(BeijingTime.Offset).Date), default);
        assert(local.Items[0].StatusDisplay == "完整" && local.Items[0].FailureStage == "UPLOAD", "upload failure preserves local capture success");
        Console.WriteLine("PASS: 553-record paging/export, Beijing date boundaries, selection snapshot, legacy truncation, cursor integrity");
        Console.WriteLine("PASS: grace boundary, partial receipts, late repair, unknown schedules, activation, dedup and older gaps");
    }
    public static CloudCaptureRecord Record(int index, DateTimeOffset at)
    {
        var bytes = new byte[16]; BitConverter.GetBytes(index + 1).CopyTo(bytes, 0);
        return new(new Guid(bytes), "ADB-001", "Complete", "BOARD_4G", at, true, 1000, 500,
            new("PASS", "NONE", 2635, 6120, true, 1), new("PASS", "NONE", 4124, "NOT_CHARGING", false, null, null, null, 1), null)
        { DeviceId = "MQ-001", TimeSource = "BOARD", TriggerSource = "SCHEDULED", ReceivedAtUtc = at.AddMinutes(2) };
    }
    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
    private static Task Login(CloudApiClient api) => api.LoginAsync("test", "test", default);
    private static CloudApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new AppSettings { ApiBaseUrl = "http://test.invalid" }, new Handler(response));
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/auth/login" ? Json(new { accessToken = "test-token", expiresAt = DateTimeOffset.UtcNow.AddHours(1) }) : response(request));
    }
}
