using System.Net;
using System.Net.Http.Json;

namespace Mosquito.Client.Core;

public sealed partial class CloudApiClient
{
    private bool? _pagedHistorySupported;
    public async Task<IReadOnlyList<CloudCaptureRecord>> PeekHistoryAsync(HistoryQuery query, CancellationToken token)
    {
        EnsureAuthenticated();
        if (_pagedHistorySupported != false)
        {
            try
            {
                using var response = await SendAuthorizedAsync(new(HttpMethod.Get, $"api/v2/captures?{QueryString(query)}&limit=50"), token);
                var page = await response.Content.ReadFromJsonAsync<CapturePage>(_jsonOptions, token) ?? throw new InvalidDataException("记录检查响应为空。");
                _pagedHistorySupported = true;
                return page.Items;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            { _pagedHistorySupported = false; }
        }
        return (await ListCapturesAsync(null, query.Status, query.From, query.ToExclusive.AddTicks(-1), token)).Where(query.Matches).ToArray();
    }
    public async Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken token)
    {
        EnsureAuthenticated();
        if (_pagedHistorySupported != false)
        {
            var records = new List<CloudCaptureRecord>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null, snapshot = null;
            try
            {
                do
                {
                    using var response = await SendAuthorizedAsync(new(HttpMethod.Get,
                        $"api/v2/captures?{QueryString(query)}&limit=50{PageSuffix(cursor, snapshot)}"), token);
                    var page = await response.Content.ReadFromJsonAsync<CapturePage>(_jsonOptions, token)
                        ?? throw new InvalidDataException("记录分页响应为空。");
                    _pagedHistorySupported = true;
                    ValidatePage(page.Snapshot, snapshot, page.NextCursor, cursors);
                    snapshot = page.Snapshot;
                    records.AddRange(page.Items);
                    if (!page.IsComplete) return new(records, false, "服务端返回不完整结果，已暂停完整导出。请重试查询。", snapshot);
                    cursor = page.NextCursor;
                } while (cursor is not null);
                return new(records, true, null, snapshot);
            }
            catch (HttpRequestException ex) when (snapshot is null && ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            {
                _pagedHistorySupported = false;
            }
        }
        // The legacy endpoint cannot filter stable IDs. Fetch all devices, then filter locally.
        var legacy = await ListCapturesAsync(null, query.Status, query.From, query.ToExclusive.AddTicks(-1), token);
        var complete = legacy.Count < 500;
        return new(legacy.Where(query.Matches).ToArray(), complete,
            complete ? "使用兼容查询；设备编号、接收时间和定位以接口实际返回为准。" : "旧接口最多返回 500 条，结果可能不完整。请缩小日期范围；完整 CSV 导出已暂停。");
    }

    public async Task<ReportSnapshot> QueryReportChecksAsync(HistoryQuery query, CancellationToken token)
    {
        EnsureAuthenticated();
        var records = new List<ReportCheck>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null, snapshot = null;
        DateTimeOffset? checkedAt = null;
        try
        {
            do
            {
                using var response = await SendAuthorizedAsync(new(HttpMethod.Get,
                    $"api/v2/report-checks?{QueryString(query)}&limit=50{PageSuffix(cursor, snapshot)}"), token);
                var page = await response.Content.ReadFromJsonAsync<ReportPage>(_jsonOptions, token)
                    ?? throw new InvalidDataException("上报检查响应为空。");
                ValidatePage(page.Snapshot, snapshot, page.NextCursor, cursors);
                if (!page.IsComplete || (checkedAt is not null && checkedAt != page.CheckedAtUtc))
                    throw new InvalidDataException("上报检查结果不完整或检查时间发生变化，请重新查询。");
                snapshot = page.Snapshot;
                checkedAt = page.CheckedAtUtc;
                records.AddRange(page.Items.Select(x => x with { CheckedAtUtc = page.CheckedAtUtc }));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        catch (HttpRequestException ex) when (snapshot is null && ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return new([], null, false, "待确认：云端尚未提供已生效的计划和上报检查结果。");
        }
        var reusedCaptures = records.Where(x => x.CaptureId is not null).GroupBy(x => x.CaptureId)
            .Where(g => g.Select(x => (x.DeviceId, x.SlotId)).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var dedup = records.GroupBy(x => (x.DeviceId, x.ExpectedAtUtc)).Select(group =>
        {
            var distinct = group.Distinct().ToArray();
            return distinct.Length == 1 && !reusedCaptures.Contains(distinct[0].CaptureId) ? distinct[0] : distinct[0] with { State = ReportState.Pending, Reason = "时段或记录关联冲突，待服务端确认" };
        }).OrderByDescending(x => x.ExpectedAtUtc).ToArray();
        return new(dedup, checkedAt, true, null);
    }

    private static string QueryString(HistoryQuery query) =>
        $"from={Uri.EscapeDataString(query.From.ToString("O"))}&toExclusive={Uri.EscapeDataString(query.ToExclusive.ToString("O"))}" +
        (query.Device is null ? "" : $"&deviceId={Uri.EscapeDataString(query.Device)}") +
        (query.Status is null ? "" : $"&status={Uri.EscapeDataString(query.Status)}");
    private static string PageSuffix(string? cursor, string? snapshot) =>
        (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}") +
        (snapshot is null ? "" : $"&snapshot={Uri.EscapeDataString(snapshot)}");
    private static void ValidatePage(string actual, string? expected, string? next, HashSet<string> cursors)
    {
        if (string.IsNullOrWhiteSpace(actual) || (expected is not null && actual != expected))
            throw new InvalidDataException("查询快照已变化，请重新查询以避免遗漏记录。");
        if (next is not null && (string.IsNullOrWhiteSpace(next) || !cursors.Add(next)))
            throw new InvalidDataException("服务端分页游标重复或无效。");
    }
}
