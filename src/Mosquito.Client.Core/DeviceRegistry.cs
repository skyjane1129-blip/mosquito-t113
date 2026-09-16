using System.Net;
using System.Net.Http.Json;
namespace Mosquito.Client.Core;
public sealed record RemoteDevice(string DeviceId, string? DeviceSerial, GeoSample? LastLocation, Guid? LatestCaptureId)
{
    // Heartbeat-derived fields. Older servers omit them, so every one is optional.
    public bool? Online { get; init; }
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public DateTimeOffset? LatestCaptureAtUtc { get; init; }
    public EnvironmentReading? LastEnvironment { get; init; }
    public PowerReading? LastPower { get; init; }
    public string? ImageVersion { get; init; }
    public string OnlineDisplay => Online switch
    {
        true => $"在线 · 最近心跳 {BeijingTime.Format(LastSeenAtUtc)}",
        false => LastSeenAtUtc is null ? "离线 · 尚未收到心跳" : $"离线 · 最近心跳 {BeijingTime.Format(LastSeenAtUtc)}",
        null => "在线状态待确认"
    };
}
public sealed record DevicePage(RemoteDevice[] Items, string? NextCursor, string Snapshot, bool IsComplete);
public sealed record DeviceRegistry(IReadOnlyList<RemoteDevice> Items, bool Supported);
public sealed partial class CloudApiClient
{
    public async Task<DeviceRegistry> GetDevicesAsync(CancellationToken token)
    {
        EnsureAuthenticated();
        var records = new List<RemoteDevice>(); var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null, snapshot = null;
        try
        {
            do
            {
                using var response = await SendAuthorizedAsync(new(HttpMethod.Get, $"api/v2/devices?limit=50{PageSuffix(cursor, snapshot)}"), token);
                var page = await response.Content.ReadFromJsonAsync<DevicePage>(_jsonOptions, token) ?? throw new InvalidDataException("设备列表为空。");
                ValidatePage(page.Snapshot, snapshot, page.NextCursor, cursors);
                if (!page.IsComplete) throw new InvalidDataException("设备列表不完整，暂不更新区域数量。");
                records.AddRange(page.Items); cursor = page.NextCursor; snapshot = page.Snapshot;
            } while (cursor is not null);
            return new(records.Where(x => !string.IsNullOrWhiteSpace(x.DeviceId)).DistinctBy(x => x.DeviceId).ToArray(), true);
        }
        catch (HttpRequestException ex) when (snapshot is null && ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        { return new([], false); }
    }

    // Queue a remote capture for one registered board. The board picks it up over 4G on its next poll.
    public async Task<RemoteCommand> CreateCaptureCommandAsync(string deviceId, int? focus, CancellationToken token)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedJsonAsync(HttpMethod.Post,
            $"api/v2/devices/{Uri.EscapeDataString(deviceId)}/commands", new { type = "capture", focus }, token);
        return await response.Content.ReadFromJsonAsync<RemoteCommand>(_jsonOptions, token)
            ?? throw new InvalidDataException("云端未返回指令信息。");
    }

    // Queue fast site learning: the board re-searches cells and takes several LBS fixes so the
    // server learns the nearest tower's position within minutes of arriving at a new site.
    public async Task<RemoteCommand> CreateLearnCommandAsync(string deviceId, int? rounds, CancellationToken token)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedJsonAsync(HttpMethod.Post,
            $"api/v2/devices/{Uri.EscapeDataString(deviceId)}/commands", new { type = "learn", rounds }, token);
        return await response.Content.ReadFromJsonAsync<RemoteCommand>(_jsonOptions, token)
            ?? throw new InvalidDataException("云端未返回指令信息。");
    }

    public async Task<RemoteCommand> GetCommandAsync(Guid commandId, CancellationToken token)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedAsync(new(HttpMethod.Get, $"api/v2/commands/{commandId:D}"), token);
        return await response.Content.ReadFromJsonAsync<RemoteCommand>(_jsonOptions, token)
            ?? throw new InvalidDataException("云端未返回指令状态。");
    }
}
