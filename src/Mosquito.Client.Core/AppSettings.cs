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
    // Remote-mode map base tiles. "amap" needs no key but is an unofficial raster endpoint (demo only);
    // "osm" is WGS84 and needs attribution; "none" draws boundaries only; "custom" uses MapTileUrlTemplate.
    public string MapTileProvider { get; set; } = "amap";
    public string? MapTileUrlTemplate { get; set; }
    public string MapTileCoordinateSystem { get; set; } = "GCJ02";
    public string MapTileCacheDirectory { get; set; } = string.Empty;
    public int MapMinZoom { get; set; } = 10;
    public int MapMaxZoom { get; set; } = 18;

    public Geo.MapSettings ToMapSettings() => new(
        string.IsNullOrWhiteSpace(MapTileProvider) ? "none" : MapTileProvider.Trim().ToLowerInvariant(),
        string.IsNullOrWhiteSpace(MapTileUrlTemplate) ? null : MapTileUrlTemplate,
        string.IsNullOrWhiteSpace(MapTileCoordinateSystem) ? "GCJ02" : MapTileCoordinateSystem.Trim().ToUpperInvariant(),
        string.IsNullOrWhiteSpace(MapTileCacheDirectory) ? Path.Combine(LocalDataRoot, "tiles") : MapTileCacheDirectory,
        Math.Clamp(MapMinZoom, 1, 19),
        Math.Clamp(Math.Max(MapMaxZoom, MapMinZoom), 1, 19));

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
        // The installer ships its own ADB under the application directory and writes a relative
        // AdbPath ("platform-tools\adb.exe"); resolve it against the settings file location so the
        // client never depends on a machine-wide ADB install or PATH.
        if (!string.IsNullOrWhiteSpace(settings.AdbPath) && !Path.IsPathRooted(settings.AdbPath))
        {
            settings.AdbPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, settings.AdbPath));
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
