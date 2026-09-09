using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class MainViewModel : ObservableModel
{
    private readonly AppSettings _settings;
    private readonly CaptureWorkflow _workflow;
    private readonly CloudApiClient _cloud;
    private readonly IDeviceDetectionService _detector;
    private bool _remote, _busy, _polling, _detecting, _liveReadingExpanded;
    private int _tab, _recordTab, _remoteView, _usbRetries;
    private DateTimeOffset? _nextDeviceCheck;
    private string _username = "", _password = "", _loginMessage = "未登录";
    private string _connection = "登录后检测设备", _message = "请先在右上角登录。";
    private string _liveText = "点击“读取设备信息”获取本次测量值。", _registryMessage = "请登录云端查看数据", _district = "上海市";
    private string _alertMessage = "上报计划待确认", _alertSummary = "上报检查待确认";
    private RemoteDevice? _device;
    private ReportSnapshot _alerts = new([], null, false, null);
    private CancellationTokenSource? _operation;

    public MainViewModel(AppSettings settings, CaptureWorkflow workflow, CloudApiClient cloud, OutboxRepository outbox,
        IDeviceDetectionService? detector = null)
    {
        _settings = settings; _workflow = workflow; _cloud = cloud; ManualFocus = settings.DefaultFocus;
        var adb = new AdbClient(settings);
        _detector = detector ?? new DeviceDetectionService(settings, adb, new WindowsUsbDeviceProbe(), adb.GetVersionAsync);
        Session = new(cloud);
        DirectHistory = new(cloud, outbox, false, Session); RemoteHistory = new(cloud, outbox, true, Session);
        DirectPhoto = new(cloud, Session); RemotePhoto = new(cloud, Session);
        Reports = new(cloud, async id => { if (!Session.CanUseCloud) return; RecordTab = 0; await RemoteHistory.OpenRecordAsync(id); }, Session);
        DirectModeCommand = new(() => SwitchMode(false), () => IsLoggedIn && !Busy);
        RemoteModeCommand = new(() => SwitchMode(true), () => IsLoggedIn && !Busy);
        LoginCommand = new(LoginAsync, () => IsLoggedOut && !Busy && !string.IsNullOrWhiteSpace(Username) && Password.Length > 0);
        LogoutCommand = new(() => { LockSession(false); return Task.CompletedTask; }, () => IsLoggedIn && !Busy);
        DetectDeviceCommand = new(RefreshDeviceAsync, () => IsLoggedIn && !Busy && IsDirect);
        CaptureCommand = new(CaptureAsync, () => IsLoggedIn && !Busy && IsDirect);
        ReadDeviceCommand = new(ReadDeviceAsync, () => IsLoggedIn && !Busy && IsDirect);
        RetryCommand = new(RetryAsync, () => Session.CanUseCloud && !Busy && IsDirect);
        RefreshRemoteCommand = new(RefreshRemoteAsync, () => Session.CanUseCloud && !Busy && IsRemote);
        OpenAlertCommand = new(async () => { Tab = 1; RecordTab = 1; await Reports.OpenAlertAsync(SelectedDevice?.DeviceId, SelectedAlert?.ExpectedAtUtc); }, () => Session.CanUseCloud && !Busy && HasAlert);
        OpenOverviewAlertsCommand = new(async () => { Tab = 1; RecordTab = 1; await Reports.OpenAlertAsync(null, null); }, () => Session.CanUseCloud && !Busy);
        CancelCommand = new(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => Busy);
        Session.Changed += SessionChanged;
        _cloud.AuthenticationExpired += CloudAuthenticationExpired;
    }
    public ClientSession Session { get; }
    public bool IsLoggedIn => Session.CanUseLocal;
    public bool IsLoggedOut => !IsLoggedIn;
    public bool IsLoginEditable => IsLoggedOut && !Busy;
    public string IdentityLabel => Session.Kind == SessionKind.Engineer ? "工程师模式" : "云端已登录 · " + Session.Account;
    public string LoginMessage { get => _loginMessage; private set => Set(ref _loginMessage, value); }
    public string CloudStatus => IsLoggedIn ? IdentityLabel : LoginMessage;
    public string Username { get => _username; set { if (Set(ref _username, value)) LoginCommand.RaiseCanExecuteChanged(); } }
    public string Password { private get => _password; set { if (Set(ref _password, value)) { Raise(nameof(HasPassword)); LoginCommand.RaiseCanExecuteChanged(); } } }
    public bool HasPassword => Password.Length > 0;
    public event Action? ClearPasswordRequested;
    public HistoryViewModel DirectHistory { get; }
    public HistoryViewModel RemoteHistory { get; }
    public HistoryViewModel ActiveHistory => IsRemote ? RemoteHistory : DirectHistory;
    public ReportsViewModel Reports { get; }
    public PhotoViewModel DirectPhoto { get; }
    public PhotoViewModel RemotePhoto { get; }
    public PhotoViewModel WorkPhoto => IsRemote ? RemotePhoto : DirectPhoto;
    public ObservableCollection<RemoteDevice> Devices { get; } = [];
    public ObservableCollection<DetectionCheck> DetectionChecks { get; } = [];
    public string DetectionTime { get; private set; } = "尚未检测";
    public bool HasDetectionDetails => DetectionChecks.Count > 0;
    public string DetectionButtonText => _detecting ? "检测中…" : "检测设备";
    public bool RegistryReady { get; private set; }
    public IReadOnlyList<RemoteDevice> VisibleDevices => Devices.Where(x => District == "上海市" || x.LastLocation?.District == District).ToArray();
    public int ManualFocus { get; set; }
    public bool UseAutofocusLock { get; set; }
    public string DeviceId => _settings.DeviceId ?? "待配置稳定设备编号";
    public string DeviceSerial => _settings.DeviceSerial;
    public string ApiBaseUrl => _settings.ApiBaseUrl;
    public bool IsRemote { get => _remote; private set { Set(ref _remote, value); Raise(nameof(IsDirect)); Raise(nameof(IsRemoteMap)); Raise(nameof(ActiveHistory)); Raise(nameof(WorkPhoto)); Raise(nameof(ModeDescription)); Commands(); } }
    public bool IsDirect => !IsRemote;
    public bool IsRemoteMap => IsRemote && RemoteView == 0;
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(IsLoginEditable)); Commands(); } }
    public string ModeDescription => IsRemote ? "每小时自动上报 · 地图与云端记录" : "Type-C / ADB · 本机采集与数据传输";
    public int Tab { get => _tab; set { if (IsLoggedIn || value == 0) Set(ref _tab, value); } }
    public int RecordTab { get => IsRemote ? _recordTab : 0; set { if (IsRemote && IsLoggedIn) Set(ref _recordTab, value); } }
    public int RemoteView { get => _remoteView; set { if ((IsLoggedIn || value == 0) && Set(ref _remoteView, value)) Raise(nameof(IsRemoteMap)); } }
    public string ConnectionStatus { get => _connection; private set => Set(ref _connection, value); }
    public string StatusMessage { get => _message; private set => Set(ref _message, value); }
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public bool IsLiveReadingExpanded { get => _liveReadingExpanded; set => Set(ref _liveReadingExpanded, value); }
    public string RegistryMessage { get => _registryMessage; private set => Set(ref _registryMessage, value); }
    public string District { get => _district; set { Set(ref _district, value); Raise(nameof(VisibleDevices)); } }
    public string AlertMessage { get => _alertMessage; private set => Set(ref _alertMessage, value); }
    public string AlertSummary { get => _alertSummary; private set => Set(ref _alertSummary, value); }
    private ReportCheck? SelectedAlert => _alerts.Items.FirstOrDefault(x => x.DeviceId == SelectedDevice?.DeviceId && x.EffectiveState == ReportState.Overdue);
    public bool HasAlert => SelectedAlert is not null;
    public RemoteDevice? SelectedDevice
    {
        get => _device;
        set
        {
            if (value is not null && !Session.CanUseCloud || !Set(ref _device, value)) return;
            UpdateAlert(); RemotePhoto.Clear(); Raise(nameof(DeviceLocation)); Commands();
            if (value?.LatestCaptureId is Guid id) _ = LoadLatestAsync(value, id);
        }
    }
    public string DeviceLocation => SelectedDevice?.LastLocation is { } location
        ? $"上次 GNSS：{location.Display}\n采样时间：{BeijingTime.Format(location.SampledAtUtc)}\n坐标系：{location.CoordinateSystem ?? "待确认"}"
        : "尚无有效 GNSS 位置，不在地图上虚构坐标。";
    public AsyncRelayCommand DirectModeCommand { get; }
    public AsyncRelayCommand RemoteModeCommand { get; }
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand DetectDeviceCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand ReadDeviceCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand RefreshRemoteCommand { get; }
    public AsyncRelayCommand OpenAlertCommand { get; }
    public AsyncRelayCommand OpenOverviewAlertsCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    public Task InitializeAsync() => Task.CompletedTask;
    private bool Current(int generation) => IsLoggedIn && generation == Session.Generation;
    private void SessionChanged()
    {
        Raise(nameof(IsLoggedIn)); Raise(nameof(IsLoggedOut)); Raise(nameof(IsLoginEditable)); Raise(nameof(IdentityLabel)); Raise(nameof(CloudStatus)); Commands();
    }
    private async Task SwitchMode(bool remote)
    {
        if (!IsLoggedIn || Busy || IsRemote == remote) return;
        IsRemote = remote; Raise(nameof(RecordTab)); StatusMessage = ModeDescription;
        if (remote)
        {
            if (Session.CanUseCloud && Devices.Count == 0) await RefreshRemoteAsync();
            else if (!Session.CanUseCloud) RegistryMessage = "当前为工程师本地登录。请退出后使用云端账号登录，以查看真实云端数据。";
        }
        else await RefreshDeviceAsync();
    }
    private async Task LoginAsync()
    {
        if (Busy || IsLoggedIn || string.IsNullOrWhiteSpace(Username) || Password.Length == 0) return;
        Busy = true; LoginMessage = "正在登录…";
        using var cts = new CancellationTokenSource(); _operation = cts;
        try
        {
            var account = Username.Trim();
            if (account == "admin")
            {
                if (Password != "000") { LoginMessage = "账号或密码错误"; return; }
                _cloud.Logout(); Session.Set(SessionKind.Engineer, account);
            }
            else
            {
                await _cloud.LoginAsync(account, Password, cts.Token);
                Session.Set(SessionKind.Cloud, account);
            }
            Password = ""; ClearPasswordRequested?.Invoke();
            IsRemote = false; Tab = 0; Raise(nameof(RecordTab));
            StatusMessage = "登录成功 · " + IdentityLabel;
        }
        catch (OperationCanceledException) { LoginMessage = cts.IsCancellationRequested ? "登录已取消" : "云端连接超时，请重试"; }
        catch (HttpRequestException ex) { LoginMessage = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "账号或密码错误" : "云端暂不可用，请检查服务连接"; }
        catch (Exception) { LoginMessage = "登录未完成，请检查云端服务配置"; }
        finally { _operation = null; Busy = false; Raise(nameof(CloudStatus)); }
        if (IsLoggedIn)
        {
            var generation = Session.Generation;
            await DirectHistory.QueryAsync(); if (Current(generation)) await RefreshDeviceAsync();
        }
    }
    private void LockSession(bool expired)
    {
        _cloud.Logout(); Session.Set(SessionKind.None);
        Username = ""; Password = ""; ClearPasswordRequested?.Invoke();
        _nextDeviceCheck = null; _usbRetries = 0;
        Devices.Clear(); SelectedDevice = null; RegistryReady = false; District = "上海市";
        _alerts = new([], null, false, null); UpdateAlert();
        DetectionChecks.Clear(); DetectionTime = "尚未检测"; Raise(nameof(DetectionTime)); Raise(nameof(HasDetectionDetails));
        ConnectionStatus = "登录后检测设备"; LiveText = "点击“读取设备信息”获取本次测量值。"; IsLiveReadingExpanded = false;
        RegistryMessage = "请登录云端查看数据"; AlertSummary = "上报检查待确认";
        IsRemote = false; Tab = 0; _recordTab = 0; RemoteView = 0; Raise(nameof(RecordTab));
        LoginMessage = expired ? "云端登录已过期，请重新登录" : "未登录";
        StatusMessage = "工作区已锁定，请先登录。本机已保存记录继续保留。";
        Raise(nameof(VisibleDevices)); Raise(nameof(CloudStatus));
    }
    private void CloudAuthenticationExpired()
    {
        void Expire() { if (Session.Kind == SessionKind.Cloud && !_cloud.IsAuthenticated) LockSession(true); }
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess()) app.Dispatcher.BeginInvoke(Expire);
        else Expire();
    }
    public void CheckSession()
    {
        if (Session.Kind == SessionKind.Cloud && !_cloud.IsAuthenticated) LockSession(true);
    }
    public void DeviceChanged()
    {
        if (!IsLoggedIn || IsRemote) return;
        _usbRetries = 4; _nextDeviceCheck = DateTimeOffset.UtcNow.AddMilliseconds(800);
    }
    public async Task TickAsync()
    {
        CheckSession();
        if (IsLoggedIn && IsDirect && !Busy && _nextDeviceCheck <= DateTimeOffset.UtcNow)
        {
            _nextDeviceCheck = null; await RefreshDeviceAsync();
        }
    }
    private Task CaptureAsync() => RunAsync(async (token, generation) =>
    {
        var artifact = await _workflow.CaptureAndUploadAsync(UploadRoute.Windows, UseAutofocusLock ? null : ManualFocus,
            new Progress<WorkflowProgress>(x => { if (Current(generation)) StatusMessage = x.Message; }), token, upload: false);
        if (!Current(generation)) return;
        await DirectPhoto.LoadAsync(LocalHistory.FromArtifact(artifact));
        if (!Current(generation)) return;
        DirectHistory.NotifyNew(); StatusMessage = "采集已保存到本机。可在记录查询查看，或登录云端后上传待传记录。";
    }, finishLocalSave: true);
    private Task ReadDeviceAsync() => RunAsync(async (token, generation) =>
    {
        var result = await _workflow.ReadLiveStatusAsync(token);
        if (Current(generation))
        {
            var environment = result.Environment;
            var power = result.Power;
            LiveText = $"测量于 {BeijingTime.Format(DateTimeOffset.UtcNow)}\n" +
                $"温度：{environment?.TemperatureDisplay ?? "未提供"}   湿度：{environment?.HumidityDisplay ?? "未提供"}\n" +
                $"环境结果：{environment?.Result ?? "未提供"}   错误码：{environment?.ErrorCode ?? "未提供"}   CRC：{environment?.CrcOk switch { true => "通过", false => "失败", _ => "未提供" }}\n" +
                $"电池电压：{power?.BatteryDisplay ?? "未提供"}   {power?.ChargeDisplay ?? ""}\n" +
                $"电源结果：{power?.Result ?? "未提供"}   错误码：{power?.ErrorCode ?? "未提供"}   FAULT_REG：{power?.FaultRegister ?? "未提供"}\n" +
                (result.Error is null ? "状态：读取正常" : "状态：" + result.Error);
            IsLiveReadingExpanded = true;
            StatusMessage = result.Error is null
                ? "设备信息读取结束，请查看右侧实时读取结果；该结果独立于照片的采集数据。"
                : "设备信息已读取，但板端报告警告或失败；请查看实时读取结果。";
        }
    });
    private Task RetryAsync() => RunAsync(async (token, generation) =>
    {
        if (!Session.CanUseCloud) return;
        var count = await _workflow.RetryPendingUploadsAsync(new Progress<WorkflowProgress>(x => { if (Current(generation)) StatusMessage = x.Message; }), token);
        if (Current(generation)) { StatusMessage = $"上传结束，成功 {count} 条；未成功的照片仍保留本机。"; DirectHistory.NotifyNew(); }
    });
    public async Task RefreshDeviceAsync()
    {
        if (!IsLoggedIn || IsRemote || Busy) return;
        var detectionGeneration = Session.Generation; var completed = false;
        _detecting = true; Raise(nameof(DetectionButtonText));
        try
        {
            await RunAsync(async (token, generation) =>
            {
                ConnectionStatus = "正在检测通信组件、USB 与板端接口…";
                var result = await _detector.DetectAsync(token);
                if (!Current(generation)) return;
                DetectionChecks.Clear(); foreach (var check in result.Checks) DetectionChecks.Add(check);
                DetectionTime = "检查于 " + BeijingTime.Format(result.CheckedAt); Raise(nameof(DetectionTime)); Raise(nameof(HasDetectionDetails));
                ConnectionStatus = result.Summary;
                completed = true;
                if (!result.Connected && _usbRetries > 0) { _usbRetries--; _nextDeviceCheck = DateTimeOffset.UtcNow.AddSeconds(3); }
            });
        }
        finally
        {
            if (Current(detectionGeneration) && !completed) ConnectionStatus = "检测未完成，请重试";
            _detecting = false; Raise(nameof(DetectionButtonText));
        }
    }
    private async Task LoadLatestAsync(RemoteDevice device, Guid id)
    {
        if (!Session.CanUseCloud) return;
        var generation = Session.Generation; var token = Session.Token;
        try
        {
            var record = (await _cloud.GetCaptureAsync(id, token)).Capture;
            if (Current(generation) && ReferenceEquals(device, SelectedDevice)) await RemotePhoto.LoadAsync(record);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation) && ReferenceEquals(device, SelectedDevice)) StatusMessage = $"读取最新采集失败：{ex.Message}"; }
        finally { CheckSession(); }
    }
    private Task RefreshRemoteAsync() => RunAsync(async (token, generation) =>
    {
        if (!Session.CanUseCloud) return;
        var registry = await _cloud.GetDevicesAsync(token);
        if (!Current(generation)) return;
        if (registry.Supported)
        {
            RegistryReady = true;
            var selectedId = SelectedDevice?.DeviceId;
            Devices.Clear(); foreach (var device in registry.Items) Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault(x => x.DeviceId == selectedId);
            RegistryMessage = $"已登记 {Devices.Count} 台 · 地图按上次有效 GNSS 展示 · {BeijingTime.Format(DateTimeOffset.UtcNow)}";
            Raise(nameof(VisibleDevices));
        }
        else RegistryMessage = "设备目录待接入：区域数量待确认。现有云端照片仍可在“记录查询”查看。";
        await RefreshAlertsAsync(token);
    });
    private async Task RefreshAlertsAsync(CancellationToken token)
    {
        if (!Session.CanUseCloud) return;
        var generation = Session.Generation;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = await _cloud.QueryReportChecksAsync(new(null, null, now.AddHours(-24), now), token);
            if (!Current(generation)) return;
            if (!snapshot.Supported) { AlertSummary = "检查暂停 · 上报计划待确认"; AlertMessage = snapshot.Warning ?? "上报计划待确认"; return; }
            _alerts = snapshot;
            var count = snapshot.Items.Where(x => x.EffectiveState == ReportState.Overdue).Select(x => x.DeviceId).Distinct().Count();
            AlertSummary = $"近 24 小时 · {count} 台设备有超时时段 · 检查于 {BeijingTime.Format(snapshot.CheckedAtUtc)}"; UpdateAlert();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) { AlertSummary = $"检查暂停 · 保留截至 {BeijingTime.Format(_alerts.CheckedAtUtc)} 的结果"; AlertMessage = $"上报检查暂不可用：{ex.Message}"; } }
        finally { CheckSession(); }
    }
    private void UpdateAlert()
    {
        var known = _alerts.Items.Any(x => x.DeviceId == SelectedDevice?.DeviceId && x.EffectiveState != ReportState.Pending);
        AlertMessage = SelectedAlert is { } alert ? $"{alert.ExpectedAtUtc.ToOffset(BeijingTime.Offset):MM-dd HH:mm} 时段超时未上报 · 点击查看" : known ? "当前检查范围未发现该设备的超时时段" : "上报计划待确认";
        Raise(nameof(HasAlert));
    }
    public async Task PollAsync()
    {
        CheckSession(); if (!IsLoggedIn || _polling) return;
        _polling = true; var generation = Session.Generation; var token = Session.Token;
        try
        {
            if (IsRemote && Session.CanUseCloud)
            {
                await RemoteHistory.PollAsync();
                if (Current(generation) && !Busy) await RefreshAlertsAsync(token);
                if (Current(generation) && Tab == 1 && RecordTab == 1) await Reports.PollAsync();
            }
            else if (IsDirect) { await RefreshDeviceAsync(); if (Current(generation)) await DirectHistory.PollAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) StatusMessage = $"检查暂停：{ex.Message}"; }
        finally { _polling = false; CheckSession(); Commands(); }
    }
    private async Task RunAsync(Func<CancellationToken, int, Task> action, bool finishLocalSave = false)
    {
        CheckSession(); if (Busy || !IsLoggedIn) return;
        var generation = Session.Generation; Busy = true;
        using var cts = finishLocalSave ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(Session.Token);
        _operation = cts;
        try { await action(cts.Token, generation); }
        catch (OperationCanceledException) { if (Current(generation)) StatusMessage = "操作已取消。已经保存的照片不会删除。"; }
        catch (Exception ex) { if (Current(generation)) StatusMessage = $"操作未完成：{ex.Message}"; }
        finally { _operation = null; Busy = false; CheckSession(); }
    }
    public void Close()
    {
        _cloud.AuthenticationExpired -= CloudAuthenticationExpired; _operation?.Cancel(); LockSession(false);
    }
    private void Commands()
    {
        foreach (var command in new[] { LoginCommand, LogoutCommand, DetectDeviceCommand, CaptureCommand, ReadDeviceCommand, RetryCommand,
            RefreshRemoteCommand, CancelCommand, DirectModeCommand, RemoteModeCommand, OpenAlertCommand, OpenOverviewAlertsCommand })
            command?.RaiseCanExecuteChanged();
    }
}
