using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosquito.Client.Core;

public sealed class AppSettings
{
    public string AdbPath { get; set; } = @"C:\embedded\android-platform-tools\adb.exe";
    public string DeviceSerial { get; set; } = "MOSQUITO-T113-DEV";
    // Provisioned stable identity; never silently treat an ADB connection serial as a registry ID.
    public string? DeviceId { get; set; }
    public string BoardCommandPath { get; set; } = "/usr/bin:/bin";
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string LocalDataRoot { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 180;
    public int DefaultFocus { get; set; } = 500;

    public static AppSettings Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var values = ReadObject(path);
        var localPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "appsettings.Local.json");
        if (File.Exists(localPath))
        {
            foreach (var property in ReadObject(localPath))
            {
                var existingName = values.Select(item => item.Key)
                    .FirstOrDefault(name => string.Equals(name, property.Key, StringComparison.OrdinalIgnoreCase));
                if (existingName is not null)
                {
                    values.Remove(existingName);
                }
                values[property.Key] = property.Value?.DeepClone();
            }
        }
        var settings = values.Deserialize<AppSettings>(options) ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.LocalDataRoot))
        {
            settings.LocalDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MosquitoCapture");
        }
        return settings;
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException($"Configuration root must be a JSON object: {path}");
    }
}
