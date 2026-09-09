using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosquito.Client.Core;

public sealed partial class CloudApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private string? _accessToken;
    private DateTimeOffset? _expiresAt;
    private int _authenticationGeneration;
    private CancellationTokenSource _authenticationLifetime = new();
    public event Action? AuthenticationExpired;

    public CloudApiClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri($"{settings.ApiBaseUrl.TrimEnd('/')}/");
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
    }

    public bool IsAuthenticated => _accessToken is not null && _expiresAt > DateTimeOffset.UtcNow;

    public void Logout()
    {
        _accessToken = null; _expiresAt = null; _authenticationGeneration++;
        var previous = _authenticationLifetime; _authenticationLifetime = new();
        previous.Cancel(); previous.Dispose();
    }

    public async Task<DateTimeOffset> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var generation = _authenticationGeneration;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _httpClient.PostAsJsonAsync(
            "api/auth/login",
            new { username, password },
            _jsonOptions,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions, timeout.Token)
            ?? throw new InvalidDataException("Cloud login returned an empty response.");
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _authenticationGeneration) throw new OperationCanceledException("Login was superseded.");
        if (string.IsNullOrWhiteSpace(login.AccessToken) || login.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("云端返回的登录凭据无效或已过期。");
        _accessToken = login.AccessToken;
        _expiresAt = login.ExpiresAt;
        return login.ExpiresAt;
    }

    public async Task<CloudCaptureRecord> UploadArtifactAsync(
        CaptureArtifact artifact,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var request = new CloudCreateRequest(
            artifact.CaptureId,
            artifact.DeviceSerial,
            artifact.UploadRoute == UploadRoute.Windows ? "WINDOWS" : "BOARD_4G",
            artifact.HostCapturedAtUtc,
            artifact.Metadata.BoardUptimeSeconds,
            artifact.Metadata.BoardTimeValid,
            artifact.Metadata.Version,
            artifact.Metadata.RecordStatus,
            artifact.PhotoSha256,
            artifact.MetadataSha256,
            artifact.Metadata.Environment,
            artifact.Metadata.Power) { DeviceId = artifact.DeviceId };
        using var createResponse = await SendAuthorizedJsonAsync(
            HttpMethod.Post,
            "api/captures",
            request,
            cancellationToken);
        var create = await createResponse.Content.ReadFromJsonAsync<CloudCreateResponse>(
            _jsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Capture create returned an empty response.");
        if (create.Status is "Complete" or "Partial")
        {
            return (await GetCaptureAsync(create.CaptureId, cancellationToken)).Capture;
        }

        await UploadFileAsync(create.PhotoUpload, artifact.LocalPhotoPath, cancellationToken);
        await UploadFileAsync(create.MetadataUpload, artifact.LocalMetadataPath, cancellationToken);
        using var completeResponse = await SendAuthorizedJsonAsync(
            HttpMethod.Post,
            $"api/captures/{artifact.CaptureId:D}/complete",
            new { photoBytes = artifact.PhotoBytes, metadataBytes = artifact.MetadataBytes },
            cancellationToken);
        return await completeResponse.Content.ReadFromJsonAsync<CloudCaptureRecord>(
            _jsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Capture completion returned an empty response.");
    }

    public async Task<IReadOnlyList<CloudCaptureRecord>> ListCapturesAsync(
        string? deviceSerial,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var query = new List<string> { "limit=500" };
        if (!string.IsNullOrWhiteSpace(deviceSerial))
        {
            query.Add($"deviceSerial={Uri.EscapeDataString(deviceSerial)}");
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }
        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        }
        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        }
        using var response = await SendAuthorizedAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/captures?{string.Join('&', query)}"),
            cancellationToken);
        return await response.Content.ReadFromJsonAsync<CloudCaptureRecord[]>(_jsonOptions, cancellationToken)
            ?? [];
    }

    public async Task<CloudCaptureDetail> GetCaptureAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        using var response = await SendAuthorizedAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/captures/{id:D}"),
            cancellationToken);
        return await response.Content.ReadFromJsonAsync<CloudCaptureDetail>(_jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Capture detail returned an empty response.");
    }

    private async Task UploadFileAsync(
        CloudUploadTarget target,
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file);
        using var request = new HttpRequestMessage(new HttpMethod(target.Method), target.Url)
        {
            Content = content
        };
        foreach (var header in target.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
            }
            else if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                content.Headers.ContentLength = long.Parse(header.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!content.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedJsonAsync<T>(
        HttpMethod method,
        string uri,
        T value,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(value, options: _jsonOptions)
        };
        return await SendAuthorizedAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var generation = _authenticationGeneration;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authenticationLifetime.Token);
        using var ownedRequest = request;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request, lifetime.Token);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && generation == _authenticationGeneration)
            {
                Logout(); AuthenticationExpired?.Invoke();
            }
            await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Cloud API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            null,
            response.StatusCode);
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Log in before calling the cloud API.");
        }
    }

    private sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed record CloudCreateRequest(
        Guid CaptureId,
        string DeviceSerial,
        string UploadRoute,
        DateTimeOffset CapturedAtUtc,
        double? BoardUptimeSeconds,
        bool BoardTimeValid,
        int MetadataVersion,
        string BoardRecordStatus,
        string PhotoSha256,
        string MetadataSha256,
        EnvironmentReading Environment,
        PowerReading Power)
    {
        public string? DeviceId { get; init; }
    }
}
