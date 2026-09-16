namespace Mosquito.Client.Core.Geo;

// 经纬度点，坐标顺序与 GeoJSON 一致（先经度后纬度）。
public readonly record struct GeoPoint(double Longitude, double Latitude);

public readonly record struct GeoBounds(double MinLongitude, double MinLatitude, double MaxLongitude, double MaxLatitude)
{
    public GeoPoint Center => new((MinLongitude + MaxLongitude) / 2, (MinLatitude + MaxLatitude) / 2);

    public bool Contains(GeoPoint point) =>
        point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude &&
        point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude;

    public GeoBounds Union(GeoBounds other) => new(
        Math.Min(MinLongitude, other.MinLongitude), Math.Min(MinLatitude, other.MinLatitude),
        Math.Max(MaxLongitude, other.MaxLongitude), Math.Max(MaxLatitude, other.MaxLatitude));

    public static GeoBounds Of(IEnumerable<GeoPoint> points)
    {
        double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;
        var any = false;
        foreach (var point in points)
        {
            any = true;
            minLon = Math.Min(minLon, point.Longitude); maxLon = Math.Max(maxLon, point.Longitude);
            minLat = Math.Min(minLat, point.Latitude); maxLat = Math.Max(maxLat, point.Latitude);
        }
        if (!any) throw new ArgumentException("至少需要一个点才能计算范围。", nameof(points));
        return new(minLon, minLat, maxLon, maxLat);
    }
}

// 环：顶点不重复首点（解析时已去掉 GeoJSON 的闭合点）。
public sealed record GeoRing(GeoPoint[] Points);

public sealed record GeoPolygon(GeoRing Outer, GeoRing[] Holes);

public sealed class DistrictShape
{
    private const double EarthRadiusKm = 6371.0088;

    public DistrictShape(string name, string adCode, GeoPoint center, GeoPoint centroid, IReadOnlyList<GeoPolygon> polygons)
    {
        if (polygons.Count == 0) throw new ArgumentException($"行政区“{name}”没有任何多边形。", nameof(polygons));
        Name = name; AdCode = adCode; Center = center; Centroid = centroid; Polygons = polygons;
        Bounds = GeoBounds.Of(polygons.SelectMany(polygon => polygon.Outer.Points));
        AreaSquareKm = polygons.Sum(polygon => SphericalArea(polygon.Outer) - polygon.Holes.Sum(SphericalArea));
    }

    public string Name { get; }
    public string AdCode { get; }
    public GeoPoint Center { get; }
    public GeoPoint Centroid { get; }
    public IReadOnlyList<GeoPolygon> Polygons { get; }
    public GeoBounds Bounds { get; }
    public double AreaSquareKm { get; }

    // 射线法：落在任一外环内且不在该多边形的洞内。
    public bool Contains(GeoPoint point) =>
        Bounds.Contains(point) &&
        Polygons.Any(polygon => Inside(polygon.Outer, point) && !polygon.Holes.Any(hole => Inside(hole, point)));

    private static bool Inside(GeoRing ring, GeoPoint p)
    {
        var points = ring.Points;
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var a = points[i]; var b = points[j];
            if (a.Latitude > p.Latitude != b.Latitude > p.Latitude &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    // 球面多边形面积（Chamberlain & Duquette 近似，R = 6371.0088 km），单位 km²。
    private static double SphericalArea(GeoRing ring)
    {
        var points = ring.Points;
        if (points.Length < 3) return 0;
        var sum = 0.0;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var a = points[j]; var b = points[i];
            sum += (Radians(b.Longitude) - Radians(a.Longitude)) * (2 + Math.Sin(Radians(a.Latitude)) + Math.Sin(Radians(b.Latitude)));
        }
        return Math.Abs(sum) * EarthRadiusKm * EarthRadiusKm / 2;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
