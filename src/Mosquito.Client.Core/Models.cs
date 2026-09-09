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
    string? LastError)
{
    public string? DeviceId { get; init; }
}

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
    string? FailureCode)
{
    public string? DeviceId { get; init; }
    public DateTimeOffset? ReceivedAtUtc { get; init; }
    public string? TriggerSource { get; init; }
    public string? TimeSource { get; init; }
    public string? FailureStage { get; init; }
    public GeoSample? Location { get; init; }
    public string? TransferStatus { get; init; }
    [JsonIgnore]
    public string? LocalPhotoPath { get; init; }
    public string DeviceDisplay => string.IsNullOrWhiteSpace(DeviceId) ? $"编号待确认 · {DeviceSerial}" : DeviceId;
    public string StatusDisplay => Status.ToUpperInvariant() switch
    {
        "COMPLETE" => "完整", "PARTIAL" => "部分完成", "FAILED" => "失败", _ => "待确认"
    };
    public string CapturedDisplay => BeijingTime.Format(CapturedAtUtc);
    public string ReceivedDisplay => BeijingTime.Format(ReceivedAtUtc);
    public string TimeSourceDisplay => TimeSource?.ToUpperInvariant() switch
    {
        "WINDOWS" => "Windows 采集时间", "BOARD" when BoardTimeValid => "板端时间（已校时）",
        "SERVER" => "服务端时间", _ => "时间来源待确认"
    };
}

public sealed record GeoSample(double Latitude, double Longitude, DateTimeOffset? SampledAtUtc, string? District, string? CoordinateSystem)
{
    public bool IsValid => double.IsFinite(Latitude) && double.IsFinite(Longitude) && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
    public string Display => IsValid ? $"{Latitude:F6}, {Longitude:F6} · {District ?? "区域待确认"}" : "无有效定位";
}

public sealed record CloudCaptureDetail(
    CloudCaptureRecord Capture,
    string? PhotoDownloadUrl,
    DateTimeOffset? DownloadUrlExpiresAt);
