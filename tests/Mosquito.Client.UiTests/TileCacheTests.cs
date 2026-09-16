using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosquito.Client.Core.Geo;
using Mosquito.Client.Map;

// Tile cache behaviour with a fake HTTP handler: no real network, no real tile provider.
internal static class TileCacheTests
{
    public static async Task RunAsync(Action<bool, string> assert, string directory)
    {
        Directory.CreateDirectory(directory);
        var png = OnePixelPng();
        var handler = new CountingHandler(png);
        var provider = new TileProvider("test", "https://tiles.invalid/{z}/{x}/{y}.png", "GCJ02", 1, 19, "test tiles", null);
        using var cache = new TileCache(provider, directory, handler);

        var first = cache.GetTileAsync(14, 13720, 6704, default);
        var second = cache.GetTileAsync(14, 13720, 6704, default);
        var results = await Task.WhenAll(first, second);
        assert(results[0] is { PixelWidth: 1, PixelHeight: 1 } && ReferenceEquals(results[0], results[1]) && handler.Requests == 1,
            "concurrent requests for one tile share a single download");
        assert(handler.Urls.Single() == "https://tiles.invalid/14/13720/6704.png", "URL template fills z/x/y");
        var again = await cache.GetTileAsync(14, 13720, 6704, default);
        assert(ReferenceEquals(again, results[0]) && handler.Requests == 1, "memory cache serves repeated requests without HTTP");
        var diskPath = Path.Combine(directory, "test", "14", "13720", "6704.png");
        assert(File.Exists(diskPath) && File.ReadAllBytes(diskPath).SequenceEqual(png), "tile bytes persist to the provider/z/x/y disk cache");

        using var fresh = new TileCache(provider, directory, new CountingHandler(png));
        assert(await fresh.GetTileAsync(14, 13720, 6704, default) is not null && fresh.RequestCount == 0, "a new cache instance reads the tile from disk without HTTP");

        handler.Status = HttpStatusCode.InternalServerError;
        assert(await cache.GetTileAsync(14, 1, 1, default) is null && handler.Requests == 2, "server errors yield null instead of an exception");
        assert(await cache.GetTileAsync(14, 1, 1, default) is null && handler.Requests == 2, "a failed tile is not retried within the cooldown");
        assert(await cache.GetTileAsync(14, -1, 1, default) is null && await cache.GetTileAsync(3, 8, 0, default) is null && handler.Requests == 2,
            "out-of-range tile indexes are rejected before any request");

        using var none = new TileCache(TileProvider.None, directory, new CountingHandler(png));
        assert(await none.GetTileAsync(14, 13720, 6704, default) is null && none.RequestCount == 0, "the none provider never requests tiles");

        var settingsProvider = TileProvider.FromSettings(new MapSettings("amap", null, "GCJ02", directory, 10, 18));
        assert(settingsProvider.Key == "amap" && settingsProvider.CoordinateSystem == "GCJ02" && settingsProvider.Url(14, 13720, 6704).Contains("x=13720&y=6704&z=14", StringComparison.Ordinal),
            "amap provider builds the raster tile URL");
        var osm = TileProvider.FromSettings(new MapSettings("osm", null, "GCJ02", directory, 10, 18));
        assert(osm.CoordinateSystem == "WGS84" && osm.UserAgent is not null && osm.Attribution.Contains("OpenStreetMap", StringComparison.Ordinal), "osm provider is WGS84 with attribution and user agent");
        var custom = TileProvider.FromSettings(new MapSettings("custom", "https://example.invalid/{z}/{x}/{y}", "WGS84", directory, 10, 18));
        assert(custom.Key == "custom" && custom.CoordinateSystem == "WGS84" && custom.HasTiles, "custom provider uses the configured template and datum");
        assert(!TileProvider.FromSettings(null).HasTiles && !TileProvider.FromSettings(new MapSettings("none", null, "GCJ02", directory, 10, 18)).HasTiles, "missing or none settings disable tiles");
    }

    private static byte[] OnePixelPng()
    {
        var bitmap = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.SteelBlue, null, new System.Windows.Rect(0, 0, 1, 1));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class CountingHandler(byte[] png) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public List<string> Urls { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            await Task.Delay(30, token);
            return Status == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }
                : new HttpResponseMessage(Status);
        }
    }
}
