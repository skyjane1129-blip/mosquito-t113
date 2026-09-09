using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mosquito.Client;
using Mosquito.Client.Core;

internal static class LoginDetectionTests
{
    public static async Task RunAsync(string output, string photoPath)
    {
        await DetectionScenarios();
        var settings = new AppSettings { ApiBaseUrl = "http://auth-fixture.invalid", LocalDataRoot = Path.Combine(output, "auth-data"), DeviceId = "MQ-TEST-001" };
        var handler = new AuthHandler(); var cloud = new CloudApiClient(settings, handler);
        var outbox = new OutboxRepository(settings.LocalDataRoot); await outbox.InitializeAsync(default);
        var board = new ProbeAdb(); var detector = new CountingDetector();
        var vm = new MainViewModel(settings, new(settings, board, new(), cloud, outbox), cloud, outbox, detector);
        var window = new MainWindow(vm, false) { Width = 1180, Height = 800, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        var password = (PasswordBox)window.FindName("PasswordInput");
        var account = (TextBox)window.FindName("AccountInput");
        await vm.InitializeAsync(); await vm.PollAsync(); await vm.TickAsync(); await vm.RefreshDeviceAsync();
        await vm.DirectHistory.QueryAsync(); await vm.RemoteHistory.QueryAsync(); await vm.Reports.QueryAsync();
        vm.DeviceChanged(); vm.CaptureCommand.Execute(null); vm.ReadDeviceCommand.Execute(null);
        Assert(vm.IsLoggedOut && board.Calls == 0 && detector.Calls == 0 && handler.Calls == 0, "locked startup performs no board, cloud or history work");
        Assert(!vm.LoginCommand.CanExecute(null) && account.Text == "" && password.Password == "" && !vm.DirectHistory.QueryCommand.CanExecute(null), "blank login and all work commands locked");
        await Save(window, Path.Combine(output, "login-locked-minimum.png"));
        AssertHeaderFits(window, "LoginPanel");
        vm.Username = "admin"; password.Password = "bad"; vm.LoginCommand.Execute(null);
        await Until(() => !vm.Busy);
        Assert(vm.IsLoggedOut && vm.LoginMessage.Contains("错误") && handler.Calls == 0, "reserved local account rejects wrong password without a cloud call");
        password.Password = "000"; vm.LoginCommand.Execute(null);
        await Until(() => vm.IsLoggedIn && !vm.Busy && detector.Calls > 0);
        Assert(vm.Session.Kind == SessionKind.Engineer && !cloud.IsAuthenticated && password.Password.Length == 0, "engineer login is offline and clears real PasswordBox");
        Assert(vm.CaptureCommand.CanExecute(null) && vm.ReadDeviceCommand.CanExecute(null) && !vm.RetryCommand.CanExecute(null), "engineer can operate hardware; cloud upload stays gated");
        vm.DetectDeviceCommand.Execute(null); await Until(() => !vm.Busy);
        Assert(detector.Calls == 2 && vm.HasDetectionDetails, "manual detection reuses the automatic detection service");
        vm.DeviceChanged(); await Task.Delay(850); await vm.TickAsync();
        Assert(detector.Calls == 3, "USB change notification triggers debounced detection after login");
        detector.Delay = true; vm.DetectDeviceCommand.Execute(null);
        Assert(vm.Busy && !vm.LogoutCommand.CanExecute(null) && !vm.RemoteModeCommand.CanExecute(null), "in-flight direct work gates logout and mode switching");
        vm.CancelCommand.Execute(null); await Until(() => !vm.Busy);
        Assert(vm.ConnectionStatus.Contains("未完成") && vm.DetectionButtonText == "检测设备", "cancelled detection exits the running state");
        detector.Delay = false; await vm.RefreshDeviceAsync();
        ((Expander)window.FindName("DetectionDetails")).IsExpanded = true;
        await Save(window, Path.Combine(output, "engineer-device-check-minimum.png")); AssertHeaderFits(window, "ModePanel");
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        await vm.RemoteHistory.QueryAsync(); await vm.Reports.QueryAsync(); await vm.PollAsync();
        Assert(handler.Calls == 0 && vm.RegistryMessage.Contains("工程师") && !vm.RefreshRemoteCommand.CanExecute(null), "engineer remote view makes no real cloud requests");
        await Save(window, Path.Combine(output, "engineer-remote-guide.png"));
        vm.LogoutCommand.Execute(null);
        Assert(vm.IsLoggedOut && vm.DetectionChecks.Count == 0 && vm.Username == "" && password.Password == "", "logout clears screen and credentials");

        vm.Username = "cloud-test"; password.Password = "wrong"; vm.LoginCommand.Execute(null); await Until(() => !vm.Busy);
        Assert(vm.IsLoggedOut && vm.LoginMessage.Contains("错误"), "cloud 401 keeps workspace locked");
        handler.FailLogin = true; password.Password = "test-pass"; vm.LoginCommand.Execute(null); await Until(() => !vm.Busy);
        Assert(vm.IsLoggedOut && vm.LoginMessage.Contains("不可用"), "cloud network failure never grants engineer access");
        handler.FailLogin = false;
        await Login(vm, password);
        Assert(vm.Session.CanUseCloud && vm.IsDirect && password.Password == "", "real API login flow unlocks cloud and starts direct");
        await Save(window, Path.Combine(output, "cloud-login-direct.png"));
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        handler.FailData = true; await vm.RemoteHistory.QueryAsync();
        Assert(vm.IsLoggedIn && cloud.IsAuthenticated, "temporary network loss preserves unexpired authentication"); handler.FailData = false;

        // A backend which ignores cancellation must still never repopulate a logged-out session.
        handler.HoldData = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.RemoteHistory.QueryAsync(); await Until(() => handler.DataWaiting);
        vm.LogoutCommand.Execute(null);
        handler.HoldData.SetResult(); await pending; handler.HoldData = null;
        Assert(vm.IsLoggedOut && vm.RemoteHistory.Rows.Count == 0 && vm.RemoteHistory.Photo.Record is null && !vm.RemoteHistory.Busy, "late history response cannot restore logout data");
        await Login(vm, password);
        vm.RemoteModeCommand.Execute(null); await Until(() => vm.IsRemote && !vm.Busy);
        handler.Unauthorized = true; await vm.RemoteHistory.QueryAsync();
        Assert(vm.IsLoggedOut && !cloud.IsAuthenticated && vm.LoginMessage.Contains("过期"), "authorized API 401 immediately locks the full workspace");
        handler.Unauthorized = false;
        handler.Lifetime = TimeSpan.FromMilliseconds(150);
        await Login(vm, password); await Task.Delay(200); vm.CheckSession();
        Assert(vm.IsLoggedOut && vm.LoginMessage.Contains("过期"), "token expiry locks without waiting for an API request");
        await Save(window, Path.Combine(output, "cloud-login-expired.png")); AssertHeaderFits(window, "LoginPanel");

        // Logout also clears downloaded/local images and already-open original-size viewers.
        vm.Username = "admin"; password.Password = "000"; vm.LoginCommand.Execute(null); await Until(() => vm.IsLoggedIn && !vm.Busy);
        var record = new CloudCaptureRecord(Guid.NewGuid(), "TEST", "Partial", "WINDOWS", DateTimeOffset.UtcNow, true, 1, 1, null, null, null) { LocalPhotoPath = photoPath };
        await vm.DirectPhoto.LoadAsync(record);
        Assert(vm.DirectPhoto.HasImage, "engineer can read a real local image");
        vm.LogoutCommand.Execute(null); Assert(!vm.DirectPhoto.HasImage && vm.DirectPhoto.Record is null, "logout clears image bytes and record");
        window.Close();
        await File.WriteAllTextAsync(Path.Combine(output, "LOGIN_DETECTION_RESULTS.txt"), "PASS: locked startup; local and cloud login; wrong/empty credentials; local hardware permissions; password clear; manual/automatic detection; engineer cloud gating; network failure; delayed response after logout; HTTP 401; expiry; minimum header layout; device detection failure layers. All data/hardware are fixtures.\n");
    }

    private static async Task Login(MainViewModel vm, PasswordBox password)
    {
        vm.Username = "cloud-test"; password.Password = "test-pass"; vm.LoginCommand.Execute(null);
        await Until(() => vm.IsLoggedIn && !vm.Busy);
    }
    private static async Task DetectionScenarios()
    {
        var settings = new AppSettings { DeviceSerial = "BOARD", DeviceId = "MQ-001" };
        var adb = new ProbeAdb(); var usb = new ProbeUsb();
        var detector = new DeviceDetectionService(settings, adb, usb, _ => Task.FromResult("ADB fixture"));
        var result = await new DeviceDetectionService(settings, adb, usb, _ => throw new FileNotFoundException("adb.exe")).DetectAsync(default);
        Assert(usb.Calls == 1 && adb.Calls == 0 && result.Summary.Contains("通信组件") && result.Summary.Contains("USB"), "PnP detects a board independently of missing adb");
        usb.Boards = []; adb.Devices = []; result = await detector.DetectAsync(default);
        Assert(result.Summary == "未发现目标设备", "unplugged board is distinct from missing tools");
        usb.Boards = [new(@"USB\VID_18D1&PID_D002\BOARD", "Test board", "", 28)];
        result = await detector.DetectAsync(default);
        Assert(result.Checks.Any(x => x.Name == "USB 驱动" && x.State == DetectionState.Warning), "driver problem is retained independently");
        adb.Devices = [new("BOARD", "offline", "")]; result = await detector.DetectAsync(default);
        Assert(result.Summary.Contains("offline") && !result.Connected, "offline handshake reported");
        adb.Devices = [new("BOARD", "device", "")]; adb.Missing = true; result = await detector.DetectAsync(default);
        Assert(result.Connected && !result.CaptureReady && result.Summary.Contains("接口未就绪"), "ADB online does not imply installed capture commands");
        adb.Missing = false; result = await detector.DetectAsync(default);
        Assert(result.Connected && result.CaptureReady && result.Version == "fixture-v1" && result.DeviceId == "MQ-001", "read-only capability and identity probe");
        Assert(!adb.LastCommand.Contains("--machine") && !adb.LastCommand.Contains("--focus") && !adb.LastCommand.Contains("reboot"), "detection does not sample, capture or reboot");
        adb.Devices = [new("BOARD", "device", ""), new("BOARD", "device", "")]; var calls = adb.Calls;
        result = await detector.DetectAsync(default);
        Assert(!result.Connected && adb.Calls == calls + 1 && result.Summary.Contains("需确认"), "ambiguous serial cannot trigger a shell command");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await detector.DetectAsync(cancel.Token); throw new InvalidOperationException("cancellation missing"); }
        catch (OperationCanceledException) { }
    }
    private static async Task Save(Window window, string path)
    {
        window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); await Task.Delay(50); window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
    private static void AssertHeaderFits(Window window, string panelName)
    {
        var brand = (FrameworkElement)window.FindName("BrandPanel"); var panel = (FrameworkElement)window.FindName(panelName);
        var origin = panel.TransformToAncestor(window).Transform(new Point());
        var brandStart = brand.TransformToAncestor(window).Transform(new Point());
        Assert(brand.DesiredSize.Width <= origin.X - brandStart.X && origin.X + panel.ActualWidth <= window.ActualWidth, "header fits without overlapping title");
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("login/detection fixture did not complete");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class CountingDetector : IDeviceDetectionService
    {
        public int Calls { get; private set; }
        public bool Delay { get; set; }
        public async Task<DeviceDetectionResult> DetectAsync(CancellationToken token)
        {
            Calls++; if (Delay) await Task.Delay(Timeout.Infinite, token);
            return new DeviceDetectionResult("已连接 · 采集接口未就绪",
                [new("ADB 组件", DetectionState.Passed, "ADB 测试组件"), new("USB 设备", DetectionState.Passed, "模拟 Mosquito T113"),
                 new("USB 驱动", DetectionState.Passed, "WINUSB · 问题代码 0"), new("板端接口", DetectionState.Warning, "采集接口未就绪：mosquito-capture")], true);
        }
    }
    private sealed class ProbeUsb : IUsbDeviceProbe
    {
        public int Calls { get; private set; }
        public IReadOnlyList<UsbBoard> Boards { get; set; } = [new(@"USB\VID_18D1&PID_D002\BOARD", "Test board", "WINUSB", 0)];
        public Task<IReadOnlyList<UsbBoard>> FindBoardsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Boards); }
    }
    private sealed class ProbeAdb : IAdbClient
    {
        public int Calls { get; private set; }
        public bool Missing { get; set; }
        public string LastCommand { get; private set; } = "";
        public IReadOnlyList<AdbDevice> Devices { get; set; } = [new("BOARD", "device", "")];
        public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken token) { Calls++; return Task.FromResult(Devices); }
        public Task<CommandResult> ShellAsync(string serial, string command, CancellationToken token)
        {
            Calls++; LastCommand = command;
            return Task.FromResult(new CommandResult(0, "MOSQUITO_PROBE=1\nMOSQUITO_IMAGE=fixture-v1\nMOSQUITO_PHOTO_METADATA=3\nCAP:mosquito-capture=" + (Missing ? "0" : "1") + "\nCAP:mosquito-environment=1\nCAP:mosquito-power=1", "", TimeSpan.Zero));
        }
        public Task<CommandResult> PullAsync(string serial, string remote, string local, CancellationToken token) => throw new InvalidOperationException("Detection cannot pull capture files");
    }
    private sealed class AuthHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool FailLogin { get; set; }
        public bool FailData { get; set; }
        public bool Unauthorized { get; set; }
        public bool DataWaiting { get; private set; }
        public TaskCompletionSource? HoldData { get; set; }
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath == "/api/auth/login")
            {
                if (FailLogin) throw new HttpRequestException("fixture offline");
                var credentials = await request.Content!.ReadFromJsonAsync<Dictionary<string, string>>(token);
                if (credentials?["password"] != "test-pass") return new(HttpStatusCode.Unauthorized);
                return Json(new { accessToken = "test-session", expiresAt = DateTimeOffset.UtcNow.Add(Lifetime) });
            }
            if (FailData) throw new HttpRequestException("fixture network unavailable");
            if (Unauthorized) return new(HttpStatusCode.Unauthorized);
            if (HoldData is not null) { DataWaiting = true; await HoldData.Task; }
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v2/captures" => Json(new CapturePage([], null, "auth-fixture", true)),
                "/api/v2/devices" => Json(new DevicePage([], null, "auth-fixture", true)),
                "/api/v2/report-checks" => Json(new ReportPage([], null, "auth-fixture", DateTimeOffset.UtcNow, true)),
                _ => new(HttpStatusCode.NotFound)
            };
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
