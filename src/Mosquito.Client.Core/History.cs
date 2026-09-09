using System.Globalization;

namespace Mosquito.Client.Core;

public static class BeijingTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public static DateTime Today => DateTimeOffset.UtcNow.ToOffset(Offset).Date;
    public static DateTimeOffset Start(DateTime date) => new(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), Offset);
    public static string Format(DateTimeOffset? value) => value?.ToOffset(Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "未提供";
}

public sealed record HistoryQuery(string? Device, string? Status, DateTimeOffset From, DateTimeOffset ToExclusive)
{
    public static HistoryQuery Dates(string? device, string? status, DateTime from, DateTime to)
    {
        if (from.Date > to.Date) throw new ArgumentException("开始日期不能晚于结束日期。");
        return new(string.IsNullOrWhiteSpace(device) ? null : device.Trim(), status, BeijingTime.Start(from), BeijingTime.Start(to.AddDays(1)));
    }
    public bool Matches(CloudCaptureRecord record) =>
        (Device is null || string.Equals(record.DeviceId, Device, StringComparison.OrdinalIgnoreCase) || string.Equals(record.DeviceSerial, Device, StringComparison.OrdinalIgnoreCase)) &&
        (Status is null || string.Equals(record.Status, Status, StringComparison.OrdinalIgnoreCase)) &&
        record.CapturedAtUtc >= From && record.CapturedAtUtc < ToExclusive;
}

public sealed record HistoryResult(IReadOnlyList<CloudCaptureRecord> Items, bool IsComplete, string? Warning, string? Snapshot = null);
public sealed record CapturePage(CloudCaptureRecord[] Items, string? NextCursor, string Snapshot, bool IsComplete);

// The session owns the displayed query snapshot. Editing controls or polling must not replace it.
public sealed class HistorySession
{
    public HistoryQuery Query { get; private set; } = HistoryQuery.Dates(null, null, BeijingTime.Today, BeijingTime.Today);
    public HistoryResult Result { get; private set; } = new([], true, null);
    public int Page { get; private set; }
    public Guid? SelectedId { get; set; }
    public bool Loaded { get; private set; }
    public bool HasNewRecords { get; set; }
    public int PageCount => Math.Max(1, (Result.Items.Count + 49) / 50);
    public IEnumerable<CloudCaptureRecord> Rows => Result.Items.Skip(Page * 50).Take(50);
    public void Apply(HistoryQuery query, HistoryResult result)
    {
        var sameQuery = Query == query;
        Query = query;
        Result = result with { Items = result.Items.DistinctBy(x => x.Id).OrderByDescending(x => x.CapturedAtUtc).ThenBy(x => x.Id).ToArray() };
        Page = sameQuery ? Math.Min(Page, PageCount - 1) : 0;
        if (!sameQuery || !Result.Items.Any(x => x.Id == SelectedId)) SelectedId = null;
        if (SelectedId is Guid selected) Page = Result.Items.ToList().FindIndex(x => x.Id == selected) / 50;
        Loaded = true;
        HasNewRecords = false;
    }
    public void Move(int delta) { Page = Math.Clamp(Page + delta, 0, PageCount - 1); SelectedId = null; }
}

public static class LocalHistory
{
    public static CloudCaptureRecord FromArtifact(CaptureArtifact artifact) => new(
        artifact.CaptureId, artifact.DeviceSerial, artifact.Metadata.RecordStatus, "WINDOWS", artifact.HostCapturedAtUtc,
        artifact.Metadata.BoardTimeValid, artifact.PhotoBytes, artifact.MetadataBytes, artifact.Metadata.Environment, artifact.Metadata.Power,
        artifact.LastError)
    {
        DeviceId = artifact.DeviceId, TriggerSource = "MANUAL", TimeSource = "WINDOWS", LocalPhotoPath = artifact.LocalPhotoPath,
        FailureStage = artifact.LastError is null ? null : "UPLOAD",
        TransferStatus = artifact.State switch
        {
            CaptureState.Complete or CaptureState.Partial => "已上传", CaptureState.Failed => "上传失败 · 本机照片已保留",
            CaptureState.Uploading => "上传未确认 · 可重试", _ => "已保存本机 · 待上传"
        }
    };
    public static async Task<HistoryResult> QueryAsync(OutboxRepository repository, HistoryQuery query, CancellationToken token)
    {
        var records = (await repository.GetAllAsync(token)).Select(FromArtifact).Where(query.Matches).OrderByDescending(x => x.CapturedAtUtc).ToArray();
        return new(records, true, null);
    }
}

public enum ReportState { Pending, NotDue, Waiting, OnTime, Overdue, Late }

public sealed record ReportCheck(
    string SlotId, string DeviceId, DateTimeOffset ExpectedAtUtc, ReportState State,
    bool ScheduleConfirmed, DateTimeOffset? ScheduleEffectiveAtUtc, bool AssociationReliable,
    Guid? CaptureId, DateTimeOffset? ReceivedAtUtc, string? RecordStatus)
{
    public DateTimeOffset? ScheduleAnchorUtc { get; init; }
    public int IntervalMinutes { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; }
    public string? Reason { get; init; }
    public string? District { get; init; }
    // Validate the server's result conservatively. Never create slots from a client history page.
    public ReportState EffectiveState
    {
        get
        {
            if (!ScheduleConfirmed || !AssociationReliable || ScheduleEffectiveAtUtc is null || ScheduleAnchorUtc is null ||
                IntervalMinutes != 60 || ExpectedAtUtc < ScheduleEffectiveAtUtc || CheckedAtUtc == default ||
                (ExpectedAtUtc - ScheduleAnchorUtc.Value).Ticks % TimeSpan.FromHours(1).Ticks != 0) return ReportState.Pending;
            var received = CaptureId is not null && ReceivedAtUtc is not null &&
                (RecordStatus?.ToUpperInvariant() is "COMPLETE" or "PARTIAL");
            if (received && (ReceivedAtUtc < ExpectedAtUtc || ReceivedAtUtc > CheckedAtUtc)) return ReportState.Pending;
            var deadline = ExpectedAtUtc.AddMinutes(15);
            var consistent = State switch
            {
                ReportState.NotDue => !received && CheckedAtUtc < ExpectedAtUtc,
                ReportState.Waiting => !received && CheckedAtUtc >= ExpectedAtUtc && CheckedAtUtc < deadline,
                ReportState.Overdue => !received && CheckedAtUtc >= deadline,
                ReportState.OnTime => received && ReceivedAtUtc < deadline,
                ReportState.Late => received && ReceivedAtUtc >= deadline,
                _ => false
            };
            return consistent ? State : ReportState.Pending;
        }
    }
    public string StateDisplay => EffectiveState switch
    {
        ReportState.NotDue => "未到时间", ReportState.Waiting => "等待上报", ReportState.OnTime => "按时收到",
        ReportState.Overdue => "超时未上报", ReportState.Late => "延迟补报", _ => "待确认"
    };
    public string ExpectedDisplay => BeijingTime.Format(ExpectedAtUtc);
    public string ReceivedDisplay => BeijingTime.Format(ReceivedAtUtc);
    public bool CanViewRecord => CaptureId is not null && EffectiveState is ReportState.OnTime or ReportState.Late;
    public string Note => EffectiveState == ReportState.Pending ? Reason ?? "计划或时段关联待确认" : RecordStatus?.ToUpperInvariant() == "PARTIAL" ? "已收到 · 数据部分完成" : Reason ?? "";
}

public sealed record ReportPage(ReportCheck[] Items, string? NextCursor, string Snapshot, DateTimeOffset CheckedAtUtc, bool IsComplete);
public sealed record ReportSnapshot(IReadOnlyList<ReportCheck> Items, DateTimeOffset? CheckedAtUtc, bool Supported, string? Warning);
