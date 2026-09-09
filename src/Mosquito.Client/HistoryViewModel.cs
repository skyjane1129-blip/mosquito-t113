using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class HistoryViewModel : ObservableModel
{
    private readonly CloudApiClient _cloud;
    private readonly OutboxRepository _outbox;
    private readonly bool _remote;
    private HistorySession _session = new();
    private readonly ClientSession _access;
    private bool Allowed => _remote ? _access.CanUseCloud : _access.CanUseLocal;
    private bool Current(int generation) => Allowed && _access.Generation == generation;
    private CloudCaptureRecord? _selected;
    private bool _busy, _polling;
    private string _device = "", _status = "全部", _range = "今天", _message;
    private DateTime? _from = BeijingTime.Today, _to = BeijingTime.Today;
    public HistoryViewModel(CloudApiClient cloud, OutboxRepository outbox, bool remote, ClientSession access)
    {
        _cloud = cloud; _outbox = outbox; _remote = remote; _access = access; Photo = new(cloud, access);
        _message = remote ? "请登录云端查询采集记录" : "请登录后查询本机记录";
        QueryCommand = new(QueryAsync, () => Allowed && !Busy);
        RefreshCommand = new(() => QueryCoreAsync(_session.Query), () => Allowed && !Busy && _session.Loaded);
        PreviousCommand = new(() => Move(-1), () => Allowed && !Busy && _session.Page > 0);
        NextCommand = new(() => Move(1), () => Allowed && !Busy && _session.Page + 1 < _session.PageCount);
        ExportCommand = new(ExportAsync, () => Allowed && !Busy && _session.Loaded && _session.Result.IsComplete && _session.Result.Items.Count > 0);
        access.Changed += Reset;
    }
    public PhotoViewModel Photo { get; }
    public ObservableCollection<CloudCaptureRecord> Rows { get; } = [];
    public IReadOnlyList<string> Statuses { get; } = ["全部", "完整", "部分完成", "失败"];
    public IReadOnlyList<string> Ranges { get; } = ["今天", "昨天", "近 7 天", "自定义"];
    public AsyncRelayCommand QueryCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public string Device { get => _device; set => Set(ref _device, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Range
    {
        get => _range;
        set
        {
            if (!Set(ref _range, value)) return;
            if (value != "自定义")
            {
                _from = value switch { "昨天" => BeijingTime.Today.AddDays(-1), "近 7 天" => BeijingTime.Today.AddDays(-6), _ => BeijingTime.Today };
                _to = value == "昨天" ? _from : BeijingTime.Today; Raise(nameof(From)); Raise(nameof(To));
            }
        }
    }
    public DateTime? From { get => _from; set { if (Set(ref _from, value)) Range = "自定义"; } }
    public DateTime? To { get => _to; set { if (Set(ref _to, value)) Range = "自定义"; } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Commands(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string PageLabel => $"第 {_session.Page + 1} / {_session.PageCount} 页 · {_session.Result.Items.Count} 条{(_session.Result.IsComplete ? "" : "（不完整）")} · 每页 50 条";
    public bool HasNewRecords => _session.HasNewRecords;
    public bool IsEmpty => _session.Loaded && Rows.Count == 0;
    public CloudCaptureRecord? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && !Allowed || !Set(ref _selected, value)) return;
            _session.SelectedId = value?.Id;
            if (value is null) Photo.Clear(); else _ = Photo.LoadAsync(value);
        }
    }
    private async Task<HistoryResult> FetchAsync(HistoryQuery query, CancellationToken token) => _remote
        ? await _cloud.QueryHistoryAsync(query, token)
        : await LocalHistory.QueryAsync(_outbox, query, token);
    public async Task QueryAsync()
    {
        if (!Allowed) return;
        try
        {
            if (From is null || To is null) throw new ArgumentException("请选择开始和结束日期。");
            var status = Status switch { "完整" => "Complete", "部分完成" => "Partial", "失败" => "Failed", _ => null };
            await QueryCoreAsync(HistoryQuery.Dates(Device, status, From.Value, To.Value));
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    private async Task QueryCoreAsync(HistoryQuery query)
    {
        if (Busy || !Allowed) return;
        var generation = _access.Generation;
        Busy = true;
        try
        {
            if (_remote && !_cloud.IsAuthenticated) { Message = "请先登录云端；已有查询和照片已保留。"; return; }
            var result = await FetchAsync(query, _access.Token);
            if (!Current(generation)) return;
            _session.Apply(query, result); Render();
            Message = $"查询完成 · {BeijingTime.Format(DateTimeOffset.UtcNow)}（北京时间）" + (result.Warning is null ? "" : $" · {result.Warning}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"查询暂停，保留上次结果：{ex.Message}"; }
        finally { if (_access.Generation == generation) Busy = false; }
    }
    private Task Move(int delta) { if (Allowed) { _session.Move(delta); Render(); } return Task.CompletedTask; }
    private void Render()
    {
        var selected = _session.SelectedId;
        Rows.Clear(); foreach (var row in _session.Rows) Rows.Add(row);
        Selected = Rows.FirstOrDefault(x => x.Id == selected);
        Raise(nameof(PageLabel)); Raise(nameof(HasNewRecords)); Raise(nameof(IsEmpty)); Commands();
    }
    public async Task OpenDeviceAsync(string device)
    {
        if (!Allowed) return;
        Device = device; Status = "全部"; Range = "近 7 天"; await QueryAsync();
    }
    public async Task OpenRecordAsync(Guid id)
    {
        if (!Allowed) return;
        var generation = _access.Generation; var token = _access.Token;
        try
        {
            var record = (await _cloud.GetCaptureAsync(id, token)).Capture;
            if (!Current(generation)) return;
            Device = record.DeviceId ?? record.DeviceSerial; Status = "全部";
            From = To = record.CapturedAtUtc.ToOffset(BeijingTime.Offset).Date;
            await QueryAsync();
            if (!Current(generation)) return;
            // The record may be outside a truncated legacy page. Show the explicitly fetched detail without pretending it belongs to that page.
            var index = _session.Result.Items.ToList().FindIndex(x => x.Id == id);
            if (index >= 0) { _session.Move(index / 50 - _session.Page); _session.SelectedId = id; Render(); }
            else { Photo.Clear(); await Photo.LoadAsync(record); if (Current(generation)) Message += " · 当前详情由上报检查入口直接打开。"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"读取关联记录失败：{ex.Message}"; }
    }
    public void NotifyNew() { if (!Allowed) return; _session.HasNewRecords = true; Raise(nameof(HasNewRecords)); }
    public async Task PollAsync()
    {
        if (!Allowed || !_session.Loaded || _session.HasNewRecords || Busy || _polling) return;
        var generation = _access.Generation; var token = _access.Token;
        _polling = true;
        var query = _session.Query; var previous = _session.Result;
        try
        {
            var latest = _remote ? await _cloud.PeekHistoryAsync(query, token) : (await FetchAsync(query, token)).Items;
            if (!Current(generation)) return;
            var known = previous.Items.ToDictionary(x => x.Id);
            if (ReferenceEquals(previous, _session.Result) && latest.Any(x => !known.TryGetValue(x.Id, out var old) || old.Status != x.Status || old.TransferStatus != x.TransferStatus)) NotifyNew();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"自动检查暂停，保留当前记录和照片：{ex.Message}"; }
        finally { if (_access.Generation == generation) _polling = false; }
    }
    private async Task ExportAsync()
    {
        if (!Allowed) return;
        var generation = _access.Generation; var token = _access.Token;
        var result = _session.Result;
        if (!result.IsComplete) { Message = "当前查询不完整，请缩小范围后再导出。"; return; }
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"采集记录-{BeijingTime.Today:yyyyMMdd}.csv", Title = $"导出完整查询结果（{result.Items.Count} 条）" };
        if (dialog.ShowDialog() != true || !Current(generation)) return;
        Busy = true; var temporary = dialog.FileName + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await CsvExporter.ExportAsync(temporary, result.Items, token);
            if (!Current(generation)) return;
            File.Move(temporary, dialog.FileName, true); Message = $"已导出本次查询的全部 {result.Items.Count} 条记录（跨所有分页）。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"导出失败：{ex.Message}"; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { Message += " 临时导出文件未能清理。"; }
            catch (UnauthorizedAccessException) { Message += " 临时导出文件未能清理。"; }
            if (_access.Generation == generation) Busy = false;
        }
    }
    private void Reset()
    {
        _session = new(); Selected = null; Rows.Clear(); Photo.Clear();
        Device = ""; Status = "全部"; Range = "今天";
        _from = _to = BeijingTime.Today; Raise(nameof(From)); Raise(nameof(To));
        Busy = false; _polling = false;
        Message = _remote ? "请登录云端查询采集记录" : "查询本机已保存的影像和数据";
        Raise(nameof(PageLabel)); Raise(nameof(IsEmpty)); Raise(nameof(HasNewRecords)); Commands();
    }
    private void Commands()
    {
        QueryCommand.RaiseCanExecuteChanged(); RefreshCommand.RaiseCanExecuteChanged(); PreviousCommand.RaiseCanExecuteChanged(); NextCommand.RaiseCanExecuteChanged(); ExportCommand.RaiseCanExecuteChanged();
    }
}
