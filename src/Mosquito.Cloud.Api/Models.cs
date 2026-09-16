using System.Text.Json.Serialization;

namespace Mosquito.Cloud.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureStatus
{
    Created,
    Uploading,
    Complete,
    Partial,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandType
{
    Capture,
    // Fast site learning: the board re-searches cells and takes several LBS fixes so the server's
    // learned cell table covers the nearest tower within minutes of arriving at a new site.
    Learn
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandStatus
{
    Pending,
    Dispatched,
    Completed,
    Failed,
    Expired
}

public sealed record EnvironmentReading(
    string Result,
    string ErrorCode,
    int? TemperatureCentiC,
    int? HumidityCentiRh,
    bool? CrcOk,
    long? SampledUptimeMs);

public sealed record PowerReading(
    string Result,
    string ErrorCode,
    int? BatteryMv,
    string? ChargeState,
    bool? VbusGood,
    int? VbusMv,
    int? ChargeCurrentMa,
    string? FaultRegister,
    long? SampledUptimeMs);

// Shape shared with the Windows client GeoSample. Coordinates are stored as reported by the
// board (WGS84 from GNSS/LBS); operator views are converted to GCJ02 by GeoConvert for map drawing.
public sealed record GeoSample(
    double Latitude,
    double Longitude,
    DateTimeOffset? SampledAtUtc,
    string? District,
    string? CoordinateSystem)
{
    public string? Source { get; init; }
    public double? AccuracyMeters { get; init; }

    // LTE cells observed by the board when this sample was taken (raw board samples only).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CellObservation[]? Cells { get; init; }

    // Distance to the configured ground-truth point, filled in by LocationEngine for evaluation.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ReferenceErrorMeters { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

// One LTE cell as reported by the Air780EG (AT+CCED): serving flag, PLMN, tracking area, E-CI, physical
// cell id, channel and signal. No subscriber identifiers are transported.
public sealed record CellObservation(
    bool Serving,
    int Mcc,
    int Mnc,
    long Tac,
    long CellId,
    int Pci,
    long Earfcn,
    int RsrpDbm,
    double? RsrqDb)
{
    public long? RsrpRaw { get; init; }
}

public sealed record CreateCaptureRequest(
    Guid? CaptureId,
    string DeviceSerial,
    string UploadRoute,
    DateTimeOffset CapturedAtUtc,
    double? BoardUptimeSeconds,
    bool BoardTimeValid,
    int MetadataVersion,
    string BoardRecordStatus,
    string PhotoSha256,
    string MetadataSha256,
    EnvironmentReading? Environment,
    PowerReading? Power)
{
    public string? DeviceId { get; init; }
    public string? TriggerSource { get; init; }
    public string? TimeSource { get; init; }
    public Guid? CommandId { get; init; }
    public GeoSample? Location { get; init; }
}

public sealed record CompleteCaptureRequest(long PhotoBytes, long MetadataBytes);

public sealed record UploadTarget(
    string ObjectKey,
    string Url,
    string Method,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Headers);

public sealed record CreateCaptureResponse(
    Guid CaptureId,
    CaptureStatus Status,
    UploadTarget PhotoUpload,
    UploadTarget MetadataUpload);

public sealed record CaptureRecord(
    Guid Id,
    string DeviceSerial,
    CaptureStatus Status,
    string UploadRoute,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    double? BoardUptimeSeconds,
    bool BoardTimeValid,
    int MetadataVersion,
    string BoardRecordStatus,
    string PhotoObjectKey,
    string MetadataObjectKey,
    string PhotoSha256,
    string MetadataSha256,
    long? PhotoBytes,
    long? MetadataBytes,
    EnvironmentReading? Environment,
    PowerReading? Power,
    string? FailureCode)
{
    public string? DeviceId { get; init; }
    public DateTimeOffset? ReceivedAtUtc { get; init; }
    public string? TriggerSource { get; init; }
    public string? TimeSource { get; init; }
    public string? FailureStage { get; init; }
    public Guid? CommandId { get; init; }
    public GeoSample? Location { get; init; }
    // Latest mosquito/egg detection run on this photo (null until an operator asks for one).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EggAnalysis? Analysis { get; init; }
}

public sealed record CaptureDetailResponse(
    CaptureRecord Capture,
    string? PhotoDownloadUrl,
    DateTimeOffset? DownloadUrlExpiresAt)
{
    // Signed URL of the server-rendered annotated JPEG once an analysis has completed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnnotatedDownloadUrl { get; init; }
}

// ---- Mosquito / egg detection (ai/analyze.py) ----

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisStatus
{
    Running,
    Completed,
    Failed
}

// One detected object in original-photo pixel coordinates. Name is "egg" or "mosquito".
public sealed record EggDetection(string Name, double Conf, double X1, double Y1, double X2, double Y2);

public sealed record EggAnalysis(
    AnalysisStatus Status,
    string ModelVersion,
    double Confidence,
    DateTimeOffset StartedAtUtc)
{
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public int? DurationMs { get; init; }
    public int? EggCount { get; init; }
    public int? MosquitoCount { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public int? Tiles { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EggDetection[]? Detections { get; init; }
    public string? AnnotatedObjectKey { get; init; }
    public string? Error { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record AnalysisResponse(Guid CaptureId, EggAnalysis? Analysis, string? AnnotatedDownloadUrl);

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record ObjectInfo(bool Exists, long Bytes, string? Sha256);

// ---- Device registry, heartbeat, commands and locations ----

public sealed record DeviceRecord(
    string DeviceId,
    string? DeviceSerial,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public GeoSample? LastLocation { get; init; }
    public Guid? LatestCaptureId { get; init; }
    public DateTimeOffset? LatestCaptureAtUtc { get; init; }
    public EnvironmentReading? LastEnvironment { get; init; }
    public PowerReading? LastPower { get; init; }
    public string? ImageVersion { get; init; }
    public double? BoardUptimeSeconds { get; init; }
}

public sealed record DeviceCommand(
    Guid Id,
    string DeviceId,
    CommandType Type,
    CommandStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string RequestedBy)
{
    public int? Focus { get; init; }
    // Learn only: number of re-search + LBS rounds the board should run.
    public int? Rounds { get; init; }
    public DateTimeOffset? DispatchedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public Guid? CaptureId { get; init; }
    public string? Error { get; init; }
}

public sealed record HeartbeatRequest(
    string DeviceId,
    string? DeviceSerial,
    string? ImageVersion,
    double? BoardUptimeSeconds,
    EnvironmentReading? Environment,
    PowerReading? Power,
    GeoSample? Location);

public sealed record LocationReport(
    string DeviceId,
    double Latitude,
    double Longitude,
    string CoordinateSystem,
    string Source,
    DateTimeOffset? SampledAtUtc,
    double? AccuracyMeters)
{
    public CellObservation[]? Cells { get; init; }
}

public sealed record CreateCommandRequest(string Type, int? Focus)
{
    public int? Rounds { get; init; }
}
public sealed record CommandAckRequest(string Status, Guid? CaptureId, string? Error);

// Operator-facing device view. lastLocation is GCJ02 for map drawing; lastLocationWgs84 is the raw sample.
public sealed record RemoteDeviceView(
    string DeviceId,
    string? DeviceSerial,
    GeoSample? LastLocation,
    Guid? LatestCaptureId)
{
    public GeoSample? LastLocationWgs84 { get; init; }
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset? LatestCaptureAtUtc { get; init; }
    public EnvironmentReading? LastEnvironment { get; init; }
    public PowerReading? LastPower { get; init; }
    public string? ImageVersion { get; init; }
}

public sealed record PageResponse<T>(T[] Items, string? NextCursor, string Snapshot, bool IsComplete);
