using System.Globalization;
using System.Text.Json;

namespace Mosquito.Client.Core.Geo;

// 行政区边界集合。上海数据来自阿里云 DataV.GeoAtlas（GCJ02），嵌入为程序集资源，详见 Assets/shanghai-districts.README.md。
public sealed class DistrictAtlas
{
    private const string ShanghaiResourceName = "Mosquito.Client.Core.Assets.shanghai-districts.geojson";
    private static readonly string[] ShanghaiOrder =
    [
        "黄浦区", "徐汇区", "长宁区", "静安区", "普陀区", "虹口区", "杨浦区", "闵行区",
        "宝山区", "嘉定区", "浦东新区", "金山区", "松江区", "青浦区", "奉贤区", "崇明区"
    ];
    private static readonly Lazy<DistrictAtlas> ShanghaiLazy = new(LoadShanghai, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DistrictAtlas Shanghai => ShanghaiLazy.Value;

    private DistrictAtlas(IReadOnlyList<DistrictShape> districts)
    {
        Districts = districts;
        Bounds = districts.Select(district => district.Bounds).Aggregate((a, b) => a.Union(b));
    }

    public IReadOnlyList<DistrictShape> Districts { get; }
    public GeoBounds Bounds { get; }
    public string Attribution => "边界数据：阿里云 DataV.GeoAtlas（GCJ02）";
    public string CoordinateSystem => "GCJ02";

    public DistrictShape? Find(string name) => Districts.FirstOrDefault(district => district.Name == name);

    public string? Locate(double latitude, double longitude)
    {
        var point = new GeoPoint(longitude, latitude);
        return Districts.FirstOrDefault(district => district.Contains(point))?.Name;
    }

    public bool InDistrict(string name, double latitude, double longitude) =>
        Find(name)?.Contains(new GeoPoint(longitude, latitude)) ?? false;

    private static DistrictAtlas LoadShanghai()
    {
        using var stream = typeof(DistrictAtlas).Assembly.GetManifestResourceStream(ShanghaiResourceName)
            ?? throw new InvalidDataException($"缺少嵌入资源：{ShanghaiResourceName}");
        return Parse(stream, ShanghaiOrder);
    }

    // 按 order 给定的名称顺序从 FeatureCollection 中取 feature；缺失则抛 InvalidDataException。
    internal static DistrictAtlas Parse(Stream geoJson, IReadOnlyList<string> order)
    {
        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GeoJSON 不是 FeatureCollection。");
        }
        var shapes = new Dictionary<string, DistrictShape>(StringComparer.Ordinal);
        foreach (var feature in features.EnumerateArray())
        {
            var shape = ReadFeature(feature);
            if (!shapes.TryAdd(shape.Name, shape)) throw new InvalidDataException($"行政区“{shape.Name}”重复出现。");
        }
        var ordered = new List<DistrictShape>(order.Count);
        foreach (var name in order)
        {
            ordered.Add(shapes.TryGetValue(name, out var shape) ? shape : throw new InvalidDataException($"边界数据缺少行政区“{name}”。"));
        }
        return new DistrictAtlas(ordered);
    }

    private static DistrictShape ReadFeature(JsonElement feature)
    {
        var properties = feature.GetProperty("properties");
        var name = properties.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("feature 缺少 name。");
        var adCode = properties.TryGetProperty("adcode", out var adcode)
            ? adcode.ValueKind == JsonValueKind.Number ? adcode.GetInt64().ToString(CultureInfo.InvariantCulture) : adcode.GetString() ?? string.Empty
            : string.Empty;
        var geometry = feature.GetProperty("geometry");
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        var polygons = type switch
        {
            "Polygon" => [ReadPolygon(coordinates)],
            "MultiPolygon" => coordinates.EnumerateArray().Select(ReadPolygon).ToArray(),
            _ => throw new InvalidDataException($"行政区“{name}”的几何类型不受支持：{type}")
        };
        var bounds = GeoBounds.Of(polygons.SelectMany(polygon => polygon.Outer.Points));
        var center = ReadPoint(properties, "center") ?? bounds.Center;
        var centroid = ReadPoint(properties, "centroid") ?? center;
        return new DistrictShape(name, adCode, center, centroid, polygons);
    }

    private static GeoPolygon ReadPolygon(JsonElement rings)
    {
        var parsed = rings.EnumerateArray().Select(ReadRing).Where(ring => ring.Points.Length >= 3).ToArray();
        if (parsed.Length == 0) throw new InvalidDataException("多边形没有有效的外环。");
        return new GeoPolygon(parsed[0], parsed.Skip(1).ToArray());
    }

    private static GeoRing ReadRing(JsonElement ring)
    {
        var points = ring.EnumerateArray().Select(ReadCoordinate).ToList();
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        return new GeoRing(points.ToArray());
    }

    private static GeoPoint? ReadPoint(JsonElement properties, string property) =>
        properties.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2
            ? ReadCoordinate(value)
            : null;

    private static GeoPoint ReadCoordinate(JsonElement pair)
    {
        var longitude = pair[0].GetDouble();
        var latitude = pair[1].GetDouble();
        if (!double.IsFinite(longitude) || !double.IsFinite(latitude)) throw new InvalidDataException("坐标不是有限数。");
        return new GeoPoint(longitude, latitude);
    }
}
