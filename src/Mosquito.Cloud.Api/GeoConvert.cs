namespace Mosquito.Cloud.Api;

// WGS84 -> GCJ02 (the offset used by mainland Chinese map providers). Board GNSS/LBS output is WGS84.
public static class GeoConvert
{
    private const double A = 6378245.0;
    private const double Ee = 0.00669342162296594323;

    public static GeoSample ToGcj02(GeoSample sample)
    {
        if (!sample.IsValid ||
            string.Equals(sample.CoordinateSystem, "GCJ02", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sample.CoordinateSystem, "WGS84", StringComparison.OrdinalIgnoreCase) ||
            OutOfChina(sample.Latitude, sample.Longitude))
        {
            return sample;
        }
        var (lat, lon) = Wgs84ToGcj02(sample.Latitude, sample.Longitude);
        return sample with { Latitude = lat, Longitude = lon, CoordinateSystem = "GCJ02" };
    }

    public static (double Latitude, double Longitude) Wgs84ToGcj02(double lat, double lon)
    {
        var dLat = TransformLat(lon - 105.0, lat - 35.0);
        var dLon = TransformLon(lon - 105.0, lat - 35.0);
        var radLat = lat / 180.0 * Math.PI;
        var magic = Math.Sin(radLat);
        magic = 1 - Ee * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        dLat = dLat * 180.0 / (A * (1 - Ee) / (magic * sqrtMagic) * Math.PI);
        dLon = dLon * 180.0 / (A / sqrtMagic * Math.Cos(radLat) * Math.PI);
        return (lat + dLat, lon + dLon);
    }

    // Inverse of Wgs84ToGcj02 by fixed-point iteration; converges to well under a metre in a few rounds.
    public static (double Latitude, double Longitude) Gcj02ToWgs84(double lat, double lon)
    {
        if (OutOfChina(lat, lon))
        {
            return (lat, lon);
        }
        var wgsLat = lat;
        var wgsLon = lon;
        for (var i = 0; i < 6; i++)
        {
            var (gLat, gLon) = Wgs84ToGcj02(wgsLat, wgsLon);
            wgsLat -= gLat - lat;
            wgsLon -= gLon - lon;
        }
        return (wgsLat, wgsLon);
    }

    // Great-circle distance on the WGS84 mean sphere; adequate for the few kilometres that matter here.
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static bool OutOfChina(double lat, double lon) =>
        lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271;

    private static double TransformLat(double x, double y)
    {
        var ret = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;
        return ret;
    }

    private static double TransformLon(double x, double y)
    {
        var ret = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;
        return ret;
    }
}
