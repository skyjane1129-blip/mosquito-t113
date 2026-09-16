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
    private static readonly TimeSpan AnalysisPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AnalysisWait = TimeSpan.FromMinutes(12);
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _analyzing;
    private BitmapSource? _image;
    private BitmapSource? _original;
    private BitmapSource? _annotated;
    private byte[]? _bytes;
    private byte[]? _annotatedBytes;
    private EggAnalysis? _analysis;
    private bool _showAnnotated;
    private bool _busy;
    private CloudCaptureRecord? _record;
    private string _text = "选择一条采集记录，查看对应的影像和数据。";
    private string _message = "尚未选择记录";
    public BitmapSource? Image { get => _image; private set { Set(ref _image, value); Raise(nameof(HasImage)); Raise(nameof(CanAnalyze)); } }
    public bool HasImage => Image is not null;
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public CloudCaptureRecord? Record => _record;
    public EggAnalysis? Analysis => _analysis;
    // Both pictures are loaded: the operator can flip between the raw photo and the annotated one.
    public bool HasAnalysis => _annotated is not null && _original is not null;
    public bool ShowAnnotated
    {
        get => _showAnnotated;
        set
        {
            if (!HasAnalysis) value = false;
            if (Set(ref _showAnnotated, value)) { Image = value ? _annotated : _original; Raise(nameof(ToggleText)); }
        }
    }
    public string ToggleText => ShowAnnotated ? "显示原图" : "显示标注图";
    public bool IsAnalyzing => _busy;
    public string AnalyzeButtonText => _busy ? "识别中…" : HasAnalysis ? "重新识别" : "识别蚊卵";
    // Needs a cloud login: the model runs on the server next to the stored photo.
    public bool CanAnalyze => HasImage && !_busy && access.CanUseCloud && _record is not null;
    public string AnalysisSummary => _analysis?.Summary ?? "";
    public bool HasAnalysisSummary => _analysis is not null;
    public string TransferSummary => _record is null ? "选择或完成一次采集后，显示该照片对应的数据。" :
        _record.TransferStatus ?? (_record.UploadRoute == "WINDOWS" ? "本机保存状态未提供" : $"云端接收：{BeijingTime.Format(_record.ReceivedAtUtc)}");
    public string Summary => _record is null ? "尚未选择采集记录" :
        $"本次采集 · {_record.CapturedDisplay}\n温度  {_record.Environment?.TemperatureDisplay ?? "未提供"}    湿度  {_record.Environment?.HumidityDisplay ?? "未提供"}\n电池电压  {_record.Power?.BatteryDisplay ?? "未提供"} · {_record.StatusDisplay}";

    public void Clear()
    {
        foreach (var viewer in _viewers.ToArray()) viewer.Close();
        _viewers.Clear();
        _loading?.Cancel(); _analyzing?.Cancel();
        _record = null; _bytes = null; _annotatedBytes = null; _original = null; _annotated = null; _analysis = null; _showAnnotated = false; _busy = false;
        Image = null;
        NotifyRecord(); NotifyAnalysis();
        Text = "选择一条采集记录，查看对应的影像和数据。"; Message = "尚未选择记录";
    }
    public async Task LoadAsync(CloudCaptureRecord record)
    {
        if (!access.CanUseLocal || record.LocalPhotoPath is null && !access.CanUseCloud) return;
        _loading?.Cancel(); _analyzing?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(access.Token);
        _loading = cts;
        _record = record; _bytes = null; _annotatedBytes = null; _original = null; _annotated = null; _analysis = record.Analysis; _showAnnotated = false; _busy = false;
        Image = null; Text = Describe(record); Message = "正在读取影像…";
        NotifyRecord(); NotifyAnalysis();
        try
        {
            byte[] bytes; string? annotatedUrl = null;
            if (record.LocalPhotoPath is not null) bytes = await File.ReadAllBytesAsync(record.LocalPhotoPath, cts.Token);
            else
            {
                var detail = await cloud.GetCaptureAsync(record.Id, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (detail.Capture.Id != record.Id) throw new InvalidDataException("服务端返回的记录编号不一致。");
                _record = detail.Capture; _analysis = detail.Capture.Analysis; annotatedUrl = detail.AnnotatedDownloadUrl; Text = Describe(detail.Capture);
                NotifyRecord(); NotifyAnalysis();
                if (string.IsNullOrWhiteSpace(detail.PhotoDownloadUrl)) { Message = "本条记录无可用影像"; return; }
                bytes = await Download(detail.PhotoDownloadUrl, cts.Token);
            }
            cts.Token.ThrowIfCancellationRequested();
            var bitmap = Decode(bytes);
            _bytes = bytes; _original = bitmap; Image = bitmap; Message = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · 原始影像";
            // A previous analysis exists: fetch its annotated picture so the toggle works straight away.
            if (annotatedUrl is not null && _analysis is { IsCompleted: true })
            {
                try
                {
                    var annotatedBytes = await Download(annotatedUrl, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    _annotatedBytes = annotatedBytes; _annotated = Decode(annotatedBytes);
                    NotifyAnalysis(); ShowAnnotated = true;
                    Message = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · 标注图（已识别）";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Message = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · 原始影像 · 标注图读取失败：{ex.Message}"; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Message = $"影像读取失败：{ex.Message}。重新选择记录可重试。"; }
        finally { if (ReferenceEquals(_loading, cts)) _loading = null; }
    }

    // "识别蚊卵": ask the server to run the model on this photo, poll until it is final, then show the
    // annotated picture. The model runs server-side (CPU, ~10-60 s) so the UI only waits and refreshes.
    public async Task AnalyzeAsync()
    {
        if (!CanAnalyze || _record is null) return;
        var record = _record; var force = _analysis is { IsCompleted: true };
        _analyzing?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(access.Token);
        _analyzing = cts;
        _busy = true; NotifyAnalysis();
        var started = DateTimeOffset.UtcNow;
        Message = force ? "正在重新识别蚊卵…" : "正在识别蚊卵…";
        try
        {
            var response = await cloud.StartAnalysisAsync(record.Id, force, cts.Token);
            var analysis = response.Analysis;
            while (analysis is not { IsFinal: true })
            {
                cts.Token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow - started > AnalysisWait) throw new TimeoutException("服务端识别超时，请稍后重新选择记录查看结果。");
                Message = $"正在识别蚊卵…已等待 {(int)(DateTimeOffset.UtcNow - started).TotalSeconds} 秒（大图按小块逐块检测，通常 10 到 60 秒）";
                await Task.Delay(AnalysisPoll, cts.Token);
                response = await cloud.GetAnalysisAsync(record.Id, cts.Token);
                analysis = response.Analysis;
            }
            cts.Token.ThrowIfCancellationRequested();
            _analysis = analysis;
            // Keep the detail text in step with the result (it was rendered when the record was loaded).
            _record = record with { Analysis = analysis }; Text = Describe(_record); NotifyRecord();
            if (analysis.IsFailed) { Message = analysis.Summary; NotifyAnalysis(); return; }
            if (string.IsNullOrWhiteSpace(response.AnnotatedDownloadUrl)) throw new InvalidDataException("服务端未返回标注图地址。");
            var annotatedBytes = await Download(response.AnnotatedDownloadUrl, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _annotatedBytes = annotatedBytes; _annotated = Decode(annotatedBytes);
            _busy = false; NotifyAnalysis(); ShowAnnotated = true;
            Message = $"识别完成 · {analysis.Summary}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Message = $"识别失败：{ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_analyzing, cts)) _analyzing = null;
            if (!cts.IsCancellationRequested) { _busy = false; NotifyAnalysis(); }
        }
    }
    public async Task SaveAsync()
    {
        if (!access.CanUseLocal) return;
        var generation = access.Generation; var token = access.Token;
        var annotated = ShowAnnotated;
        var bytes = annotated ? _annotatedBytes : _bytes; var record = _record;
        if (bytes is null || record is null) return;
        var dialog = new SaveFileDialog
        {
            Title = annotated ? "保存标注照片" : "保存原始照片",
            Filter = "JPEG 照片|*.jpg",
            FileName = annotated ? record.AnnotatedPhotoName : record.PhotoName
        };
        if (dialog.ShowDialog() != true || access.Generation != generation) return;
        try { await File.WriteAllBytesAsync(dialog.FileName, bytes, token); if (access.Generation == generation) Message = annotated ? "标注照片已保存" : "原始照片已保存"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (access.Generation == generation) Message = $"保存失败：{ex.Message}"; }
    }
    private void NotifyRecord()
    {
        Raise(nameof(Record)); Raise(nameof(Summary)); Raise(nameof(TransferSummary));
    }
    private void NotifyAnalysis()
    {
        Raise(nameof(Analysis)); Raise(nameof(HasAnalysis)); Raise(nameof(ShowAnnotated)); Raise(nameof(ToggleText));
        Raise(nameof(IsAnalyzing)); Raise(nameof(AnalyzeButtonText)); Raise(nameof(CanAnalyze));
        Raise(nameof(AnalysisSummary)); Raise(nameof(HasAnalysisSummary));
    }
    private Task<byte[]> Download(string url, CancellationToken token) =>
        downloadPhoto is null ? Photos.GetByteArrayAsync(url, token) : downloadPhoto(url, token);
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    public void OpenViewer()
    {
        if (access.CanUseLocal && Image is not null)
        {
            var name = ShowAnnotated ? _record?.AnnotatedPhotoName : _record?.PhotoName;
            var viewer = new PhotoWindow(Image)
            {
                Owner = Application.Current.MainWindow,
                Title = (ShowAnnotated ? "蚊卵标注图" : "原图查看") + (name is null ? "" : $" · {name}")
            };
            _viewers.Add(viewer); viewer.Closed += (_, _) => _viewers.Remove(viewer); viewer.Show();
        }
    }
    public static string Describe(CloudCaptureRecord r) =>
        $"照片名称  {r.PhotoName}\n设备编号  {r.DeviceDisplay}\n记录状态  {r.StatusDisplay}\n" +
        $"采集时间  {r.CapturedDisplay}\n时间来源  {r.TimeSourceDisplay}\n接收时间  {r.ReceivedDisplay}\n" +
        $"触发来源  {r.TriggerSource?.ToUpperInvariant() switch { "MANUAL" => "人工采集", "SCHEDULED" => "定时采集", "REMOTE_COMMAND" => "远程指令采集（4G）", _ => "未提供" }}\n" +
        $"蚊卵识别  {r.Analysis?.Summary ?? "尚未识别（点“识别蚊卵”）"}\n" +
        $"温度  {r.Environment?.TemperatureDisplay ?? "未提供"}     湿度  {r.Environment?.HumidityDisplay ?? "未提供"}\n" +
        $"电池电压  {r.Power?.BatteryDisplay ?? "未提供"}     充电  {r.Power?.ChargeDisplay ?? "未提供"}\n" +
        $"环境结果  {r.Environment?.Result ?? "未提供"} · {r.Environment?.ErrorCode ?? "未提供"}\n" +
        $"电源结果  {r.Power?.Result ?? "未提供"} · {r.Power?.ErrorCode ?? "未提供"} · FAULT_REG {r.Power?.FaultRegister ?? "未提供"}\n" +
        $"照片关联位置  {r.Location?.Display ?? "无有效定位"}\n定位方式  {r.Location?.SourceDisplay ?? "未提供"}{(r.Location is null ? "" : $" · 估计精度 {r.Location.AccuracyDisplay}")}\n定位采样时间  {BeijingTime.Format(r.Location?.SampledAtUtc)}\n坐标系  {r.Location?.CoordinateSystem ?? "未提供"}\n" +
        (r.TransferStatus is null ? "" : $"传输状态  {r.TransferStatus}\n") +
        (r.StatusDisplay == "部分完成" ? $"数据说明  环境：{r.Environment?.ErrorCode ?? "未提供"}；电源：{r.Power?.ErrorCode ?? "未提供"}\n" : "") +
        (r.FailureCode is null ? "" : $"{(r.FailureStage?.ToUpperInvariant() == "UPLOAD" ? "上传失败（采集照片已保留）" : "失败阶段：" + (r.FailureStage ?? "待确认"))}\n{r.FailureCode}\n") +
        $"采集编号  {r.Id:D}\n所有时间均为北京时间（UTC+8）";
}
