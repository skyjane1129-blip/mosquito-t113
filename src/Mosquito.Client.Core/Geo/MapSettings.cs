namespace Mosquito.Client.Core.Geo;

// 地图底图配置。TileProvider：amap（高德栅格瓦片，GCJ02，默认）、osm（OpenStreetMap，WGS84）、none（不加载底图）、custom（使用 TileUrlTemplate）。
public sealed record MapSettings(
    string TileProvider,
    string? TileUrlTemplate,
    string TileCoordinateSystem,
    string CacheDirectory,
    int MinZoom,
    int MaxZoom)
{
    public static MapSettings Offline(string cacheDirectory) => new("none", null, "GCJ02", cacheDirectory, 10, 18);
}
