namespace Mosquito.Client.Core.Geo;

// WGS84 <-> GCJ02（国测局火星坐标）转换。设备与服务端记录统一使用 GCJ02；只有在使用 WGS84 底图（如 OpenStreetMap）时才需要反算。
public static class ChinaCoordinates
{
    private const double A = 6378245.0;
    private const double Ee = 0.00669342162296594323;

    public static bool IsInChina(double longitude, double latitude) =>
        longitude is >= 72.004 and <= 137.8347 && latitude is >= 0.8293 and <= 55.8271;

    public static (double Longitude, double Latitude) Wgs84ToGcj02(double longitude, double latitude)
    {
        if (!IsInChina(longitude, latitude)) return (longitude, latitude);
        var dLat = TransformLatitude(longitude - 105.0, latitude - 35.0);
        var dLon = TransformLongitude(longitude - 105.0, latitude - 35.0);
        var radLat = latitude / 180.0 * Math.PI;
        var magic = Math.Sin(radLat);
        magic = 1 - Ee * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        dLat = dLat * 180.0 / (A * (1 - Ee) / (magic * sqrtMagic) * Math.PI);
        dLon = dLon * 180.0 / (A / sqrtMagic * Math.Cos(radLat) * Math.PI);
        return (longitude + dLon, latitude + dLat);
    }

    // 迭代反算：把 GCJ02 当作初值，反复用正向偏移修正，直到偏移小于 1e-9 度。
    public static (double Longitude, double Latitude) Gcj02ToWgs84(double longitude, double latitude)
    {
        if (!IsInChina(longitude, latitude)) return (longitude, latitude);
        double wgsLon = longitude, wgsLat = latitude;
        for (var i = 0; i < 12; i++)
        {
            var (gcjLon, gcjLat) = Wgs84ToGcj02(wgsLon, wgsLat);
            var dLon = gcjLon - longitude;
            var dLat = gcjLat - latitude;
            wgsLon -= dLon;
            wgsLat -= dLat;
            if (Math.Abs(dLon) < 1e-9 && Math.Abs(dLat) < 1e-9) break;
        }
        return (wgsLon, wgsLat);
    }

    private static double TransformLatitude(double x, double y)
    {
        var ret = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;
        return ret;
    }

    private static double TransformLongitude(double x, double y)
    {
        var ret = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;
        return ret;
    }
}
