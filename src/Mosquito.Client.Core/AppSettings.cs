using System.Text.Json;

namespace Mosquito.Client.Core;

public sealed class AppSettings
{
    public string AdbPath { get; set; } = @"C:\embedded\android-platform-tools\adb.exe";
    public string DeviceSerial { get; set; } = "MOSQUITO-T113-DEV";
    public string BoardCommandPath { get; set; } = "/data/local/tmp/client-demo:/usr/bin:/bin";
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string LocalDataRoot { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 180;
    public int DefaultFocus { get; set; } = 500;

    public static AppSettings Load(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppSettings()
            : new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.LocalDataRoot))
        {
            settings.LocalDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MosquitoCapture");
        }
        return settings;
    }
}
