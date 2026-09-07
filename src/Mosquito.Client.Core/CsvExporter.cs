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
            "拍摄编号,设备序列号,状态,业务拍摄时间UTC,上传通道,温度°C,湿度%RH,电池电压V,充电状态,故障码");
        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new[]
            {
                capture.Id.ToString("D"),
                capture.DeviceSerial,
                capture.Status,
                capture.CapturedAtUtc.ToString("O"),
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
                capture.FailureCode ?? string.Empty
            };
            await writer.WriteLineAsync(string.Join(',', row.Select(Escape)));
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
