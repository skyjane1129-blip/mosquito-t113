using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OSS = AlibabaCloud.OSS.V2;

namespace Mosquito.Cloud.Api;

public interface IObjectStorage
{
    UploadTarget CreateUploadTarget(string objectKey, string sha256, string contentType);
    Task<ObjectInfo> GetInfoAsync(string objectKey, CancellationToken cancellationToken);
    (string Url, DateTimeOffset ExpiresAt) CreateDownloadTarget(string objectKey);
    Task<ObjectInfo> AcceptLocalUploadAsync(string token, Stream body, CancellationToken cancellationToken);
    // Server-side reads/writes used by the analysis pipeline (photo in, annotated JPEG out).
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
    Task<ObjectInfo> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken);
}

public sealed class LocalObjectStorage(IOptions<StorageOptions> options) : IObjectStorage
{
    private readonly StorageOptions _options = options.Value;

    public UploadTarget CreateUploadTarget(string objectKey, string sha256, string contentType)
    {
        ValidateSha256(sha256);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.UploadUrlMinutes);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new UploadToken(
            objectKey,
            sha256.ToLowerInvariant(),
            contentType,
            expiresAt.ToUnixTimeSeconds())));
        var token = $"{payload}.{Sign(payload)}";
        return new UploadTarget(
            objectKey,
            $"{_options.PublicBaseUrl.TrimEnd('/')}/api/uploads/{token}",
            "PUT",
            expiresAt,
            new Dictionary<string, string> { ["Content-Type"] = contentType });
    }

    public async Task<ObjectInfo> AcceptLocalUploadAsync(
        string token,
        Stream body,
        CancellationToken cancellationToken)
    {
        var upload = ValidateToken(token);
        var path = ResolvePath(upload.ObjectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await body.CopyToAsync(output, cancellationToken);
            }

            var bytes = new FileInfo(temporaryPath).Length;
            var actualSha = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!OperatorTokenService.FixedEquals(actualSha, upload.Sha256))
            {
                throw new InvalidDataException("Uploaded object SHA-256 does not match the declared value.");
            }
            File.Move(temporaryPath, path, false);
            await File.WriteAllTextAsync($"{path}.sha256", actualSha, cancellationToken);
            return new ObjectInfo(true, bytes, actualSha);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(objectKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Stored object does not exist.", objectKey);
        }
        return Task.FromResult<Stream>(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async Task<ObjectInfo> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(output, cancellationToken);
            }
            var bytes = new FileInfo(temporaryPath).Length;
            var sha = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, path, true);
            await File.WriteAllTextAsync($"{path}.sha256", sha, cancellationToken);
            return new ObjectInfo(true, bytes, sha);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ObjectInfo> GetInfoAsync(string objectKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(objectKey);
        if (!File.Exists(path))
        {
            return new ObjectInfo(false, 0, null);
        }
        var shaPath = $"{path}.sha256";
        var sha = File.Exists(shaPath)
            ? (await File.ReadAllTextAsync(shaPath, cancellationToken)).Trim()
            : await ComputeSha256Async(path, cancellationToken);
        return new ObjectInfo(true, new FileInfo(path).Length, sha);
    }

    public (string Url, DateTimeOffset ExpiresAt) CreateDownloadTarget(string objectKey)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.DownloadUrlMinutes);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new DownloadToken(
            objectKey,
            expiresAt.ToUnixTimeSeconds())));
        var token = $"{payload}.{Sign(payload)}";
        return ($"{_options.PublicBaseUrl.TrimEnd('/')}/api/downloads/{token}", expiresAt);
    }

    public string ResolveDownloadToken(string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !OperatorTokenService.FixedEquals(parts[1], Sign(parts[0])))
        {
            throw new UnauthorizedAccessException("Invalid download token.");
        }
        var payload = JsonSerializer.Deserialize<DownloadToken>(Base64UrlDecode(parts[0]))
            ?? throw new UnauthorizedAccessException("Invalid download token payload.");
        if (payload.ExpiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            throw new UnauthorizedAccessException("Download token expired.");
        }
        return ResolvePath(payload.ObjectKey);
    }

    private UploadToken ValidateToken(string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !OperatorTokenService.FixedEquals(parts[1], Sign(parts[0])))
        {
            throw new UnauthorizedAccessException("Invalid upload token.");
        }
        var payload = JsonSerializer.Deserialize<UploadToken>(Base64UrlDecode(parts[0]))
            ?? throw new UnauthorizedAccessException("Invalid upload token payload.");
        if (payload.ExpiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            throw new UnauthorizedAccessException("Upload token expired.");
        }
        ValidateSha256(payload.Sha256);
        return payload;
    }

    private string ResolvePath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid object key.");
        }
        var root = Path.GetFullPath(_options.LocalRoot);
        var path = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Object key escapes the storage root.");
        }
        return path;
    }

    private string Sign(string payload) =>
        Base64UrlEncode(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.TokenSigningKey),
            Encoding.UTF8.GetBytes(payload)));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private sealed record UploadToken(string ObjectKey, string Sha256, string ContentType, long ExpiresUnix);
    private sealed record DownloadToken(string ObjectKey, long ExpiresUnix);
}

public sealed class AliyunOssObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;
    private readonly OSS.Client _client;

    public AliyunOssObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        var credentialConfig = new Aliyun.Credentials.Models.Config
        {
            Type = "ecs_ram_role",
            RoleName = string.IsNullOrWhiteSpace(_options.EcsRamRoleName) ? null : _options.EcsRamRoleName
        };
        var credentialClient = new Aliyun.Credentials.Client(credentialConfig);
        var provider = new OSS.Credentials.CredentialsProviderFunc(() =>
        {
            var credential = credentialClient.GetCredential();
            return new OSS.Credentials.Credentials(
                credential.AccessKeyId,
                credential.AccessKeySecret,
                credential.SecurityToken);
        });
        var configuration = OSS.Configuration.LoadDefault();
        configuration.Region = _options.Region;
        configuration.CredentialsProvider = provider;
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            configuration.Endpoint = _options.Endpoint;
        }
        _client = new OSS.Client(configuration);
    }

    public UploadTarget CreateUploadTarget(string objectKey, string sha256, string contentType)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.UploadUrlMinutes);
        var result = _client.Presign(new OSS.Models.PutObjectRequest
        {
            Bucket = _options.Bucket,
            Key = objectKey,
            ContentType = contentType,
            ForbidOverwrite = true,
            ServerSideEncryption = "AES256",
            Metadata = new Dictionary<string, string> { ["sha256"] = sha256.ToLowerInvariant() }
        }, expiresAt.UtcDateTime);
        var expiration = result.Expiration.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(result.Expiration.Value, DateTimeKind.Utc))
            : expiresAt;
        return new UploadTarget(
            objectKey,
            result.Url ?? throw new InvalidOperationException("OSS did not return an upload URL."),
            result.Method ?? "PUT",
            expiration,
            result.SignedHeaders?.ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? new Dictionary<string, string>());
    }

    public async Task<ObjectInfo> GetInfoAsync(string objectKey, CancellationToken cancellationToken)
    {
        var result = await _client.HeadObjectAsync(
            new OSS.Models.HeadObjectRequest { Bucket = _options.Bucket, Key = objectKey },
            cancellationToken: cancellationToken);
        string? sha256 = null;
        result.Metadata?.TryGetValue("sha256", out sha256);
        return new ObjectInfo(true, result.ContentLength ?? 0, sha256);
    }

    public (string Url, DateTimeOffset ExpiresAt) CreateDownloadTarget(string objectKey)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.DownloadUrlMinutes);
        var result = _client.Presign(
            new OSS.Models.GetObjectRequest { Bucket = _options.Bucket, Key = objectKey },
            expiresAt.UtcDateTime);
        var expiration = result.Expiration.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(result.Expiration.Value, DateTimeKind.Utc))
            : expiresAt;
        return (
            result.Url ?? throw new InvalidOperationException("OSS did not return a download URL."),
            expiration);
    }

    public Task<ObjectInfo> AcceptLocalUploadAsync(
        string token,
        Stream body,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Uploads go directly to OSS when the AliyunOss provider is active.");

    // The analysis pipeline currently runs against local storage only (demo deployment); wiring OSS
    // GetObject/PutObject is a follow-up for the production profile.
    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Photo analysis is not available with the AliyunOss storage provider yet.");

    public Task<ObjectInfo> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Photo analysis is not available with the AliyunOss storage provider yet.");

    public void Dispose() => _client.Dispose();
}
