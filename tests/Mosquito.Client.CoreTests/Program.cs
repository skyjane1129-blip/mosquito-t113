using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mosquito.Client.Core;

try
{
var id = Guid.Parse("a73d7c04-a788-4426-93bf-412e4829fc5e");
var metadataText = $$"""
    metadata_version=3
    capture_id={{id:D}}
    record_status=COMPLETE
    photo=/mnt/UDISK/mosquito-test/camera/photo.jpg
    system_time_valid=no
    uptime_seconds=60775.84
    mode=manual-focus
    focus_selected=500
    jpeg_width=3264
    jpeg_height=2448
    jpeg_bytes=365352
    jpeg_sha256=3161988ac96a1437142ce30587642a5156e134aeeb612fef9d706bc48cb88ca1

    [environment]
    RESULT=PASS
    ERROR_CODE=NONE
    SAMPLED_UPTIME_MS=60769603
    TEMPERATURE_CENTI_C=3016
    HUMIDITY_CENTI_RH=4608
    CRC_OK=1

    [power]
    RESULT=PASS
    ERROR_CODE=NONE
    SAMPLED_UPTIME_MS=60769712
    BATTERY_MV=4124
    VBUS_GOOD=1
    VBUS_MV=4900
    CHARGE_STATE=CHARGE_DONE
    CHARGE_CURRENT_MA=0
    FAULT_REG=0x00
    """;

var parser = new KeyValueMetadataParser();
var parsed = parser.Parse(metadataText, id);
Assert(parsed.Version == 3, "metadata version");
Assert(parsed.Environment.TemperatureDisplay == "30.16 °C", "temperature display");
Assert(parsed.Environment.HumidityDisplay == "46.08 %RH", "humidity display");
Assert(parsed.Power.BatteryDisplay == "4.124 V", "battery is voltage, not a fabricated percentage");
Assert(parsed.Power.ChargeDisplay == "充电完成", "charge state localization");
Assert(!parsed.BoardTimeValid, "invalid board clock preserved");

var wrongIdRejected = false;
try
{
    _ = parser.Parse(metadataText, Guid.NewGuid());
}
catch (InvalidDataException)
{
    wrongIdRejected = true;
}
Assert(wrongIdRejected, "capture UUID mismatch rejected");

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mosquito-client-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryRoot);
try
{
    var baseSettingsPath = Path.Combine(temporaryRoot, "appsettings.json");
    var localSettingsPath = Path.Combine(temporaryRoot, "appsettings.Local.json");
    await File.WriteAllTextAsync(baseSettingsPath, JsonSerializer.Serialize(new AppSettings
    {
        DeviceSerial = "BASE-SERIAL",
        DeviceId = null,
        ApiBaseUrl = "https://base.invalid",
        LocalDataRoot = temporaryRoot,
        CommandTimeoutSeconds = 91,
        DefaultFocus = 500
    }));
    await File.WriteAllTextAsync(localSettingsPath, """
        {
          "deviceId": "MQ-SH-LOCAL-001",
          "apiBaseUrl": "https://local.invalid"
        }
        """);
    var layeredSettings = AppSettings.Load(baseSettingsPath);
    Assert(layeredSettings.DeviceId == "MQ-SH-LOCAL-001" && layeredSettings.ApiBaseUrl == "https://local.invalid",
        "local settings override matching base properties case-insensitively");
    Assert(layeredSettings.DeviceSerial == "BASE-SERIAL" && layeredSettings.CommandTimeoutSeconds == 91 && layeredSettings.DefaultFocus == 500,
        "local settings preserve base properties they do not specify");
    File.Delete(localSettingsPath);
    var baseOnlySettings = AppSettings.Load(baseSettingsPath);
    Assert(baseOnlySettings.DeviceId is null && baseOnlySettings.ApiBaseUrl == "https://base.invalid",
        "missing optional local settings leave the base configuration unchanged");
    // The installer writes an app-relative ADB path; it must resolve next to the settings file.
    await File.WriteAllTextAsync(localSettingsPath, """{ "adbPath": "platform-tools\\adb.exe" }""");
    var relativeAdb = AppSettings.Load(baseSettingsPath);
    Assert(relativeAdb.AdbPath == Path.Combine(temporaryRoot, "platform-tools", "adb.exe"),
        "relative AdbPath resolves against the settings directory (bundled ADB)");
    await File.WriteAllTextAsync(localSettingsPath, """{ "adbPath": "C:\\embedded\\android-platform-tools\\adb.exe" }""");
    Assert(AppSettings.Load(baseSettingsPath).AdbPath == @"C:\embedded\android-platform-tools\adb.exe",
        "absolute AdbPath is left unchanged");
    File.Delete(localSettingsPath);

    var photoPath = Path.Combine(temporaryRoot, "photo.jpg");
    var metadataPath = Path.Combine(temporaryRoot, "metadata.txt");
    await File.WriteAllBytesAsync(photoPath, "photo"u8.ToArray());
    await File.WriteAllTextAsync(metadataPath, metadataText);
    var artifact = new CaptureArtifact(
        id,
        "MOSQUITO-T113-DEV",
        UploadRoute.Windows,
        DateTimeOffset.Parse("2026-09-01T06:09:55Z"),
        photoPath,
        metadataPath,
        new string('a', 64),
        new string('b', 64),
        5,
        Encoding.UTF8.GetByteCount(metadataText),
        parsed,
        CaptureState.PendingUpload,
        null);
    var outbox = new OutboxRepository(temporaryRoot);
    await outbox.InitializeAsync(CancellationToken.None);
    await outbox.SaveAsync(artifact, CancellationToken.None);
    Assert((await outbox.GetPendingAsync(CancellationToken.None)).Count == 1, "pending capture persisted");
    await outbox.MarkAsync(artifact, CaptureState.Complete, null, true, CancellationToken.None);
    Assert((await outbox.GetPendingAsync(CancellationToken.None)).Count == 0, "completed capture leaves retry queue");

    var csvPath = Path.Combine(temporaryRoot, "captures.csv");
    await CsvExporter.ExportAsync(csvPath, [new CloudCaptureRecord(
        id,
        "MOSQUITO-T113-DEV",
        "Complete",
        "WINDOWS",
        artifact.HostCapturedAtUtc,
        false,
        365352,
        7043,
        parsed.Environment,
        parsed.Power,
        null)], CancellationToken.None);
    var csv = await File.ReadAllTextAsync(csvPath);
    Assert(csv.Contains("30.16", StringComparison.Ordinal), "CSV temperature");
    Assert(csv.Contains("4.124", StringComparison.Ordinal), "CSV battery voltage");
    // Photo naming: <device id or serial>_<Beijing capture time>.jpg, e.g. MQ-SH-001_20260916_111850.jpg
    var named = new CloudCaptureRecord(id, "MOSQUITO-T113-DEV", "Complete", "BOARD_4G",
        new DateTimeOffset(2026, 9, 16, 3, 18, 50, TimeSpan.Zero), true, 1, 1, null, null, null) { DeviceId = "MQ-SH-001" };
    Assert(named.PhotoName == "MQ-SH-001_20260916_111850.jpg", $"photo name uses device id and Beijing time ({named.PhotoName})");
    Assert(named.AnnotatedPhotoName == "MQ-SH-001_20260916_111850_蚊卵标注.jpg", "annotated photo name adds a suffix");
    Assert((named with { DeviceId = null }).PhotoName == "MOSQUITO-T113-DEV_20260916_111850.jpg", "photo name falls back to the ADB serial");
    Assert(csv.TrimStart('﻿').StartsWith("照片名称,", StringComparison.Ordinal) && csv.Contains("MOSQUITO-T113-DEV_", StringComparison.Ordinal), "CSV leads with the photo name");
    await HistoryTests.RunAsync(Assert, temporaryRoot, outbox, artifact);
    await PhotoExportTests.RunAsync(Assert, temporaryRoot);
    MapTests.Run(Assert);
}
finally
{
    // Disposed connections remain pooled; release Windows file handles before cleanup.
    SqliteConnection.ClearAllPools();
    Directory.Delete(temporaryRoot, true);
}

Console.WriteLine("PASS: metadata v3 parsing and UUID integrity");
Console.WriteLine("PASS: real sensor units without fake battery percentage");
Console.WriteLine("PASS: optional appsettings.Local.json property overrides");
Console.WriteLine("PASS: SQLite retry outbox state transitions");
Console.WriteLine("PASS: UTF-8 CSV export");
Console.WriteLine("PASS: batch photo export (naming, annotated pictures, per-record failures, cancel)");
Console.WriteLine("PASS: Shanghai district atlas, Web Mercator and GCJ02 conversion");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Assertion failed: {message}");
    }
}
