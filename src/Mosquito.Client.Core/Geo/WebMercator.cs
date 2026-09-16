namespace Mosquito.Client.Core.Geo;

// Web 墨卡托（EPSG:3857）像素坐标：zoom 为 z 时世界宽高为 256·2^z 像素，原点在左上角（西经 180°、北纬 85.05°）。
public static class WebMercator
{
    public const int TileSize = 256;
    private const double MaxLatitude = 85.05112878;

    public static (double X, double Y) ToWorld(double longitude, double latitude, double zoom)
    {
        var size = TileSize * Math.Pow(2, zoom);
        var phi = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        var x = (longitude + 180) / 360 * size;
        var y = (1 - Math.Log(Math.Tan(phi) + 1 / Math.Cos(phi)) / Math.PI) / 2 * size;
        return (x, y);
    }

    public static (double Longitude, double Latitude) ToGeo(double x, double y, double zoom)
    {
        var size = TileSize * Math.Pow(2, zoom);
        var longitude = x / size * 360 - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / size))) * 180 / Math.PI;
        return (longitude, latitude);
    }

    public static (int X, int Y) TileOf(double worldX, double worldY) =>
        ((int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));

    // 使 bounds 在 widthPx×heightPx（四周各留 paddingPx）内完整显示的最大 zoom，可为小数。
    public static double FitZoom(GeoBounds bounds, double widthPx, double heightPx, double paddingPx)
    {
        var (left, top) = ToWorld(bounds.MinLongitude, bounds.MaxLatitude, 0);
        var (right, bottom) = ToWorld(bounds.MaxLongitude, bounds.MinLatitude, 0);
        var worldWidth = Math.Max(right - left, 1e-9);
        var worldHeight = Math.Max(bottom - top, 1e-9);
        var availableWidth = Math.Max(widthPx - 2 * paddingPx, 1);
        var availableHeight = Math.Max(heightPx - 2 * paddingPx, 1);
        return Math.Min(Math.Log2(availableWidth / worldWidth), Math.Log2(availableHeight / worldHeight));
    }

    public static double Clamp(double zoom, double min, double max) => Math.Clamp(zoom, min, max);
}
