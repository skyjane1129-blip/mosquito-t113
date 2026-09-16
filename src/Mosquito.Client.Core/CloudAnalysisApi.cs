using System.Net.Http.Json;

namespace Mosquito.Client.Core;

// Mosquito / egg detection endpoints: POST starts a server-side run (returns 202 while it is still
// running), GET polls the result. The annotated JPEG is fetched through the signed URL in the response.
public sealed partial class CloudApiClient
{
    public async Task<CloudAnalysisResponse> StartAnalysisAsync(Guid captureId, bool force, CancellationToken token)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedAsync(
            new HttpRequestMessage(HttpMethod.Post, $"api/v2/captures/{captureId:D}/analysis?force={(force ? "true" : "false")}"), token);
        return await response.Content.ReadFromJsonAsync<CloudAnalysisResponse>(_jsonOptions, token)
            ?? throw new InvalidDataException("云端未返回识别状态。");
    }

    public async Task<CloudAnalysisResponse> GetAnalysisAsync(Guid captureId, CancellationToken token)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/v2/captures/{captureId:D}/analysis"), token);
        return await response.Content.ReadFromJsonAsync<CloudAnalysisResponse>(_jsonOptions, token)
            ?? throw new InvalidDataException("云端未返回识别状态。");
    }
}
