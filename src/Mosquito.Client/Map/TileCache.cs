using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Mosquito.Client.Core.Geo;

namespace Mosquito.Client.Map;

// A raster tile source. Coordinates of device points and boundaries are GCJ02; a WGS84 provider makes the map convert them.
public sealed record TileProvider(string Key, string UrlTemplate, string CoordinateSystem, int MinZoom, int MaxZoom, string Attribution, string? UserAgent)
{
    private static readonly string[] Subdomains = ["1", "2", "3", "4"];
    public static readonly TileProvider None = new("none", "", "GCJ02", 1, 19, "未加载底图", null);
    public static readonly TileProvider AMap = new("amap",
        "https://webrd0{s}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}",
        "GCJ02", 3, 18, "底图 © 高德地图", null);
    public static readonly TileProvider OpenStreetMap = new("osm",
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        "WGS84", 0, 19, "底图 © OpenStreetMap contributors", "MosquitoCapture/1.0 (SCDC mosquito monitoring demo)");

    public bool HasTiles => Key != "none" && !string.IsNullOrWhiteSpace(UrlTemplate);

    public static TileProvider FromSettings(MapSettings? settings)
    {
        if (settings is null) return None;
        var provider = settings.TileProvider switch
        {
            "amap" => AMap,
            "osm" => OpenStreetMap,
            "none" => None,
            _ => settings.TileUrlTemplate is null ? None : new TileProvider("custom", settings.TileUrlTemplate, settings.TileCoordinateSystem, 1, 19, "底图：自定义瓦片服务", null)
        };
        if (provider.Key == "custom" || settings.TileUrlTemplate is null) return provider;
        // A template on a named provider overrides its URL (e.g. a mirror) but keeps the datum and attribution.
        return provider with { UrlTemplate = settings.TileUrlTemplate };
    }

    public string Url(int z, int x, int y) => UrlTemplate
        .Replace("{s}", Subdomains[(x + y) & 3], StringComparison.Ordinal)
        .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

// Memory + disk tile cache with in-flight de-duplication. Safe to call from the UI thread; decoding happens off it.
public sealed class TileCache : IDisposable
{
    private const int MemoryCapacity = 400;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly TileProvider _provider;
    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly object _memoryLock = new();
    private readonly Dictionary<(int Z, int X, int Y), LinkedListNode<((int Z, int X, int Y) Key, BitmapSource Image)>> _memory = new();
    private readonly LinkedList<((int Z, int X, int Y) Key, BitmapSource Image)> _recency = new();
    private readonly ConcurrentDictionary<(int Z, int X, int Y), Lazy<Task<BitmapSource?>>> _inFlight = new();
    private readonly ConcurrentDictionary<(int Z, int X, int Y), DateTimeOffset> _failures = new();
    private int _requests;

    public TileCache(TileProvider provider, string cacheDirectory, HttpMessageHandler? handler = null)
    {
        _provider = provider;
        _directory = cacheDirectory;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(15);
        if (provider.UserAgent is not null) _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", provider.UserAgent);
    }

    public TileProvider Provider => _provider;
    // Number of HTTP requests actually issued (memory and disk hits do not count).
    public int RequestCount => Volatile.Read(ref _requests);

    public Task<BitmapSource?> GetTileAsync(int z, int x, int y, CancellationToken token)
    {
        if (!_provider.HasTiles || z < 0 || z > 22 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z) return Task.FromResult<BitmapSource?>(null);
        var key = (z, x, y);
        if (TryGetMemory(key, out var cached)) return Task.FromResult<BitmapSource?>(cached);
        if (_failures.TryGetValue(key, out var failedAt) && DateTimeOffset.UtcNow - failedAt < FailureCooldown) return Task.FromResult<BitmapSource?>(null);
        // Every caller shares one download (Lazy guarantees a single LoadAsync even under contention);
        // cancelling one viewer must not abort the others.
        var task = _inFlight.GetOrAdd(key, k => new Lazy<Task<BitmapSource?>>(() => LoadAsync(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }

    private async Task<BitmapSource?> LoadAsync((int Z, int X, int Y) key)
    {
        try
        {
            var image = await Task.Run(() => LoadCoreAsync(key)).ConfigureAwait(false);
            if (image is null) _failures[key] = DateTimeOffset.UtcNow;
            else { _failures.TryRemove(key, out _); Remember(key, image); }
            return image;
        }
        finally { _inFlight.TryRemove(key, out _); }
    }

    private async Task<BitmapSource?> LoadCoreAsync((int Z, int X, int Y) key)
    {
        var path = DiskPath(key);
        byte[]? bytes = null;
        try { if (path is not null && File.Exists(path)) bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (bytes is null || bytes.Length == 0)
        {
            try
            {
                Interlocked.Increment(ref _requests);
                using var response = await _http.GetAsync(_provider.Url(key.Z, key.X, key.Y), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) { return null; }
            if (bytes.Length == 0) return null;
            if (path is not null) await WriteDiskAsync(path, bytes).ConfigureAwait(false);
        }
        return Decode(bytes);
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException) { return null; }
    }

    private string? DiskPath((int Z, int X, int Y) key) =>
        string.IsNullOrWhiteSpace(_directory) ? null :
        Path.Combine(_directory, _provider.Key, key.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key.X.ToString(System.Globalization.CultureInfo.InvariantCulture), key.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");

    private static async Task WriteDiskAsync(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private bool TryGetMemory((int Z, int X, int Y) key, out BitmapSource image)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _recency.Remove(node); _recency.AddFirst(node);
                image = node.Value.Image; return true;
            }
        }
        image = null!; return false;
    }

    private void Remember((int Z, int X, int Y) key, BitmapSource image)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(key, out var existing)) _recency.Remove(existing);
            var node = _recency.AddFirst((key, image));
            _memory[key] = node;
            while (_memory.Count > MemoryCapacity && _recency.Last is { } last)
            {
                _recency.RemoveLast(); _memory.Remove(last.Value.Key);
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
