using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class PhotoViewModel : ObservableModel
{
    private readonly CloudApiClient cloud;
    private readonly ClientSession access;
    private readonly Func<string, CancellationToken, Task<byte[]>>? downloadPhoto;
    private readonly List<PhotoWindow> _viewers = [];
    public PhotoViewModel(CloudApiClient cloud, ClientSession access, Func<string, CancellationToken, Task<byte[]>>? downloadPhoto = null)
    {
        this.cloud = cloud; this.access = access; this.downloadPhoto = downloadPhoto;
        access.Changed += Clear;
    }
    private static readonly HttpClient Photos = new() { Timeout = TimeSpan.FromSeconds(60) };
    private CancellationTokenSource? _loading;
    private BitmapSource? _image;
    private byte[]? _bytes;
    private CloudCaptureRecord? _record;
    private string _text = "选择一条采集记录，查看对应的影像和数据。";
    private string _message = "尚未选择记录";
    public BitmapSource? Image { get => _image; private set { Set(ref _image, value); Raise(nameof(HasImage)); } }
    public bool HasImage => Image is not null;
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public CloudCaptureRecord? Record => _record;
    public string TransferSummary => _record is null ? "选择或完成一次采集后，显示该照片对应的数据。" :
        _record.TransferStatus ?? (_record.UploadRoute == "WINDOWS" ? "本机保存状态未提供" : $"云端接收：{BeijingTime.Format(_record.ReceivedAtUtc)}");
    public string Summary => _record is null ? "尚未选择采集记录" :
        $"本次采集 · {_record.CapturedDisplay}\n温度  {_record.Environment?.TemperatureDisplay ?? "未提供"}    湿度  {_record.Environment?.HumidityDisplay ?? "未提供"}\n电池电压  {_record.Power?.BatteryDisplay ?? "未提供"} · {_record.StatusDisplay}";

    public void Clear()
    {
        foreach (var viewer in _viewers.ToArray()) viewer.Close();
        _viewers.Clear();
        _loading?.Cancel(); _record = null; Image = null; _bytes = null;
        NotifyRecord();
        Text = "选择一条采集记录，查看对应的影像和数据。"; Message = "尚未选择记录";
    }
    public async Task LoadAsync(CloudCaptureRecord record)
    {
        if (!access.CanUseLocal || record.LocalPhotoPath is null && !access.CanUseCloud) return;
        _loading?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(access.Token);
        _loading = cts;
        _record = record; Image = null; _bytes = null; Text = Describe(record); Message = "正在读取影像…";
        NotifyRecord();
        try
        {
            byte[] bytes;
            if (record.LocalPhotoPath is not null) bytes = await File.ReadAllBytesAsync(record.LocalPhotoPath, cts.Token);
            else
            {
                var detail = await cloud.GetCaptureAsync(record.Id, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (detail.Capture.Id != record.Id) throw new InvalidDataException("服务端返回的记录编号不一致。");
                _record = detail.Capture; Text = Describe(detail.Capture);
                NotifyRecord();
                if (string.IsNullOrWhiteSpace(detail.PhotoDownloadUrl)) { Message = "本条记录无可用影像"; return; }
                bytes = await (downloadPhoto is null ? Photos.GetByteArrayAsync(detail.PhotoDownloadUrl, cts.Token) : downloadPhoto(detail.PhotoDownloadUrl, cts.Token));
            }
            cts.Token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _bytes = bytes; Image = bitmap; Message = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · 原始影像";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Message = $"影像读取失败：{ex.Message}。重新选择记录可重试。"; }
        finally { if (ReferenceEquals(_loading, cts)) _loading = null; }
    }
    public async Task SaveAsync()
    {
        if (!access.CanUseLocal) return;
        var generation = access.Generation; var token = access.Token;
        var bytes = _bytes; var record = _record;
        if (bytes is null || record is null) return;
        var dialog = new SaveFileDialog { Title = "保存原始照片", Filter = "JPEG 照片|*.jpg", FileName = $"{record.Id:D}.jpg" };
        if (dialog.ShowDialog() != true || access.Generation != generation) return;
        try { await File.WriteAllBytesAsync(dialog.FileName, bytes, token); if (access.Generation == generation) Message = "原始照片已保存"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (access.Generation == generation) Message = $"保存失败：{ex.Message}"; }
    }
    private void NotifyRecord()
    {
        Raise(nameof(Record)); Raise(nameof(Summary)); Raise(nameof(TransferSummary));
    }
    public void OpenViewer()
    {
        if (access.CanUseLocal && Image is not null)
        {
            var viewer = new PhotoWindow(Image) { Owner = Application.Current.MainWindow };
            _viewers.Add(viewer); viewer.Closed += (_, _) => _viewers.Remove(viewer); viewer.Show();
        }
    }
    public static string Describe(CloudCaptureRecord r) =>
        $"设备编号  {r.DeviceDisplay}\n记录状态  {r.StatusDisplay}\n" +
        $"采集时间  {r.CapturedDisplay}\n时间来源  {r.TimeSourceDisplay}\n接收时间  {r.ReceivedDisplay}\n" +
        $"触发来源  {r.TriggerSource?.ToUpperInvariant() switch { "MANUAL" => "人工采集", "SCHEDULED" => "定时采集", _ => "未提供" }}\n" +
        $"温度  {r.Environment?.TemperatureDisplay ?? "未提供"}     湿度  {r.Environment?.HumidityDisplay ?? "未提供"}\n" +
        $"电池电压  {r.Power?.BatteryDisplay ?? "未提供"}     充电  {r.Power?.ChargeDisplay ?? "未提供"}\n" +
        $"环境结果  {r.Environment?.Result ?? "未提供"} · {r.Environment?.ErrorCode ?? "未提供"}\n" +
        $"电源结果  {r.Power?.Result ?? "未提供"} · {r.Power?.ErrorCode ?? "未提供"} · FAULT_REG {r.Power?.FaultRegister ?? "未提供"}\n" +
        $"照片关联位置  {r.Location?.Display ?? "无有效定位"}\nGNSS 采样时间  {BeijingTime.Format(r.Location?.SampledAtUtc)}\n坐标系  {r.Location?.CoordinateSystem ?? "未提供"}\n" +
        (r.TransferStatus is null ? "" : $"传输状态  {r.TransferStatus}\n") +
        (r.StatusDisplay == "部分完成" ? $"数据说明  环境：{r.Environment?.ErrorCode ?? "未提供"}；电源：{r.Power?.ErrorCode ?? "未提供"}\n" : "") +
        (r.FailureCode is null ? "" : $"{(r.FailureStage?.ToUpperInvariant() == "UPLOAD" ? "上传失败（采集照片已保留）" : "失败阶段：" + (r.FailureStage ?? "待确认"))}\n{r.FailureCode}\n") +
        $"采集编号  {r.Id:D}\n所有时间均为北京时间（UTC+8）";
}
