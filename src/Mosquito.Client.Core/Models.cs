using System.Text.Json.Serialization;

namespace Mosquito.Client.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadRoute
{
    Windows,
    Board4G
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureState
{
    Local,
    PendingUpload,
    Uploading,
    Complete,
    Partial,
    Failed
}

public sealed record EnvironmentReading(
    string Result,
    string ErrorCode,
    int? TemperatureCentiC,
    int? HumidityCentiRh,
    bool? CrcOk,
    long? SampledUptimeMs)
{
    public string TemperatureDisplay => TemperatureCentiC is null ? "--" : $"{TemperatureCentiC / 100.0:F2} °C";
    public string HumidityDisplay => HumidityCentiRh is null ? "--" : $"{HumidityCentiRh / 100.0:F2} %RH";
}

public sealed record PowerReading(
    string Result,
    string ErrorCode,
    int? BatteryMv,
    string? ChargeState,
    bool? VbusGood,
    int? VbusMv,
    int? ChargeCurrentMa,
    string? FaultRegister,
    long? SampledUptimeMs)
{
    public string BatteryDisplay => BatteryMv is null ? "--" : $"{BatteryMv / 1000.0:F3} V";
    public string ChargeDisplay => ChargeState switch
    {
        "CHARGE_DONE" => "充电完成",
        "FAST_CHARGE" => "快速充电",
        "PRE_CHARGE" => "预充电",
        "NOT_CHARGING" => "未充电",
        _ => ChargeState ?? "未知"
    };
}

public sealed record ParsedMetadata(
    int Version,
    Guid CaptureId,
    string RecordStatus,
    string PhotoPath,
    string JpegSha256,
    long JpegBytes,
    int JpegWidth,
    int JpegHeight,
    bool BoardTimeValid,
    double? BoardUptimeSeconds,
    string CaptureMode,
    int? FocusSelected,
    EnvironmentReading Environment,
    PowerReading Power,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections);

public sealed record CaptureArtifact(
    Guid CaptureId,
    string DeviceSerial,
    UploadRoute UploadRoute,
    DateTimeOffset HostCapturedAtUtc,
    string LocalPhotoPath,
    string LocalMetadataPath,
    string PhotoSha256,
    string MetadataSha256,
    long PhotoBytes,
    long MetadataBytes,
    ParsedMetadata Metadata,
    CaptureState State,
    string? LastError);

public sealed record AdbDevice(string Serial, string State, string Details);
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

public sealed record CloudUploadTarget(
    string ObjectKey,
    string Url,
    string Method,
    DateTimeOffset ExpiresAt,
    Dictionary<string, string> Headers);

public sealed record CloudCreateResponse(
    Guid CaptureId,
    string Status,
    CloudUploadTarget PhotoUpload,
    CloudUploadTarget MetadataUpload);

public sealed record CloudCaptureRecord(
    Guid Id,
    string DeviceSerial,
    string Status,
    string UploadRoute,
    DateTimeOffset CapturedAtUtc,
    bool BoardTimeValid,
    long? PhotoBytes,
    long? MetadataBytes,
    EnvironmentReading? Environment,
    PowerReading? Power,
    string? FailureCode);

public sealed record CloudCaptureDetail(
    CloudCaptureRecord Capture,
    string? PhotoDownloadUrl,
    DateTimeOffset? DownloadUrlExpiresAt);
