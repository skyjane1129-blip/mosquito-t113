using System.Net;
using System.Net.Http.Json;
namespace Mosquito.Client.Core;
public sealed record RemoteDevice(string DeviceId, string? DeviceSerial, GeoSample? LastLocation, Guid? LatestCaptureId);
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
}
