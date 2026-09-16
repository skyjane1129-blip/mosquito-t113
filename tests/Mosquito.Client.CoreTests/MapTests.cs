using Mosquito.Client.Core;
using Mosquito.Client.Core.Geo;

internal static class MapTests
{
    public static void Run(Action<bool, string> assert)
    {
        var atlas = DistrictAtlas.Shanghai;
        var expected = new[]
        {
            "黄浦区", "徐汇区", "长宁区", "静安区", "普陀区", "虹口区", "杨浦区", "闵行区",
            "宝山区", "嘉定区", "浦东新区", "金山区", "松江区", "青浦区", "奉贤区", "崇明区"
        };
        assert(atlas.Districts.Select(x => x.Name).SequenceEqual(expected), "atlas loads the 16 Shanghai districts in display order");
        assert(atlas.CoordinateSystem == "GCJ02" && atlas.Attribution.Contains("DataV.GeoAtlas"), "atlas declares GCJ02 and its attribution");
        assert(atlas.Districts.All(x => x.Polygons.Count >= 1 && x.Polygons.All(p => p.Outer.Points.Length >= 3)), "every district has at least one valid polygon");
        assert(atlas.Bounds.MinLongitude > 120.8 && atlas.Bounds.MaxLongitude < 122.3 && atlas.Bounds.MinLatitude > 30.6 && atlas.Bounds.MaxLatitude < 32.0,
            "city bounds stay within the Shanghai area");

        var byArea = atlas.Districts.OrderByDescending(x => x.AreaSquareKm).ToArray();
        Console.WriteLine("district areas km²: " + string.Join(", ", byArea.Select(x => $"{x.Name}={x.AreaSquareKm:F0}")));
        assert(new[] { byArea[0].Name, byArea[1].Name }.Order().SequenceEqual(new[] { "崇明区", "浦东新区" }.Order()),
            "Chongming and Pudong are the two largest districts");
        assert(byArea[0].AreaSquareKm > 1000 && byArea[^1].Name == "黄浦区" && byArea[^1].AreaSquareKm < 30,
            "largest district exceeds 1000 km² and Huangpu is the smallest below 30 km²");
        assert(atlas.Find("崇明区")!.Polygons.Count > 1, "Chongming keeps its islands as separate polygons");

        assert(atlas.Locate(31.2305, 121.4737) == "黄浦区", "People's Square resolves to Huangpu");
        assert(atlas.Locate(31.2453, 121.5065) == "浦东新区", "Oriental Pearl resolves to Pudong");
        assert(atlas.Locate(31.204, 121.588) == "浦东新区", "Zhangjiang resolves to Pudong");
        assert(atlas.Locate(31.626, 121.397) == "崇明区", "Chengqiao resolves to Chongming");
        assert(atlas.Locate(31.031, 121.228) == "松江区", "Songjiang town resolves to Songjiang");
        assert(atlas.Locate(31.0, 122.5) is null, "open sea resolves to no district");
        foreach (var (lat, lon) in new[] { (31.211, 121.562), (31.221, 121.572), (31.231, 121.582) })
            assert(atlas.InDistrict("浦东新区", lat, lon) && !atlas.InDistrict("黄浦区", lat, lon), $"UI fixture point {lat},{lon} lies in Pudong only");
        assert(new GeoSample(31.2305, 121.4737, null, null, "GCJ02") is { IsValid: true }, "GeoSample validity unchanged");

        var (x0, y0) = WebMercator.ToWorld(0, 0, 0);
        assert(Math.Abs(x0 - 128) < 1e-9 && Math.Abs(y0 - 128) < 1e-9, "zoom 0 maps the origin to the world centre");
        foreach (var (lon, lat, zoom) in new[] { (121.4737, 31.2305, 12.0), (-73.9857, 40.7484, 15.5), (151.2093, -33.8688, 3.0) })
        {
            var (wx, wy) = WebMercator.ToWorld(lon, lat, zoom);
            var (lon2, lat2) = WebMercator.ToGeo(wx, wy, zoom);
            assert(Math.Abs(lon2 - lon) < 1e-9 && Math.Abs(lat2 - lat) < 1e-9, $"Mercator round trip at zoom {zoom}");
        }
        var (tx, ty) = WebMercator.TileOf(WebMercator.ToWorld(121.4737, 31.2305, 14).X, WebMercator.ToWorld(121.4737, 31.2305, 14).Y);
        assert(tx == 13720 && ty == 6694, $"tile index of People's Square at zoom 14 is 13720/6694 (got {tx}/{ty})");
        var cityFit = WebMercator.FitZoom(atlas.Bounds, 900, 500, 16);
        var pudongFit = WebMercator.FitZoom(atlas.Find("浦东新区")!.Bounds, 900, 500, 24);
        var huangpuFit = WebMercator.FitZoom(atlas.Find("黄浦区")!.Bounds, 900, 500, 24);
        assert(cityFit < pudongFit && pudongFit < huangpuFit && cityFit is > 8 and < 11, $"FitZoom grows as the area shrinks (city {cityFit:F2})");
        assert(WebMercator.Clamp(25, 10, 18) == 18 && WebMercator.Clamp(3, 10, 18) == 10, "zoom clamp");

        var (gLon, gLat) = ChinaCoordinates.Wgs84ToGcj02(121.4692, 31.2331);
        assert(gLon > 121.4692 && Math.Abs(gLon - 121.4692) < 0.01 && Math.Abs(gLat - 31.2331) < 0.01, "GCJ02 offset in Shanghai is a few hundred metres eastwards");
        var (wLon, wLat) = ChinaCoordinates.Gcj02ToWgs84(gLon, gLat);
        assert(Math.Abs(wLon - 121.4692) < 1e-6 && Math.Abs(wLat - 31.2331) < 1e-6, "GCJ02 -> WGS84 inverse round trip within 1e-6 degrees");
        assert(ChinaCoordinates.IsInChina(121.47, 31.23) && !ChinaCoordinates.IsInChina(139.69, 35.69), "IsInChina for Shanghai and Tokyo");
        var (tLon, tLat) = ChinaCoordinates.Wgs84ToGcj02(139.69, 35.69);
        assert(tLon == 139.69 && tLat == 35.69, "coordinates outside China are left untouched");

        var settings = new AppSettings { LocalDataRoot = @"C:\data", MapTileProvider = " AMap ", MapMaxZoom = 5, MapMinZoom = 12 };
        var map = settings.ToMapSettings();
        assert(map.TileProvider == "amap" && map.CacheDirectory == @"C:\data\tiles" && map.MinZoom == 12 && map.MaxZoom == 12,
            "map settings normalise provider, default cache directory and zoom range");
    }
}
