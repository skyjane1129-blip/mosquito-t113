using System.Text.RegularExpressions;

namespace Mosquito.Cloud.Api;

public sealed partial class CaptureService(
    ICaptureRepository repository,
    IObjectStorage storage,
    DeviceService? deviceService = null)
{
    public async Task<CreateCaptureResponse> CreateAsync(
        CreateCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var id = request.CaptureId ?? Guid.NewGuid();
        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is not null)
        {
            if (existing.DeviceSerial != request.DeviceSerial ||
                !OperatorTokenService.FixedEquals(existing.PhotoSha256, request.PhotoSha256) ||
                !OperatorTokenService.FixedEquals(existing.MetadataSha256, request.MetadataSha256))
            {
                throw new CaptureConflictException("Capture ID already belongs to different content.");
            }
            return BuildUploadResponse(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var prefix = $"captures/{request.DeviceSerial}/{request.CapturedAtUtc:yyyy/MM/dd}/{id:D}";
        var capture = new CaptureRecord(
            id,
            request.DeviceSerial,
            CaptureStatus.Uploading,
            request.UploadRoute,
            request.CapturedAtUtc.ToUniversalTime(),
            now,
            now,
            request.BoardUptimeSeconds,
            request.BoardTimeValid,
            request.MetadataVersion,
            request.BoardRecordStatus.ToUpperInvariant(),
            $"{prefix}/photo.jpg",
            $"{prefix}/metadata.txt",
            request.PhotoSha256.ToLowerInvariant(),
            request.MetadataSha256.ToLowerInvariant(),
            null,
            null,
            request.Environment,
            request.Power,
            null)
        {
            DeviceId = request.DeviceId,
            TriggerSource = request.TriggerSource?.ToUpperInvariant() ??
                            (request.CommandId is null ? "MANUAL" : "REMOTE_COMMAND"),
            TimeSource = request.TimeSource?.ToUpperInvariant() ?? InferTimeSource(request),
            CommandId = request.CommandId,
            // The board's LBS file carries no timestamp; the fix is taken immediately before the capture.
            Location = request.Location is { IsValid: true } location
                ? location with { SampledAtUtc = location.SampledAtUtc ?? request.CapturedAtUtc }
                : null
        };
        if (!await repository.AddAsync(capture, cancellationToken))
        {
            throw new CaptureConflictException("Capture ID was created concurrently.");
        }
        return BuildUploadResponse(capture);
    }

    public async Task<CaptureRecord> CompleteAsync(
        Guid id,
        CompleteCaptureRequest request,
        CancellationToken cancellationToken)
    {
        var capture = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Capture does not exist.");
        if (capture.Status is CaptureStatus.Complete or CaptureStatus.Partial)
        {
            return capture;
        }
        if (request.PhotoBytes <= 0 || request.MetadataBytes <= 0)
        {
            throw new CaptureValidationException("Uploaded object sizes must be positive.");
        }

        var photo = await storage.GetInfoAsync(capture.PhotoObjectKey, cancellationToken);
        var metadata = await storage.GetInfoAsync(capture.MetadataObjectKey, cancellationToken);
        if (!photo.Exists || !metadata.Exists)
        {
            throw new CaptureConflictException("Both photo and metadata must be uploaded before completion.");
        }
        if (photo.Bytes != request.PhotoBytes || metadata.Bytes != request.MetadataBytes)
        {
            throw new CaptureConflictException("Reported object size does not match stored content.");
        }
        if (photo.Sha256 is null || metadata.Sha256 is null ||
            !OperatorTokenService.FixedEquals(photo.Sha256, capture.PhotoSha256) ||
            !OperatorTokenService.FixedEquals(metadata.Sha256, capture.MetadataSha256))
        {
            throw new CaptureConflictException("Stored object SHA-256 does not match the capture manifest.");
        }

        var status = capture.BoardRecordStatus == "COMPLETE"
            ? CaptureStatus.Complete
            : CaptureStatus.Partial;
        var now = DateTimeOffset.UtcNow;
        var completed = capture with
        {
            Status = status,
            PhotoBytes = photo.Bytes,
            MetadataBytes = metadata.Bytes,
            UpdatedAtUtc = now,
            ReceivedAtUtc = now,
            FailureCode = status == CaptureStatus.Partial ? "BOARD_RECORD_PARTIAL" : null
        };
        await repository.UpdateAsync(completed, cancellationToken);
        if (deviceService is not null)
        {
            await deviceService.NoteCaptureAsync(completed, cancellationToken);
        }
        return completed;
    }

    public Task<CaptureRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        repository.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<CaptureRecord>> ListAsync(
        string? deviceSerial,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken) =>
        repository.ListAsync(deviceSerial, status, from, to, limit, cancellationToken);

    public Task<IReadOnlyList<CaptureRecord>> QueryAsync(
        string? deviceId,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) =>
        repository.QueryAsync(null, deviceId, status, from, to, null, cancellationToken);

    public async Task<CaptureDetailResponse?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var capture = await repository.GetAsync(id, cancellationToken);
        if (capture is null)
        {
            return null;
        }
        if (capture.Status is not (CaptureStatus.Complete or CaptureStatus.Partial))
        {
            return new CaptureDetailResponse(capture, null, null);
        }
        var download = storage.CreateDownloadTarget(capture.PhotoObjectKey);
        var annotated = capture.Analysis is { Status: AnalysisStatus.Completed, AnnotatedObjectKey: { } key }
            ? storage.CreateDownloadTarget(key).Url
            : null;
        return new CaptureDetailResponse(capture, download.Url, download.ExpiresAt) { AnnotatedDownloadUrl = annotated };
    }

    // Operator views draw on GCJ02 maps; raw WGS84 stays in storage.
    public static CaptureRecord ForOperator(CaptureRecord capture) =>
        capture.Location is null ? capture : capture with { Location = GeoConvert.ToGcj02(capture.Location) };

    private CreateCaptureResponse BuildUploadResponse(CaptureRecord capture) => new(
        capture.Id,
        capture.Status,
        storage.CreateUploadTarget(capture.PhotoObjectKey, capture.PhotoSha256, "image/jpeg"),
        storage.CreateUploadTarget(capture.MetadataObjectKey, capture.MetadataSha256, "text/plain; charset=utf-8"));

    private static string InferTimeSource(CreateCaptureRequest request) => request.UploadRoute switch
    {
        "WINDOWS" => "WINDOWS",
        _ => request.BoardTimeValid ? "BOARD" : "SERVER"
    };

    private static void ValidateRequest(CreateCaptureRequest request)
    {
        if (!DeviceSerialPattern().IsMatch(request.DeviceSerial))
        {
            throw new CaptureValidationException("Device serial may contain only 1-64 letters, digits and hyphens.");
        }
        if (request.DeviceId is not null)
        {
            DeviceService.ValidateDeviceId(request.DeviceId);
        }
        if (request.UploadRoute is not ("WINDOWS" or "BOARD_4G"))
        {
            throw new CaptureValidationException("UploadRoute must be WINDOWS or BOARD_4G.");
        }
        if (request.MetadataVersion != 3)
        {
            throw new CaptureValidationException("Only metadata version 3 is accepted.");
        }
        if (request.BoardRecordStatus is not ("COMPLETE" or "PARTIAL"))
        {
            throw new CaptureValidationException("BoardRecordStatus must be COMPLETE or PARTIAL.");
        }
        ValidateSha(request.PhotoSha256, nameof(request.PhotoSha256));
        ValidateSha(request.MetadataSha256, nameof(request.MetadataSha256));
        if (request.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new CaptureValidationException("CapturedAtUtc cannot be more than five minutes in the future.");
        }
    }

    private static void ValidateSha(string value, string name)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new CaptureValidationException($"{name} must contain 64 hexadecimal characters.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceSerialPattern();
}

public sealed class CaptureValidationException(string message) : Exception(message);
public sealed class CaptureConflictException(string message) : Exception(message);
