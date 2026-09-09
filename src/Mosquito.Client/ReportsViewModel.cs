using System.Collections.ObjectModel;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public sealed class ReportsViewModel : ObservableModel
{
    private readonly CloudApiClient _cloud;
    private readonly ClientSession _access;
    private bool Allowed => _access.CanUseCloud;
    private bool Current(int generation) => Allowed && generation == _access.Generation;
    private ReportSnapshot _snapshot = new([], null, false, null);
    private bool _busy;
    private string _device = "", _status = "全部", _range = "近 24 小时", _message = "等待云端提供已生效的上报计划。";
    private DateTime? _from = BeijingTime.Today.AddDays(-1), _to = BeijingTime.Today;
    private ReportCheck? _selected;
    private int _page;
    private HistoryQuery? _lastQuery;
    private string _appliedStatus = "全部";
    public ReportsViewModel(CloudApiClient cloud, Func<Guid, Task> openRecord, ClientSession access)
    {
        _cloud = cloud; _access = access;
        QueryCommand = new(QueryAsync, () => Allowed && !Busy);
        PreviousCommand = new(() => Move(-1), () => Allowed && !Busy && _page > 0);
        NextCommand = new(() => Move(1), () => Allowed && !Busy && (_page + 1) * 50 < _snapshot.Items.Count);
        ViewRecordCommand = new(async () => { if (Allowed && Selected?.CanViewRecord == true) await openRecord(Selected.CaptureId!.Value); }, () => Allowed && Selected?.CanViewRecord == true && !Busy);
        access.Changed += Reset;
    }
    public ObservableCollection<ReportCheck> Rows { get; } = [];
    public AsyncRelayCommand QueryCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand ViewRecordCommand { get; }
    public IReadOnlyList<string> Statuses { get; } = ["全部", "超时未上报", "延迟补报", "按时收到", "等待上报", "未到时间", "待确认"];
    public IReadOnlyList<string> Ranges { get; } = ["近 24 小时", "今天", "昨天", "近 7 天", "自定义"];
    public string Device { get => _device; set => Set(ref _device, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Range
    {
        get => _range;
        set
        {
            if (!Set(ref _range, value) || value == "自定义") return;
            _to = value == "昨天" ? BeijingTime.Today.AddDays(-1) : BeijingTime.Today;
            _from = value switch { "近 24 小时" => BeijingTime.Today.AddDays(-1), "近 7 天" => BeijingTime.Today.AddDays(-6), _ => _to };
            Raise(nameof(From)); Raise(nameof(To));
        }
    }
    public DateTime? From { get => _from; set { if (Set(ref _from, value)) Range = "自定义"; } }
    public DateTime? To { get => _to; set { if (Set(ref _to, value)) Range = "自定义"; } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Commands(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string CheckedLabel => _snapshot.CheckedAtUtc is null ? "尚无可靠检查结果" : $"检查截至 {BeijingTime.Format(_snapshot.CheckedAtUtc)} · 北京时间";
    public string PageLabel => $"第 {_page + 1} / {Math.Max(1, (_snapshot.Items.Count + 49) / 50)} 页 · {_snapshot.Items.Count} 个计划时段";
    public bool IsEmpty => Rows.Count == 0;
    public ReportCheck? Selected { get => _selected; set { Set(ref _selected, value); ViewRecordCommand.RaiseCanExecuteChanged(); Raise(nameof(SelectedNote)); } }
    public string SelectedNote => Selected is null ? "选择一个时段查看说明；没有收到可用记录的时段没有照片。" : $"{Selected.DeviceId} · {Selected.ExpectedDisplay} · {Selected.StateDisplay}\n{Selected.Note}";
    public async Task QueryAsync()
    {
        if (Busy || !Allowed) return;
        var generation = _access.Generation;
        Busy = true;
        try
        {
            if (!_cloud.IsAuthenticated) { Message = "请先登录云端；检查已暂停。"; return; }
            if (From is null || To is null) throw new ArgumentException("请选择开始和结束日期。");
            var now = DateTimeOffset.UtcNow;
            var query = Range == "近 24 小时" ? new HistoryQuery(string.IsNullOrWhiteSpace(Device) ? null : Device.Trim(), null, now.AddHours(-24), now) : HistoryQuery.Dates(Device, null, From.Value, To.Value);
            // Fetch all states before local filtering so defensive normalization is reflected in the filter.
            var status = Status;
            var snapshot = await _cloud.QueryReportChecksAsync(query, _access.Token);
            if (!Current(generation)) return;
            if (!snapshot.Supported)
            {
                Message = snapshot.Warning + (_snapshot.CheckedAtUtc is null ? "" : " 已保留上次结果，检查暂停。");
                return;
            }
            _lastQuery = query; _appliedStatus = status;
            var rows = snapshot.Items.Where(x => status == "全部" || x.StateDisplay == status).ToArray();
            _snapshot = snapshot with { Items = rows };
            _page = 0; Render();
            Message = "每小时一次 · 宽限 15 分钟 · 部分完成也算收到 · 低功耗休眠不代表设备故障";
            Raise(nameof(CheckedLabel));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"检查暂停，保留上次结果和检查时间：{ex.Message}"; }
        finally { if (_access.Generation == generation) Busy = false; }
    }
    public async Task OpenAlertAsync(string? device, DateTimeOffset? expected)
    {
        if (!Allowed) return;
        var generation = _access.Generation;
        Device = device ?? ""; Status = "超时未上报"; Range = "近 24 小时";
        if (expected is not null) { From = To = expected.Value.ToOffset(BeijingTime.Offset).Date; }
        await QueryAsync(); if (Current(generation)) Selected = Rows.FirstOrDefault(x => expected is null || x.ExpectedAtUtc == expected);
    }
    public async Task PollAsync()
    {
        if (Busy || _lastQuery is null || !Allowed) return;
        var generation = _access.Generation;
        Busy = true;
        try
        {
            var snapshot = await _cloud.QueryReportChecksAsync(_lastQuery, _access.Token);
            if (!Current(generation)) return;
            if (!snapshot.Supported) { Message = "检查暂停，保留上次结果：" + snapshot.Warning; return; }
            var slot = Selected?.SlotId; var device = Selected?.DeviceId;
            _snapshot = snapshot with { Items = snapshot.Items.Where(x => _appliedStatus == "全部" || x.StateDisplay == _appliedStatus).ToArray() };
            _page = Math.Clamp(_page, 0, Math.Max(0, (_snapshot.Items.Count - 1) / 50));
            Render(); Selected = Rows.FirstOrDefault(x => x.SlotId == slot && x.DeviceId == device);
            Raise(nameof(CheckedLabel)); Message = "检查已更新 · 补报会更新原时段；未收到记录的其他时段保持原状。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) Message = $"检查暂停，保留上次结果和检查时间：{ex.Message}"; }
        finally { if (_access.Generation == generation) Busy = false; }
    }
    private Task Move(int delta) { _page = Math.Clamp(_page + delta, 0, Math.Max(0, (_snapshot.Items.Count - 1) / 50)); Render(); return Task.CompletedTask; }
    private void Render() { Selected = null; Rows.Clear(); foreach (var row in _snapshot.Items.Skip(_page * 50).Take(50)) Rows.Add(row); Raise(nameof(PageLabel)); Raise(nameof(IsEmpty)); Commands(); }
    private void Commands() { QueryCommand.RaiseCanExecuteChanged(); PreviousCommand.RaiseCanExecuteChanged(); NextCommand.RaiseCanExecuteChanged(); ViewRecordCommand.RaiseCanExecuteChanged(); }
    private void Reset()
    {
        _snapshot = new([], null, false, null); _lastQuery = null; _page = 0; _appliedStatus = "全部";
        Device = ""; Status = "全部"; Range = "近 24 小时"; _from = BeijingTime.Today.AddDays(-1); _to = BeijingTime.Today;
        Raise(nameof(From)); Raise(nameof(To)); Busy = false; Render(); Raise(nameof(CheckedLabel));
        Message = "请登录云端查看上报检查。";
    }
}
