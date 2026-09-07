using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed record RouteChoice(string Label, UploadRoute Route)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly CaptureWorkflow _workflow;
    private readonly CloudApiClient _cloud;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _isAuthenticated;
    private string _connectionStatus = "正在检查设备";
    private string _cloudStatus = "未登录";
    private string _statusMessage = "请连接 USB0，并确保开发板已开启 ADB。";
    private int _progressValue;
    private string _temperature = "--";
    private string _humidity = "--";
    private string _battery = "--";
    private string _chargeState = "--";
    private string? _sensorWarning;
    private RouteChoice _selectedRoute;
    private bool _useAutofocusLock;
    private int _manualFocus;
    private string _filterStatus = "全部";
    private DateTime? _filterFrom;
    private DateTime? _filterTo;
    private CloudCaptureRecord? _selectedCapture;
    private string? _photoSource;
    private string _detailText = "选择一条记录后查看详情";

    public MainViewModel(AppSettings settings, CaptureWorkflow workflow, CloudApiClient cloud)
    {
        _settings = settings;
        _workflow = workflow;
        _cloud = cloud;
        _manualFocus = settings.DefaultFocus;
        Routes =
        [
            new RouteChoice("Windows 网络上传（默认）", UploadRoute.Windows),
            new RouteChoice("开发板 4G 直传（不回退）", UploadRoute.Board4G)
        ];
        _selectedRoute = Routes[0];
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsBusy);
        CaptureCommand = new AsyncRelayCommand(CaptureAsync, () => !IsBusy);
        RefreshDeviceCommand = new AsyncRelayCommand(RefreshDeviceAsync, () => !IsBusy);
        RefreshGalleryCommand = new AsyncRelayCommand(RefreshGalleryAsync, () => !IsBusy && IsAuthenticated);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsBusy && IsAuthenticated);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => Captures.Count > 0 && !IsBusy);
        LoadDetailCommand = new AsyncRelayCommand(LoadDetailAsync, () => SelectedCapture is not null && IsAuthenticated);
        CancelCommand = new AsyncRelayCommand(() =>
        {
            _operationCancellation?.Cancel();
            return Task.CompletedTask;
        }, () => IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Username { get; set; } = "demo";
    public string Password { private get; set; } = string.Empty;
    public IReadOnlyList<RouteChoice> Routes { get; }
    public IReadOnlyList<string> StatusFilters { get; } = ["全部", "Complete", "Partial", "Failed"];
    public ObservableCollection<CloudCaptureRecord> Captures { get; } = [];
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand RefreshDeviceCommand { get; }
    public AsyncRelayCommand RefreshGalleryCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand LoadDetailCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    public string DeviceSerial => _settings.DeviceSerial;
    public string ApiBaseUrl => _settings.ApiBaseUrl;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (Set(ref _isAuthenticated, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string CloudStatus { get => _cloudStatus; private set => Set(ref _cloudStatus, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Humidity { get => _humidity; private set => Set(ref _humidity, value); }
    public string Battery { get => _battery; private set => Set(ref _battery, value); }
    public string ChargeState { get => _chargeState; private set => Set(ref _chargeState, value); }
    public string? SensorWarning { get => _sensorWarning; private set => Set(ref _sensorWarning, value); }
    public string? PhotoSource { get => _photoSource; private set => Set(ref _photoSource, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    public RouteChoice SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (Set(ref _selectedRoute, value))
            {
                StatusMessage = value.Route == UploadRoute.Windows
                    ? "照片将通过 Windows 网络上传。"
                    : "照片将由开发板通过 4G 直传；失败时不会自动切换通道。";
            }
        }
    }

    public bool UseAutofocusLock { get => _useAutofocusLock; set => Set(ref _useAutofocusLock, value); }
    public int ManualFocus { get => _manualFocus; set => Set(ref _manualFocus, value); }
    public string FilterStatus { get => _filterStatus; set => Set(ref _filterStatus, value); }
    public DateTime? FilterFrom { get => _filterFrom; set => Set(ref _filterFrom, value); }
    public DateTime? FilterTo { get => _filterTo; set => Set(ref _filterTo, value); }

    public CloudCaptureRecord? SelectedCapture
    {
        get => _selectedCapture;
        set
        {
            if (Set(ref _selectedCapture, value))
            {
                LoadDetailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task RefreshDeviceAsync()
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            var online = await _workflow.IsDeviceOnlineAsync(CancellationToken.None);
            ConnectionStatus = online ? "设备在线" : "设备未连接";
            if (!online)
            {
                Temperature = Humidity = Battery = ChargeState = "--";
                return;
            }
            var status = await _workflow.ReadLiveStatusAsync(CancellationToken.None);
            Temperature = status.Environment?.TemperatureDisplay ?? "--";
            Humidity = status.Environment?.HumidityDisplay ?? "--";
            Battery = status.Power?.BatteryDisplay ?? "--";
            ChargeState = status.Power?.ChargeDisplay ?? "--";
            SensorWarning = status.Error;
        }
        catch (Exception exception)
        {
            ConnectionStatus = "设备检查失败";
            SensorWarning = exception.Message;
        }
    }

    private async Task LoginAsync()
    {
        await RunBusyAsync(async cancellationToken =>
        {
            var expires = await _cloud.LoginAsync(Username.Trim(), Password, cancellationToken);
            IsAuthenticated = true;
            CloudStatus = $"已登录 · 有效至 {expires.ToLocalTime():HH:mm}";
            StatusMessage = "云端登录成功，可以开始采集。";
            await RefreshGalleryCoreAsync(cancellationToken);
        }, "登录失败");
    }

    private async Task CaptureAsync()
    {
        if (SelectedRoute.Route == UploadRoute.Windows && !IsAuthenticated)
        {
            StatusMessage = "请先登录云端，再使用 Windows 上传通道。";
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            ProgressValue = 0;
            var progress = new Progress<WorkflowProgress>(item =>
            {
                ProgressValue = item.Percent;
                StatusMessage = item.Message;
            });
            var artifact = await _workflow.CaptureAndUploadAsync(
                SelectedRoute.Route,
                UseAutofocusLock ? null : ManualFocus,
                progress,
                cancellationToken);
            PhotoSource = new Uri(artifact.LocalPhotoPath).AbsoluteUri;
            Temperature = artifact.Metadata.Environment.TemperatureDisplay;
            Humidity = artifact.Metadata.Environment.HumidityDisplay;
            Battery = artifact.Metadata.Power.BatteryDisplay;
            ChargeState = artifact.Metadata.Power.ChargeDisplay;
            SensorWarning = artifact.Metadata.RecordStatus == "PARTIAL"
                ? "照片已保留，但一个或多个传感器样本不完整。"
                : null;
            StatusMessage = artifact.State switch
            {
                CaptureState.Complete => "采集完成，照片和环境数据已上传。",
                CaptureState.Partial => "照片已上传，记录标记为部分完成。",
                CaptureState.Failed => $"照片已安全保存，等待重试上传：{artifact.LastError}",
                _ => "采集完成。"
            };
            if (IsAuthenticated)
            {
                await RefreshGalleryCoreAsync(cancellationToken);
            }
        }, "采集失败");
    }

    private Task RefreshGalleryAsync() =>
        RunBusyAsync(RefreshGalleryCoreAsync, "刷新记录失败");

    private async Task RefreshGalleryCoreAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? from = FilterFrom is null
            ? null
            : new DateTimeOffset(FilterFrom.Value.Date, TimeZoneInfo.Local.GetUtcOffset(FilterFrom.Value));
        DateTimeOffset? to = FilterTo is null
            ? null
            : new DateTimeOffset(FilterTo.Value.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(FilterTo.Value));
        var records = await _cloud.ListCapturesAsync(
            _settings.DeviceSerial,
            FilterStatus == "全部" ? null : FilterStatus,
            from,
            to,
            cancellationToken);
        Captures.Clear();
        foreach (var record in records)
        {
            Captures.Add(record);
        }
        ExportCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadDetailAsync()
    {
        if (SelectedCapture is null)
        {
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            var detail = await _cloud.GetCaptureAsync(SelectedCapture.Id, cancellationToken);
            PhotoSource = detail.PhotoDownloadUrl;
            DetailText = $"编号：{detail.Capture.Id:D}\n" +
                         $"时间：{detail.Capture.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                         $"状态：{detail.Capture.Status}\n" +
                         $"温度：{detail.Capture.Environment?.TemperatureDisplay ?? "--"}\n" +
                         $"湿度：{detail.Capture.Environment?.HumidityDisplay ?? "--"}\n" +
                         $"电池：{detail.Capture.Power?.BatteryDisplay ?? "--"}\n" +
                         $"充电：{detail.Capture.Power?.ChargeDisplay ?? "--"}";
        }, "读取详情失败");
    }

    private async Task RetryAsync()
    {
        await RunBusyAsync(async cancellationToken =>
        {
            var progress = new Progress<WorkflowProgress>(item => StatusMessage = item.Message);
            var succeeded = await _workflow.RetryPendingUploadsAsync(progress, cancellationToken);
            StatusMessage = $"重试完成，本次成功 {succeeded} 条。";
            await RefreshGalleryCoreAsync(cancellationToken);
        }, "重试上传失败");
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出采集记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"蚊虫采集记录-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            await CsvExporter.ExportAsync(dialog.FileName, Captures, cancellationToken);
            StatusMessage = $"已导出 {Captures.Count} 条记录。";
        }, "导出失败");
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action, string failurePrefix)
    {
        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "操作已取消；板端已生成的照片不会被删除。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"{failurePrefix}：{exception.Message}";
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        LoginCommand.RaiseCanExecuteChanged();
        CaptureCommand.RaiseCanExecuteChanged();
        RefreshDeviceCommand.RaiseCanExecuteChanged();
        RefreshGalleryCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        LoadDetailCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
