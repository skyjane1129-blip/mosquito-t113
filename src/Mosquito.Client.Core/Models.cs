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
    // Latest server-side mosquito/egg detection on this photo (null until requested).
    public EggAnalysis? Analysis { get; init; }
    [JsonIgnore]
    public string? LocalPhotoPath { get; init; }
    public string DeviceDisplay => string.IsNullOrWhiteSpace(DeviceId) ? $"编号待确认 · {DeviceSerial}" : DeviceId;
    // Photo file name shown to operators, following the convention of pest-monitoring lamps and camera
    // traps: <device id>_<capture time, Beijing, yyyyMMdd_HHmmss>.jpg — sortable, unique per device and
    // readable without opening the record. Falls back to the ADB serial while the device id is unassigned.
    public string PhotoName =>
        $"{(string.IsNullOrWhiteSpace(DeviceId) ? DeviceSerial : DeviceId)}_{CapturedAtUtc.ToOffset(BeijingTime.Offset):yyyyMMdd_HHmmss}.jpg";
    public string AnnotatedPhotoName => PhotoName[..^4] + "_蚊卵标注.jpg";
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
    // Server-side positioning source: GNSS satellite fix, LBS single-cell estimate, a named
    // multi-cell provider (LBS_AMAP / LBS_BAIDU / CELL_OPENCELLID), or the server's fused result.
    public string? Source { get; init; }
    public double? AccuracyMeters { get; init; }
    public bool IsValid => double.IsFinite(Latitude) && double.IsFinite(Longitude) && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
    public string Display => IsValid ? $"{Latitude:F6}, {Longitude:F6} · {District ?? "区域待确认"}" : "无有效定位";
    // Estimated radius the server attached to the estimate; shown so the operator reads the coarseness.
    public string AccuracyDisplay => AccuracyMeters is { } m && double.IsFinite(m) && m > 0
        ? (m >= 1000 ? $"约 ±{m / 1000:F1} km" : $"约 ±{m:F0} m")
        : "精度未知";
    public string SourceDisplay => Source?.ToUpperInvariant() switch
    {
        "GNSS" => "卫星定位（GNSS）",
        "CELL_LEARNED_MEDIAN" => "基站定位（自学习最强小区，多次中值）",
        "CELL_LEARNED" => "基站定位（自学习最强小区）",
        "FUSED_MEDIAN" => "融合定位（多次中值）",
        "FUSED" => "融合定位（多源加权）",
        "LBS_AMAP" => "多基站定位（高德）",
        "LBS_BAIDU" => "多基站定位（百度）",
        "CELL_OPENCELLID" => "基站库定位（OpenCellID）",
        "LBS" or "LBS_SINGLE_CELL" => "单基站定位（LBS，精度较粗）",
        null => "定位方式待确认",
        _ => Source
    };
}

// Operator-issued board command as returned by /api/v2/devices/{id}/commands and /api/v2/commands/{id}.
public sealed record RemoteCommand(Guid Id, string DeviceId, string Type, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public string? RequestedBy { get; init; }
    public int? Focus { get; init; }
    public int? Rounds { get; init; }
    public DateTimeOffset? DispatchedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public Guid? CaptureId { get; init; }
    public string? Error { get; init; }
    public bool IsFinal => Status.ToUpperInvariant() is "COMPLETED" or "FAILED" or "EXPIRED";
    // "Learn" = fast site learning (modem re-search + LBS rounds); anything else is a capture.
    public bool IsLearn => string.Equals(Type, "Learn", StringComparison.OrdinalIgnoreCase);
    public string StatusDisplay => (Status.ToUpperInvariant(), IsLearn) switch
    {
        ("PENDING", _) => "等待板子经 4G 领取指令",
        ("DISPATCHED", false) => "板子已领取，正在拍照并上传",
        ("DISPATCHED", true) => "板子已领取，正在重搜网并逐轮定位学习",
        ("COMPLETED", false) => "远程拍照完成",
        ("COMPLETED", true) => "快速定位学习完成",
        ("FAILED", false) => "远程拍照失败",
        ("FAILED", true) => "快速定位学习失败",
        ("EXPIRED", _) => "指令超时，板子未在有效期内领取",
        _ => Status
    };
}

public sealed record CloudCaptureDetail(
    CloudCaptureRecord Capture,
    string? PhotoDownloadUrl,
    DateTimeOffset? DownloadUrlExpiresAt)
{
    // Signed URL of the server-rendered annotated JPEG once an analysis completed.
    public string? AnnotatedDownloadUrl { get; init; }
}

// ---- Mosquito / egg detection (server runs the trained YOLO model; see mosquito-cloud-service/ai) ----

public sealed record EggDetection(string Name, double Conf, double X1, double Y1, double X2, double Y2);

public sealed record EggAnalysis(string Status, string ModelVersion, double Confidence, DateTimeOffset StartedAtUtc)
{
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public int? DurationMs { get; init; }
    public int? EggCount { get; init; }
    public int? MosquitoCount { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public int? Tiles { get; init; }
    public EggDetection[]? Detections { get; init; }
    public string? AnnotatedObjectKey { get; init; }
    public string? Error { get; init; }
    public string? RequestedBy { get; init; }
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
    public bool IsFinal => IsCompleted || IsFailed;
    // One line for the photo pane: counts first, then how the result was produced.
    public string Summary => IsCompleted
        ? $"蚊卵 {EggCount ?? 0} 个 · 蚊子 {MosquitoCount ?? 0} 只 · 模型 {ModelVersion} · 置信度阈值 {Confidence:0.00}" +
          (DurationMs is { } ms ? $" · 用时 {ms / 1000.0:0.#} 秒" : "") + $" · {BeijingTime.Format(CompletedAtUtc)}"
        : IsFailed ? $"识别失败：{Error ?? "未知错误"}" : IsRunning ? "正在识别…" : Status;
}

public sealed record CloudAnalysisResponse(Guid CaptureId, EggAnalysis? Analysis, string? AnnotatedDownloadUrl);
