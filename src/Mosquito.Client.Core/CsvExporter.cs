using System.Globalization;
using System.Text;

namespace Mosquito.Client.Core;

public static class CsvExporter
{
    public static async Task ExportAsync(
        string path,
        IEnumerable<CloudCaptureRecord> captures,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(
            "采集编号,设备编号,ADB序列号,记录状态,采集时间(北京时间),时间来源,服务端接收时间(北京时间),触发来源,上传通道,温度°C,湿度%RH,电池电压V,充电状态,失败阶段,故障详情,上传状态,纬度,经度,GNSS采样时间(北京时间),坐标系");
        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new[]
            {
                capture.Id.ToString("D"),
                capture.DeviceId ?? "待确认",
                capture.DeviceSerial,
                capture.StatusDisplay,
                capture.CapturedDisplay,
                capture.TimeSourceDisplay,
                capture.ReceivedDisplay,
                capture.TriggerSource ?? "未提供",
                capture.UploadRoute,
                capture.Environment?.TemperatureCentiC is int temperature
                    ? (temperature / 100.0).ToString("F2", CultureInfo.InvariantCulture)
                    : string.Empty,
                capture.Environment?.HumidityCentiRh is int humidity
                    ? (humidity / 100.0).ToString("F2", CultureInfo.InvariantCulture)
                    : string.Empty,
                capture.Power?.BatteryMv is int voltage
                    ? (voltage / 1000.0).ToString("F3", CultureInfo.InvariantCulture)
                    : string.Empty,
                capture.Power?.ChargeState ?? string.Empty,
                capture.FailureStage ?? string.Empty,
                capture.FailureCode ?? string.Empty,
                capture.TransferStatus ?? string.Empty,
                capture.Location?.Latitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                capture.Location?.Longitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                BeijingTime.Format(capture.Location?.SampledAtUtc),
                capture.Location?.CoordinateSystem ?? string.Empty
            };
            await writer.WriteLineAsync(string.Join(',', row.Select(Escape)));
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static string Escape(string value)
    {
        // Text coming from device metadata must not become spreadsheet formulas.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('@') ||
            (value.TrimStart().StartsWith('-') && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
