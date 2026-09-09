using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Mosquito.Client;
using Mosquito.Client.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit opt-in Windows diagnostic. Normal UI tests remain entirely simulated.
        if (args is ["--detect-device", var settingsPath, var reportPath])
        {
            try
            {
                var settings = AppSettings.Load(Path.GetFullPath(settingsPath)); var adb = new AdbClient(settings);
                var detection = new DeviceDetectionService(settings, adb, new WindowsUsbDeviceProbe(), adb.GetVersionAsync).DetectAsync(default).GetAwaiter().GetResult();
                var json = JsonSerializer.Serialize(detection, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } });
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); File.WriteAllText(reportPath, json);
                Console.WriteLine(json); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        var runRealDirect = args.Length == 3 && args[0] == "--real-direct";
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (runRealDirect)
                    await RunRealDirectAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                else
                    await RunAsync(args.Length == 0 ? Path.GetFullPath("artifacts/ui-check") : Path.GetFullPath(args[0]));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        });
        // Run only the dispatcher: App.Run would invoke the production OnStartup and touch real configuration/ADB.
        Dispatcher.Run(); return result;
    }

    private static async Task RunRealDirectAsync(string settingsPath, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("real-direct output directory must be new and empty");
        Directory.CreateDirectory(output);
        var settings = AppSettings.Load(settingsPath);
        var outputRoot = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dataRoot = Path.GetFullPath(settings.LocalDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert(dataRoot.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase),
            "real-direct LocalDataRoot must be inside its isolated output directory");

        var diagnosticDirectory = Path.Combine(settings.LocalDataRoot, "diagnostics");
        Directory.CreateDirectory(diagnosticDirectory);
        var diagnosticPath = Path.Combine(diagnosticDirectory, "adb-commands.jsonl");
        await using (var seed = new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            seed.SetLength(5 * 1024 * 1024);
        var adb = new AdbClient(settings);
        Assert(settings.DeviceSerial == "MOSQUITO-T113-DEV", "real-direct targets the expected physical board");
        Assert(settings.BoardCommandPath == "/usr/bin:/bin" &&
               !settings.BoardCommandPath.Contains("client-demo", StringComparison.Ordinal),
            "v5.6.4 real-direct uses only installed board commands, without client-demo");
        var nativeCommands = new[] { "mosquito-version", "mosquito-capture", "mosquito-environment", "mosquito-power" };
        var identityProbe = await adb.ShellAsync(settings.DeviceSerial,
            "export PATH=/usr/bin:/bin; for c in mosquito-version mosquito-capture mosquito-environment mosquito-power; do p=$(command -v \"$c\" 2>/dev/null); printf 'COMMAND_PATH:%s=%s\\n' \"$c\" \"$p\"; done; mosquito-version",
            default);
        Assert(identityProbe.ExitCode == 0, "installed v5.6.4 identity probe returns board exit 0");
        var commandPaths = nativeCommands.ToDictionary(
            command => command,
            command => RequiredOutputMarker(identityProbe.StandardOutput, "COMMAND_PATH:" + command));
        Assert(commandPaths.All(pair => pair.Value == "/usr/bin/" + pair.Key),
            "all direct commands resolve exactly from /usr/bin");
        var imageVersion = RequiredOutputMarker(identityProbe.StandardOutput, "MOSQUITO_IMAGE");
        var buildDate = RequiredOutputMarker(identityProbe.StandardOutput, "MOSQUITO_BUILD_DATE");
        var boardPackage = RequiredOutputMarker(identityProbe.StandardOutput, "MOSQUITO_BOARD_PACKAGE");
        var sourceState = RequiredOutputMarker(identityProbe.StandardOutput, "MOSQUITO_SOURCE_STATE");
        var metadataDeclaration = RequiredOutputMarker(identityProbe.StandardOutput, "MOSQUITO_PHOTO_METADATA");
        Assert(imageVersion == "dev-v5.6.4-client-direct" && boardPackage == "2.18-1" && metadataDeclaration == "3",
            "physical board identifies the exact v5.6.4 client-direct image, package and metadata declaration");
        var markerSuccess = await adb.ShellAsync(settings.DeviceSerial, "printf 'MARKER_BUSINESS=ok'; true", default);
        var markerFailure = await adb.ShellAsync(settings.DeviceSerial, "printf 'EXPECTED_REMOTE_FAILURE=1'; false", default);
        const string diagnosticSecret = "diagnostic-test-secret";
        var redactionProbe = await adb.ShellAsync(settings.DeviceSerial,
            $"printf 'access_token={diagnosticSecret}'; true", default);
        var rotatedDiagnosticPath = diagnosticPath + ".1";
        Assert(File.Exists(rotatedDiagnosticPath) && new FileInfo(rotatedDiagnosticPath).Length == 5 * 1024 * 1024,
            "ADB diagnostic log rotates at the 5 MiB bound");
        File.Delete(rotatedDiagnosticPath);
        Assert(markerSuccess.ExitCode == 0 && markerSuccess.StandardOutput == "MARKER_BUSINESS=ok",
            "shell success returns board exit 0 and strips its random marker");
        Assert(markerFailure.ExitCode == 1 && markerFailure.StandardOutput == "EXPECTED_REMOTE_FAILURE=1",
            "shell failure returns board exit 1 instead of adb.exe host exit 0");
        Assert(!markerSuccess.StandardOutput.Contains("__MOSQUITO_REMOTE_EXIT_", StringComparison.Ordinal) &&
               !markerFailure.StandardOutput.Contains("__MOSQUITO_REMOTE_EXIT_", StringComparison.Ordinal),
            "random shell marker is not exposed as board business output");

        var cloud = new CloudApiClient(settings);
        var outbox = new OutboxRepository(settings.LocalDataRoot);
        await outbox.InitializeAsync(default);
        var beforeIds = (await outbox.GetAllAsync(default)).Select(item => item.CaptureId).ToHashSet();
        var workflow = new CaptureWorkflow(settings, adb, new(), cloud, outbox);
        var detector = new DeviceDetectionService(settings, adb, new WindowsUsbDeviceProbe(), adb.GetVersionAsync);
        var vm = new MainViewModel(settings, workflow, cloud, outbox, detector);
        var window = new MainWindow(vm, false)
        {
            Width = 1500,
            Height = 960,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Title = "Mosquito 实板直连界面联调"
        };
        window.Show();
        var password = (System.Windows.Controls.PasswordBox)window.FindName("PasswordInput");
        var loginButton = (System.Windows.Controls.Button)window.FindName("LoginButton");
        var detectButton = (System.Windows.Controls.Button)window.FindName("DetectButton");
        var readButton = (System.Windows.Controls.Button)window.FindName("ReadDeviceButton");
        var captureButton = (System.Windows.Controls.Button)window.FindName("CaptureButton");
        vm.Username = "admin";
        password.Password = "000";
        ClickBoundButton(loginButton, vm.LoginCommand);
        await UntilReal(() => vm.IsLoggedIn && !vm.Busy && vm.HasDetectionDetails, TimeSpan.FromSeconds(30),
            "engineer login and automatic real-device detection");
        Assert(vm.Session.Kind == SessionKind.Engineer && !cloud.IsAuthenticated,
            "real direct test uses local engineer login without cloud access");
        Assert(vm.DetectionChecks.Any(check => check.Name == "ADB 通信" && check.State == DetectionState.Passed) &&
               vm.DetectionChecks.Any(check => check.Name == "板端接口" && check.State == DetectionState.Passed),
            "real board detection reports ADB communication and all three direct commands");
        Assert(vm.DetectionChecks.Any(check => check.Name == "元数据兼容性" &&
                                               check.State == DetectionState.Passed &&
                                               check.Detail.Contains("metadata v3", StringComparison.Ordinal)),
            "UI recognizes the installed image metadata v3 declaration");
        Assert(vm.DetectionChecks.Any(check => check.Name == "设备身份" &&
                                               check.Detail.Contains("dev-v5.6.4-client-direct", StringComparison.Ordinal)),
            "UI detection displays the exact v5.6.4 image identity");
        ClickBoundButton(detectButton, vm.DetectDeviceCommand);
        await UntilReal(() => vm.Busy, TimeSpan.FromSeconds(5), "manual detect button operation to start");
        await UntilReal(() => !vm.Busy, TimeSpan.FromSeconds(30), "manual detect button operation to finish");
        Assert(vm.ConnectionStatus.Contains("已连接", StringComparison.Ordinal),
            "named WPF detect button completed against the real board");
        ((System.Windows.Controls.Expander)window.FindName("DetectionDetails")).IsExpanded = true;
        await SaveWindow(window, Path.Combine(output, "01-real-device-detection.png"));

        var liveReadings = new List<string>();
        var sawPowerWarning = false;
        var sawCompleteReading = false;
        for (var attempt = 1; attempt <= 20 && (!sawPowerWarning || !sawCompleteReading); attempt++)
        {
            Assert(vm.ReadDeviceCommand.CanExecute(null), $"read-device button is enabled on attempt {attempt}");
            ClickBoundButton(readButton, vm.ReadDeviceCommand);
            await UntilReal(() => vm.Busy, TimeSpan.FromSeconds(5), "read-device operation to start");
            await UntilReal(() => !vm.Busy, TimeSpan.FromSeconds(30), "read-device operation to finish");
            liveReadings.Add(vm.LiveText);
            sawCompleteReading |= !vm.LiveText.Contains("温度：--", StringComparison.Ordinal) &&
                                  !vm.LiveText.Contains("湿度：--", StringComparison.Ordinal) &&
                                  !vm.LiveText.Contains("电池电压：--", StringComparison.Ordinal) &&
                                  vm.LiveText.Contains("环境结果：PASS", StringComparison.Ordinal) &&
                                  vm.LiveText.Contains("CRC：通过", StringComparison.Ordinal) &&
                                  (vm.LiveText.Contains("电源结果：PASS", StringComparison.Ordinal) ||
                                   vm.LiveText.Contains("电源结果：WARN", StringComparison.Ordinal)) &&
                                  System.Text.RegularExpressions.Regex.IsMatch(
                                      vm.LiveText, @"FAULT_REG：0x[0-9A-Fa-f]{2}",
                                      System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            sawPowerWarning = vm.LiveText.Contains("电源结果：WARN", StringComparison.Ordinal) &&
                              vm.LiveText.Contains("错误码：POWER_FAULT_ACTIVE", StringComparison.Ordinal) &&
                              vm.LiveText.Contains("FAULT_REG：0x80", StringComparison.Ordinal);
        }
        Assert(sawCompleteReading, "at least one real environment and power reading contains all required fields");
        Assert(sawPowerWarning, "physical board power WARN/POWER_FAULT_ACTIVE/0x80 is visible in live UI text");
        Assert(vm.StatusMessage.Contains("警告", StringComparison.Ordinal), "status bar calls out the board warning");
        await SaveWindow(window, Path.Combine(output, "02-real-live-reading.png"));

        vm.ManualFocus = 500;
        vm.UseAutofocusLock = false;
        Assert(vm.CaptureCommand.CanExecute(null), "manual capture button is enabled");
        ClickBoundButton(captureButton, vm.CaptureCommand);
        await UntilReal(() => vm.Busy, TimeSpan.FromSeconds(5), "manual capture operation to start");
        await UntilReal(() => !vm.Busy, TimeSpan.FromMinutes(4), "manual capture operation to finish");
        var artifacts = await outbox.GetAllAsync(default);
        var artifact = artifacts.Single(item => !beforeIds.Contains(item.CaptureId));
        Assert(vm.DirectPhoto.Record?.Id == artifact.CaptureId, "capture result view shows the newly saved UUID");
        Assert(vm.DirectPhoto.Image is { PixelWidth: 3264, PixelHeight: 2448 },
            "production photo preview loaded the real 3264x2448 JPEG");
        Assert(File.Exists(artifact.LocalPhotoPath) && File.Exists(artifact.LocalMetadataPath),
            "photo and metadata are both saved in the isolated Windows data directory");
        Assert(artifact.Metadata.FocusSelected == 500, "metadata focus_selected equals requested manual focus 500");
        var focus = artifact.Metadata.Sections[string.Empty];
        Assert(focus.GetValueOrDefault("focus_requested") == "500" &&
               focus.GetValueOrDefault("focus_selected") == "500" &&
               focus.GetValueOrDefault("focus_readback_before_capture") == "500" &&
               focus.GetValueOrDefault("focus_readback_after_capture") == "500" &&
               focus.GetValueOrDefault("focus_lock_verified") == "yes" &&
               focus.GetValueOrDefault("focus_auto_before_capture") == "0" &&
               focus.GetValueOrDefault("focus_auto_after_capture") == "0" &&
               focus.GetValueOrDefault("focus_min") == "1" &&
               focus.GetValueOrDefault("focus_max") == "1023" &&
               focus.GetValueOrDefault("focus_step") == "1",
            "manual metadata confirms requested/selected/readbacks/lock/auto-off and focus range");
        Assert(focus.GetValueOrDefault("environment_sample_exit") == "0" &&
               focus.GetValueOrDefault("power_sample_exit") == "0" &&
               artifact.Metadata.Sections.ContainsKey("environment") &&
               artifact.Metadata.Sections.ContainsKey("power") &&
               artifact.Metadata.Environment.Result == "PASS" &&
               artifact.Metadata.Environment.CrcOk == true &&
               artifact.Metadata.Environment.TemperatureCentiC is not null &&
               artifact.Metadata.Environment.HumidityCentiRh is not null &&
               artifact.Metadata.Environment.SampledUptimeMs is > 0 &&
               artifact.Metadata.Power.Result is "PASS" or "WARN" &&
               artifact.Metadata.Power.BatteryMv is not null &&
               artifact.Metadata.Power.FaultRegister is not null &&
               artifact.Metadata.Power.SampledUptimeMs is > 0,
            "the pulled metadata embeds complete environment and power samples from this capture invocation");
        Assert(artifact.Metadata.PhotoPath.StartsWith("/mnt/UDISK/mosquito-test/camera/", StringComparison.Ordinal),
            "metadata photo stays in the approved board camera directory");
        Assert(artifact.State == CaptureState.PendingUpload,
            "offline direct capture is saved to SQLite as PendingUpload");

        vm.DirectHistory.Range = "今天";
        await vm.DirectHistory.QueryAsync();
        Assert(vm.DirectHistory.Rows.Any(row => row.Id == artifact.CaptureId),
            "the real capture is visible in the direct SQLite history query");
        vm.Tab = 1;
        vm.DirectHistory.Selected = vm.DirectHistory.Rows.Single(row => row.Id == artifact.CaptureId);
        await UntilReal(() => vm.DirectHistory.Photo.Image is not null, TimeSpan.FromSeconds(15),
            "history photo preview to load");
        Assert(vm.DirectHistory.Photo.Image is { PixelWidth: 3264, PixelHeight: 2448 },
            "history preview reopens the same full-resolution real JPEG");
        await SaveWindow(window, Path.Combine(output, "03-real-history-preview.png"));
        vm.Tab = 0;
        ((System.Windows.Controls.Expander)((CaptureSummaryView)window.FindName("DirectCaptureSummary")).FindName("MoreDetails")).IsExpanded = true;
        await SaveWindow(window, Path.Combine(output, "04-real-manual-capture.png"));

        Assert(File.Exists(diagnosticPath), "structured ADB diagnostics were persisted");
        var diagnosticText = await File.ReadAllTextAsync(diagnosticPath);
        Assert(diagnosticText.Contains("\"hostExitCode\":0", StringComparison.Ordinal) &&
               diagnosticText.Contains("\"remoteExitCode\":1", StringComparison.Ordinal) &&
               diagnosticText.Contains("mosquito-capture --id", StringComparison.Ordinal) &&
               diagnosticText.Contains("\"operation\":\"pull\"", StringComparison.Ordinal),
            "diagnostics include host/remote exits, capture command, output and both pulls");
        var pullOperationCount = System.Text.RegularExpressions.Regex.Matches(
            diagnosticText, "\\\"operation\\\":\\\"pull\\\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        Assert(pullOperationCount == 2, "exactly two ADB pull operations fetched the photo and metadata");
        Assert(!diagnosticText.Contains("__MOSQUITO_REMOTE_EXIT_", StringComparison.Ordinal),
            "diagnostic business output does not retain private random markers");
        Assert(redactionProbe.StandardOutput.Contains(diagnosticSecret, StringComparison.Ordinal) &&
               !diagnosticText.Contains(diagnosticSecret, StringComparison.Ordinal) &&
               diagnosticText.Contains("access_token=[REDACTED]", StringComparison.Ordinal),
            "diagnostic redaction does not alter command results but removes token values from disk");

        var captureLogLine = diagnosticText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement.GetProperty("remoteCommand").ValueKind == JsonValueKind.String &&
                                document.RootElement.GetProperty("remoteCommand").GetString()!.Contains("mosquito-capture --id", StringComparison.Ordinal));
        string captureOutputPhoto;
        string captureOutputMetadata;
        string captureOutputResult;
        int captureRemoteExit;
        using (captureLogLine)
        {
            var stdout = captureLogLine.RootElement.GetProperty("standardOutput").GetString() ?? "";
            captureOutputResult = RequiredOutputMarker(stdout, "CAPTURE_RESULT");
            var captureOutputId = RequiredOutputMarker(stdout, "CAPTURE_ID");
            captureOutputPhoto = RequiredOutputMarker(stdout, "PHOTO");
            captureOutputMetadata = RequiredOutputMarker(stdout, "METADATA");
            Assert(captureOutputResult == artifact.Metadata.RecordStatus &&
                   Guid.Parse(captureOutputId) == artifact.CaptureId &&
                   captureOutputPhoto == artifact.Metadata.PhotoPath &&
                   captureOutputMetadata.StartsWith("/mnt/UDISK/mosquito-test/camera/", StringComparison.Ordinal) &&
                   captureOutputMetadata.EndsWith(".txt", StringComparison.Ordinal),
                "capture stdout result, UUID, photo and metadata markers uniquely match the saved artifact");
            captureRemoteExit = captureLogLine.RootElement.GetProperty("remoteExitCode").GetInt32();
            Assert((captureRemoteExit == 0 && artifact.Metadata.RecordStatus == "COMPLETE") ||
                   (captureRemoteExit == 10 && artifact.Metadata.RecordStatus == "PARTIAL"),
                "remote exit maps exactly to COMPLETE/PARTIAL");
        }

        var resultPath = Path.Combine(output, "REAL_DIRECT_RESULT.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
        {
            result = "PASS",
            testedAtUtc = DateTimeOffset.UtcNow,
            deviceSerial = settings.DeviceSerial,
            configuredBoardCommandPath = settings.BoardCommandPath,
            identityProbeExit = identityProbe.ExitCode,
            imageVersion,
            buildDate,
            boardPackage,
            sourceState,
            metadataDeclaration,
            commandPaths,
            detectionSummary = vm.ConnectionStatus,
            detectionChecks = vm.DetectionChecks,
            markerSuccessExit = markerSuccess.ExitCode,
            markerFailureExit = markerFailure.ExitCode,
            diagnosticRotation = "PASS",
            diagnosticRedaction = "PASS",
            liveReadAttempts = liveReadings.Count,
            powerWarningVisible = sawPowerWarning,
            powerWarningObservation = sawPowerWarning ? "OBSERVED" : "NOT_OBSERVED",
            liveReadings,
            captureId = artifact.CaptureId,
            requestedFocus = 500,
            focusSelected = artifact.Metadata.FocusSelected,
            recordStatus = artifact.Metadata.RecordStatus,
            captureRemoteExit,
            captureOutputResult,
            captureOutputPhoto,
            captureOutputMetadata,
            pullOperationCount,
            state = artifact.State,
            artifact.LocalPhotoPath,
            artifact.LocalMetadataPath,
            artifact.PhotoBytes,
            artifact.MetadataBytes,
            jpegWidth = artifact.Metadata.JpegWidth,
            jpegHeight = artifact.Metadata.JpegHeight,
            artifact.PhotoSha256,
            artifact.MetadataSha256,
            captureEnvironment = artifact.Metadata.Environment,
            capturePower = artifact.Metadata.Power,
            environmentSampleExit = focus.GetValueOrDefault("environment_sample_exit"),
            powerSampleExit = focus.GetValueOrDefault("power_sample_exit"),
            sqliteHistoryVisible = vm.DirectHistory.Rows.Any(row => row.Id == artifact.CaptureId),
            currentPreview = vm.DirectPhoto.Message,
            historyPreview = vm.DirectHistory.Photo.Message,
            autofocusTested = false,
            autofocusNote = "Camera is not mechanically fixed; autofocus remains an accepted open item."
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        }));
        Console.WriteLine(await File.ReadAllTextAsync(resultPath));
        window.Close();
    }

    private static string RequiredOutputMarker(string output, string name)
    {
        var prefix = name + "=";
        var values = output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..])
            .ToArray();
        Assert(values.Length == 1, $"capture output contains exactly one {name} marker");
        return values[0];
    }

    private static void ClickBoundButton(System.Windows.Controls.Button button, System.Windows.Input.ICommand expectedCommand)
    {
        Assert(ReferenceEquals(button.Command, expectedCommand), $"{button.Name} XAML binding resolves to the expected command");
        Assert(button.IsEnabled && expectedCommand.CanExecute(button.CommandParameter), $"{button.Name} is enabled");
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        var invoke = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
            as System.Windows.Automation.Provider.IInvokeProvider
            ?? throw new InvalidOperationException($"{button.Name} does not expose the UI Automation Invoke pattern");
        invoke.Invoke();
    }

    private static async Task UntilReal(Func<bool> condition, TimeSpan timeout, string operation)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for {operation} after {timeout.TotalSeconds:F0} seconds.");
    }

    private static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var data = Path.Combine(output, "fixture-data"); Directory.CreateDirectory(data);
        var photoPath = Path.Combine(data, "test-photo.jpg"); WritePhoto(photoPath);
        var bytes = await File.ReadAllBytesAsync(photoPath);
        var now = DateTimeOffset.UtcNow;
        var handler = new FixtureHandler(now);
        var settings = new AppSettings { ApiBaseUrl = "http://fixture.invalid", DeviceId = "MQ-SH-001", DeviceSerial = "TEST-ADB", LocalDataRoot = data };
        var cloud = new CloudApiClient(settings, handler);
        var outbox = new OutboxRepository(data); await outbox.InitializeAsync(default);
        var adb = new NoHardware();
        var vm = new MainViewModel(settings, new(settings, adb, new(), cloud, outbox), cloud, outbox, new FixtureDetector());
        var window = new MainWindow(vm, false) { Width = 1500, Height = 960, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Title = "UI 验证 · 模拟数据" };
        var bindingErrors = new ErrorTrace(); PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        window.Show();
        await LoginDetectionTests.RunAsync(output, photoPath);
        vm.Username = "test"; vm.Password = "test"; vm.LoginCommand.Execute(null);
        await Until(() => vm.IsLoggedIn && !vm.Busy);
        var offlineCloud = new CloudApiClient(settings, handler);
        var offlineCapture = await new CaptureWorkflow(settings, new CaptureFixture(photoPath), new(), offlineCloud, outbox)
            .CaptureAndUploadAsync(UploadRoute.Windows, 500, null, default, upload: false);
        Assert(!offlineCloud.IsAuthenticated && offlineCapture.State == CaptureState.PendingUpload && File.Exists(offlineCapture.LocalPhotoPath), "direct capture persists locally without cloud login");
        var engineer = new MainViewModel(settings, new(settings, new CaptureFixture(photoPath), new(), offlineCloud, outbox), offlineCloud, outbox, new FixtureDetector());
        engineer.Username = "admin"; engineer.Password = "000"; engineer.LoginCommand.Execute(null);
        await Until(() => engineer.IsLoggedIn && !engineer.Busy && engineer.HasDetectionDetails);
        engineer.CaptureCommand.Execute(null);
        await Until(() => !engineer.Busy && engineer.DirectPhoto.Record is not null);
        var savedId = engineer.DirectPhoto.Record!.Id;
        engineer.LogoutCommand.Execute(null);
        Assert(engineer.IsLoggedOut && !engineer.DirectPhoto.HasImage && (await outbox.GetAllAsync(default)).Any(x => x.CaptureId == savedId), "engineer command captures through ADB workflow and logout preserves the local artifact");
        engineer.Close();
        var warningCloud = new CloudApiClient(settings, handler);
        var warningAdb = new LiveWarningFixture();
        var warningVm = new MainViewModel(settings, new(settings, warningAdb, new(), warningCloud, outbox), warningCloud, outbox, new FixtureDetector());
        warningVm.Username = "admin"; warningVm.Password = "000"; warningVm.LoginCommand.Execute(null);
        await Until(() => warningVm.IsLoggedIn && !warningVm.Busy);
        warningVm.ReadDeviceCommand.Execute(null);
        await Until(() => !warningVm.Busy && warningVm.IsLiveReadingExpanded);
        Assert(warningVm.LiveText.Contains("电源结果：WARN") &&
               warningVm.LiveText.Contains("错误码：POWER_FAULT_ACTIVE") &&
               warningVm.LiveText.Contains("FAULT_REG：0x80") &&
               warningVm.StatusMessage.Contains("警告"),
            "power WARN with remote exit 0 remains visible in live UI text and status");
        warningVm.Close();
        await vm.DirectPhoto.LoadAsync(LocalHistory.FromArtifact(offlineCapture));
        await SaveWindow(window, Path.Combine(output, "direct-workspace.png"));
        AssertSinglePhoto(window, "direct workspace");
        window.Width = 1180; window.Height = 800;
        await SaveWindow(window, Path.Combine(output, "direct-minimum-window.png"));
        var captureButton = (System.Windows.Controls.Button)window.FindName("CaptureButton");
        var readButton = (System.Windows.Controls.Button)window.FindName("ReadDeviceButton");
        var resultsScroll = (System.Windows.Controls.ScrollViewer)window.FindName("DirectResultsScroll");
        var summary = (CaptureSummaryView)window.FindName("DirectCaptureSummary");
        ((System.Windows.Controls.Expander)summary.FindName("MoreDetails")).IsExpanded = true;
        window.UpdateLayout();
        var capturePosition = captureButton.TranslatePoint(new Point(), window);
        var readPosition = readButton.TranslatePoint(new Point(), window);
        resultsScroll.ScrollToBottom();
        await SaveWindow(window, Path.Combine(output, "direct-details-minimum.png"));
        Assert(resultsScroll.VerticalOffset > 0 && captureButton.TranslatePoint(new Point(), window) == capturePosition && readButton.TranslatePoint(new Point(), window) == readPosition,
            "scrolling long results keeps direct capture and read controls fixed at minimum window size");
        Assert(captureButton.IsVisible && readButton.IsVisible && capturePosition.Y + captureButton.ActualHeight < window.ActualHeight - 38,
            "direct actions fit inside minimum workspace");
        ((System.Windows.Controls.Expander)window.FindName("DetectionDetails")).IsExpanded = true;
        await SaveWindow(window, Path.Combine(output, "direct-detection-minimum.png"));
        Assert(resultsScroll.ViewportHeight >= 80, "expanded detection leaves usable scrolling space for capture results");
        ((System.Windows.Controls.Expander)window.FindName("DetectionDetails")).IsExpanded = false;
        ((System.Windows.Controls.Expander)summary.FindName("MoreDetails")).IsExpanded = false;
        resultsScroll.ScrollToTop();
        window.Width = 1500; window.Height = 960;
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        Assert(!vm.CaptureCommand.CanExecute(null) && !vm.ReadDeviceCommand.CanExecute(null), "remote mode has no capture/read controls");
        vm.SelectedDevice = vm.Devices.First();
        await vm.RemotePhoto.LoadAsync(handler.Records[0] with { LocalPhotoPath = photoPath });
        await SaveWindow(window, Path.Combine(output, "remote-workspace.png"));
        AssertSinglePhoto(window, "remote map workspace");
        var remoteDevice = vm.SelectedDevice;
        var remotePhoto = vm.RemotePhoto.Record;
        var remoteImage = vm.RemotePhoto.Image;
        vm.RemoteView = 1;
        await SaveWindow(window, Path.Combine(output, "remote-image-workspace.png"));
        AssertSinglePhoto(window, "remote image workspace");
        Assert(vm.SelectedDevice == remoteDevice && ReferenceEquals(vm.RemotePhoto.Record, remotePhoto) && ReferenceEquals(vm.RemotePhoto.Image, remoteImage),
            "switching map to image reuses the selected device, record and loaded image");
        Assert(!((FrameworkElement)window.FindName("RemotePreview")).IsVisible, "main image view hides duplicate remote preview");
        window.Width = 1180; window.Height = 800;
        await SaveWindow(window, Path.Combine(output, "remote-image-minimum.png"));
        vm.RemoteView = 0;
        await SaveWindow(window, Path.Combine(output, "remote-map-minimum.png"));
        AssertSinglePhoto(window, "remote map restored at minimum window size");
        Assert(((FrameworkElement)window.FindName("RemotePreview")).IsVisible, "returning to map restores remote photo preview");
        window.Width = 1500; window.Height = 960;
        vm.District = "浦东新区";
        await SaveWindow(window, Path.Combine(output, "remote-district.png"));
        vm.Tab = 1; vm.RecordTab = 0;
        vm.RemoteHistory.Range = "近 7 天"; await vm.RemoteHistory.QueryAsync();
        Assert(vm.RemoteHistory.Rows.Count == 50 && vm.RemoteHistory.PageLabel.Contains("553"), "real WPF history binding shows 553-record query paginated at 50");
        vm.RemoteHistory.Selected = vm.RemoteHistory.Rows[3];
        await vm.RemoteHistory.Photo.LoadAsync(handler.Records[3] with { LocalPhotoPath = photoPath });
        await SaveWindow(window, Path.Combine(output, "history-records.png"));
        foreach (var grid in VisualChildren<System.Windows.Controls.DataGrid>(window))
            Assert(grid.Columns.All(c => c.ActualWidth >= 85), "history columns measured without clipping");
        window.Width = 1180; window.Height = 800;
        await SaveWindow(window, Path.Combine(output, "history-minimum-window.png"));
        window.Width = 1500; window.Height = 960;
        var selected = vm.RemoteHistory.Selected;
        vm.Tab = 0; vm.RemoteView = 1; vm.Tab = 1;
        Assert(vm.RemoteHistory.Selected == selected, "navigation retains selected history record");
        vm.RemoteHistory.NextCommand.Execute(null);
        vm.RemoteHistory.Selected = vm.RemoteHistory.Rows[1];
        var modeSelection = vm.RemoteHistory.Selected.Id;
        var page = vm.RemoteHistory.PageLabel;
        vm.DirectModeCommand.Execute(null); await Until(() => !vm.IsRemote);
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        Assert(vm.RemoteHistory.PageLabel == page && vm.RemoteHistory.Range == "近 7 天", "mode switches preserve history page and filters");
        Assert(vm.RemoteHistory.Selected?.Id == modeSelection, "mode switches preserve selected row");
        handler.Fail = true;
        var before = vm.RemoteHistory.Rows.Select(x => x.Id).ToArray();
        await vm.RemoteHistory.QueryAsync();
        Assert(before.SequenceEqual(vm.RemoteHistory.Rows.Select(x => x.Id)) && vm.RemoteHistory.Message.Contains("保留"), "network failure retains history snapshot");
        handler.Fail = false;
        vm.RecordTab = 1; await vm.Reports.QueryAsync();
        vm.Reports.Selected = vm.Reports.Rows.First(x => x.EffectiveState == ReportState.Overdue);
        Assert(!vm.Reports.ViewRecordCommand.CanExecute(null), "missing slot has no photo action");
        await SaveWindow(window, Path.Combine(output, "report-checks.png"));
        handler.Repair = true; await vm.Reports.PollAsync();
        Assert(vm.Reports.Selected?.SlotId == "a" && vm.Reports.Selected.EffectiveState == ReportState.Late && vm.Reports.ViewRecordCommand.CanExecute(null), "late repair updates original slot and preserves selection");
        vm.DirectModeCommand.Execute(null); await Until(() => !vm.IsRemote);
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        Assert(vm.RecordTab == 1 && vm.Reports.Selected?.SlotId == "a", "remote report tab and selection survive mode switch");
        var checkedAt = vm.Reports.CheckedLabel; handler.Fail = true; await vm.Reports.QueryAsync();
        Assert(vm.Reports.CheckedLabel == checkedAt && vm.Reports.Rows.Count > 0 && vm.Reports.Message.Contains("暂停"), "network failure retains check timestamp and rows");
        await SaveWindow(window, Path.Combine(output, "report-checks-offline.png")); handler.Fail = false;
        vm.Reports.Selected = vm.Reports.Rows.First(x => x.CanViewRecord);
        vm.Reports.ViewRecordCommand.Execute(null); await Until(() => vm.RecordTab == 0 && !vm.RemoteHistory.Busy);
        Assert(vm.RemoteHistory.Selected is not null, "report opens associated history record");

        // Two in-flight detail requests complete out of order. Only the newest selection may win.
        var gate = new TaskCompletionSource();
        var first = handler.Records[0]; var second = handler.Records[1];
        var raceApi = new CloudApiClient(settings, new AsyncHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(first.Id.ToString("D"))) await gate.Task;
            return FixtureHandler.Json(new CloudCaptureDetail(request.RequestUri.AbsolutePath.EndsWith(first.Id.ToString("D")) ? first : second, "http://fixture.invalid/photo.jpg", now.AddHours(1)));
        }));
        await raceApi.LoginAsync("test", "test", default);
        var photoAccess = new ClientSession(raceApi); photoAccess.Set(SessionKind.Cloud, "test");
        var photo = new PhotoViewModel(raceApi, photoAccess, (_, _) => Task.FromResult(bytes));
        var slow = photo.LoadAsync(first); var fast = photo.LoadAsync(second); await fast; gate.SetResult(); await slow;
        Assert(photo.Record?.Id == second.Id && photo.HasImage, "late detail response cannot replace newer selected image");
        Assert(adb.Calls == 0, "UI tests never touch real board or ADB");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(bindingErrors.Errors.Count == 0, "no WPF binding errors: " + string.Join("\n", bindingErrors.Errors));
        window.Close(); SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(output, "RESULTS.txt"), "PASS: WPF bindings, single workspace photo, fixed direct controls with scrolling results at minimum size, map/image selection preservation, 553-record pagination, mode/navigation state, offline preservation, report drill-through, missing-photo gating, out-of-order image responses.\nAll screenshots use simulated records and a generated TEST PHOTO. No board or real cloud calls.\n");
        Console.WriteLine("PASS: WPF interactions and screenshots: " + output);
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("UI operation did not complete.");
    }
    private static void AssertSinglePhoto(Window window, string context) =>
        Assert(VisualChildren<System.Windows.Controls.Image>(window).Count(x => x.IsVisible && x.Source is not null) == 1,
            context + " displays exactly one photo without duplicate preview");
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var next in VisualChildren<T>(child)) yield return next;
        }
    }
    private static async Task SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(50);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
    private static void WritePhoto(string path)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(205, 218, 213)), null, new Rect(0, 0, 3264, 2448));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(230, 237, 231)), new Pen(Brushes.SlateGray, 8), new Rect(420, 300, 2424, 1848), 50, 50);
            dc.DrawText(new FormattedText("TEST PHOTO / 模拟采集影像", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 80, Brushes.DarkSlateGray, 1), new Point(820, 1050));
            dc.DrawText(new FormattedText("仅用于界面验证，不代表实机采集结果", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 54, Brushes.SlateGray, 1), new Point(910, 1220));
        }
        var bitmap = new RenderTargetBitmap(3264, 2448, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ErrorTrace : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:") == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class NoHardware : IAdbClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken token) { Calls++; return Task.FromResult<IReadOnlyList<AdbDevice>>([]); }
        public Task<CommandResult> ShellAsync(string serial, string command, CancellationToken token) => throw new InvalidOperationException("Hardware calls forbidden in UI fixture");
        public Task<CommandResult> PullAsync(string serial, string remote, string local, CancellationToken token) => throw new InvalidOperationException("Hardware calls forbidden in UI fixture");
    }
    private sealed class FixtureDetector : IDeviceDetectionService
    {
        public Task<DeviceDetectionResult> DetectAsync(CancellationToken token) => Task.FromResult(new DeviceDetectionResult("已连接 · 检测通过（模拟）",
            [new("ADB 组件", DetectionState.Passed, "模拟通信组件"), new("USB 驱动", DetectionState.Passed, "模拟 WinUSB 设备"),
             new("板端接口", DetectionState.Passed, "模拟设备 · 采集 / 环境 / 电源命令存在")], true, true, "MQ-SH-001", "模拟镜像"));
    }
    private sealed class CaptureFixture(string sourcePhoto) : IAdbClient
    {
        private Guid _captureId;
        public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AdbDevice>>([new("TEST-ADB", "device", "fixture")]);
        public Task<CommandResult> ShellAsync(string serial, string command, CancellationToken token)
        {
            _captureId = Guid.Parse(command.Split("--id ")[1].Split(' ')[0]);
            return Task.FromResult(new CommandResult(10,
                $"CAPTURE_RESULT=PARTIAL\nCAPTURE_ID={_captureId:D}\nPHOTO=/mnt/UDISK/mosquito-test/camera/photo.jpg\nMETADATA=/mnt/UDISK/mosquito-test/camera/metadata.txt",
                "", TimeSpan.Zero));
        }
        public async Task<CommandResult> PullAsync(string serial, string remote, string local, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            if (remote.EndsWith(".jpg")) File.Copy(sourcePhoto, local, true);
            else
            {
                var bytes = await File.ReadAllBytesAsync(sourcePhoto, token);
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                await File.WriteAllTextAsync(local, $"metadata_version=3\ncapture_id={_captureId:D}\nrecord_status=PARTIAL\nphoto=/mnt/UDISK/mosquito-test/camera/photo.jpg\nsystem_time_valid=no\nmode=manual-focus\nfocus_min=1\nfocus_max=1023\nfocus_step=1\nfocus_requested=500\nfocus_selected=500\nfocus_readback_before_capture=500\nfocus_readback_after_capture=500\nfocus_lock_verified=yes\nfocus_auto_before_capture=0\nfocus_auto_after_capture=0\njpeg_width=3264\njpeg_height=2448\njpeg_bytes={bytes.Length}\njpeg_sha256={sha}\n[environment]\nRESULT=PASS\nERROR_CODE=NONE\nTEMPERATURE_CENTI_C=2635\nHUMIDITY_CENTI_RH=6130\n[power]\nRESULT=FAIL\nERROR_CODE=NO_SAMPLE\n", token);
            }
            return new(0, "", "", TimeSpan.Zero);
        }
    }
    private sealed class LiveWarningFixture : IAdbClient
    {
        public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AdbDevice>>([new("TEST-ADB", "device", "fixture")]);
        public Task<CommandResult> ShellAsync(string serial, string command, CancellationToken token)
        {
            var output = command.Contains("mosquito-environment", StringComparison.Ordinal)
                ? "RESULT=PASS\nERROR_CODE=NONE\nTEMPERATURE_CENTI_C=2635\nHUMIDITY_CENTI_RH=6130\nCRC_OK=1\nSAMPLED_UPTIME_MS=1000"
                : "RESULT=WARN\nERROR_CODE=POWER_FAULT_ACTIVE\nBATTERY_MV=4124\nCHARGE_STATE=CHARGE_DONE\nVBUS_GOOD=1\nVBUS_MV=4900\nCHARGE_CURRENT_MA=0\nFAULT_REG=0x80\nSAMPLED_UPTIME_MS=1010";
            return Task.FromResult(new CommandResult(0, output, "", TimeSpan.Zero));
        }
        public Task<CommandResult> PullAsync(string serial, string remote, string local, CancellationToken token) =>
            throw new InvalidOperationException("Live warning fixture does not pull files");
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => request.RequestUri!.AbsolutePath == "/api/auth/login" ? Task.FromResult(FixtureHandler.Json(new { accessToken = "fixture", expiresAt = DateTimeOffset.UtcNow.AddHours(1) })) : response(request);
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly DateTimeOffset _now;
        public bool Fail { get; set; }
        public bool Repair { get; set; }
        public CloudCaptureRecord[] Records { get; }
        public FixtureHandler(DateTimeOffset now)
        {
            _now = now;
            Records = Enumerable.Range(0, 553).Select(i => new CloudCaptureRecord(Guid.NewGuid(), "TEST-ADB", i % 7 == 0 ? "Partial" : "Complete", "BOARD_4G", now.AddMinutes(-i), true, 2458000, 1800,
                new("PASS", "NONE", 2635 + i % 70, 6130 + i % 50, true, 2000), new("PASS", "NONE", 4124 - i % 30, "NOT_CHARGING", false, null, null, null, 2000), null)
                { DeviceId = $"MQ-SH-{i % 3 + 1:000}", TriggerSource = "SCHEDULED", TimeSource = "BOARD", ReceivedAtUtc = now.AddMinutes(-i + 1), Location = new(31.211 + i % 3 * .01, 121.562 + i % 3 * .01, now.AddMinutes(-i), "浦东新区", "GCJ02") }).ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(new { accessToken = "fixture", expiresAt = _now.AddHours(1) }));
            if (Fail) throw new HttpRequestException("模拟云端连接中断");
            var path = request.RequestUri.AbsolutePath;
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            var offset = query.TryGetValue("cursor", out var cursor) ? int.Parse(cursor) : 0;
            if (path == "/api/v2/captures")
            {
                var filtered = Records.Where(x => (!query.TryGetValue("deviceId", out var device) || x.DeviceId == device) && x.CapturedAtUtc >= DateTimeOffset.Parse(query["from"]) && x.CapturedAtUtc < DateTimeOffset.Parse(query["toExclusive"])).ToArray();
                return Task.FromResult(Json(new CapturePage(filtered.Skip(offset).Take(50).ToArray(), offset + 50 < filtered.Length ? (offset + 50).ToString() : null, "fixture", true)));
            }
            if (path == "/api/v2/devices") return Task.FromResult(Json(new DevicePage(Records.Take(3).Select(x => new RemoteDevice(x.DeviceId!, x.DeviceSerial, x.Location, x.Id)).ToArray(), null, "fixture", true)));
            if (path == "/api/v2/report-checks")
            {
                var anchor = _now.AddMinutes(-120);
                var checks = new[]
                {
                    new ReportCheck("a", "MQ-SH-001", anchor, Repair ? ReportState.Late : ReportState.Overdue, true, anchor.AddDays(-1), true, Repair ? Records[3].Id : null, Repair ? anchor.AddMinutes(19) : null, Repair ? "Complete" : null),
                    new ReportCheck("b", "MQ-SH-002", anchor, ReportState.Late, true, anchor.AddDays(-1), true, Records[1].Id, anchor.AddMinutes(19), "Complete"),
                    new ReportCheck("c", "MQ-SH-003", anchor, ReportState.OnTime, true, anchor.AddDays(-1), true, Records[2].Id, anchor.AddMinutes(3), "Partial"),
                    new ReportCheck("d", "MQ-SH-004", anchor, ReportState.Pending, false, null, false, null, null, null),
                    new ReportCheck("e", "MQ-SH-001", anchor.AddHours(2), ReportState.Waiting, true, anchor.AddDays(-1), true, null, null, null)
                }.Select(x => x with { ScheduleAnchorUtc = anchor, IntervalMinutes = 60, District = "浦东新区" }).Where(x => !query.TryGetValue("deviceId", out var device) || x.DeviceId == device).ToArray();
                return Task.FromResult(Json(new ReportPage(checks, null, "fixture", _now, true)));
            }
            if (Guid.TryParse(path.Split('/').Last(), out var id)) return Task.FromResult(Json(new CloudCaptureDetail(Records.First(x => x.Id == id), null, null)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }) };
    }
}
