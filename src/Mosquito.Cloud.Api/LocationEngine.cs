using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mosquito.Cloud.Api;

// Server-side positioning: every board fix (Luat single-cell LBS or GNSS) arrives with the serving and
// neighbour LTE cells. The engine asks each configured external locator for its own estimate, fuses the
// cell-based estimates (inverse-variance weighted), keeps a robust median over the recent fused history
// for this stationary trap, stores every candidate for evaluation and picks the one the policy prefers.
public sealed class LocationOptions
{
    // auto = GNSS > FUSED_MEDIAN > FUSED > board sample; or force a source name (LBS, LBS_AMAP, LBS_BAIDU,
    // CELL_OPENCELLID, FUSED, FUSED_MEDIAN) to display that estimate while the comparison runs.
    public string Policy { get; set; } = "auto";
    // Optional ground truth for the evaluation endpoint; kept in appsettings.Local.json, never in Git.
    public double? ReferenceLatitude { get; set; }
    public double? ReferenceLongitude { get; set; }
    public string ReferenceCoordinateSystem { get; set; } = "GCJ02";
    public string AmapKey { get; set; } = string.Empty;
    public string BaiduAk { get; set; } = string.Empty;
    public string OpenCellIdKey { get; set; } = string.Empty;
    public int ProviderTimeoutSeconds { get; set; } = 8;
    // The free Luat single-cell answer carries no radius; this is the sigma used when fusing it.
    public double SingleCellRadiusMeters { get; set; } = 500;
    public int HistoryWindowHours { get; set; } = 24;
    public int HistoryMinSamples { get; set; } = 3;
    public int HistoryMaxSamples { get; set; } = 48;
}

public static class LocationSources
{
    public const string Lbs = "LBS";
    public const string Gnss = "GNSS";
    public const string Amap = "LBS_AMAP";
    public const string Baidu = "LBS_BAIDU";
    public const string OpenCellId = "CELL_OPENCELLID";
    public const string Fused = "FUSED";
    public const string FusedMedian = "FUSED_MEDIAN";
    // Self-learned cell table: strongest observed cell whose position we learned from past Luat answers.
    public const string CellLearned = "CELL_LEARNED";
    public const string CellLearnedMedian = "CELL_LEARNED_MEDIAN";

    public static string Normalize(string? source) => source?.Trim().ToUpperInvariant() switch
    {
        null or "" => Lbs,
        "GNSS" or "GPS" => Gnss,
        "LBS" or "LBS_SINGLE_CELL" or "LBS_LUAT" => Lbs,
        var other => other
    };
}

public sealed record LocatorEstimate(
    string Source,
    double Latitude,
    double Longitude,
    string CoordinateSystem,
    double? RadiusMeters,
    string? Detail);

public interface ICellLocator
{
    string Source { get; }
    bool Enabled { get; }
    Task<LocatorEstimate?> LocateAsync(IReadOnlyList<CellObservation> cells, CancellationToken cancellationToken);
}

public sealed record LocationOutcome(GeoSample Chosen, IReadOnlyList<GeoSample> Candidates);

public sealed record LocationSourceStats(
    string Source,
    int Samples,
    int SamplesWithReference,
    double? MeanErrorMeters,
    double? MedianErrorMeters,
    double? P90ErrorMeters,
    double? MinErrorMeters,
    double? MaxErrorMeters,
    double? LastErrorMeters,
    double? MeanRadiusMeters,
    DateTimeOffset? LastSampledAtUtc);

public sealed record LocationEvaluation(
    string DeviceId,
    bool HasReference,
    string ReferenceCoordinateSystem,
    int WindowHours,
    string Policy,
    string? ChosenSource,
    double? ChosenErrorMeters,
    IReadOnlyList<LocationSourceStats> Sources);

public sealed class LocationEngine(
    IDeviceRepository devices,
    IEnumerable<ICellLocator> locators,
    IOptions<LocationOptions> options,
    ILogger<LocationEngine> logger)
{
    private readonly LocationOptions _options = options.Value;
    private readonly ICellLocator[] _locators = locators.ToArray();

    public bool HasReference => _options.ReferenceLatitude is double lat && _options.ReferenceLongitude is double lon &&
                                double.IsFinite(lat) && double.IsFinite(lon);

    public IReadOnlyList<string> EnabledLocators => _locators.Where(l => l.Enabled).Select(l => l.Source).ToArray();

    // True when the candidate should replace the current registry position (never downgrade a fused/GNSS
    // position with a plain board echo that carries no new information).
    public static bool ShouldReplace(GeoSample? current, GeoSample candidate)
    {
        if (current is null || !current.IsValid)
        {
            return true;
        }
        var currentSource = LocationSources.Normalize(current.Source);
        var candidateSource = LocationSources.Normalize(candidate.Source);
        if (candidateSource == LocationSources.Gnss)
        {
            return true;
        }
        if (currentSource == LocationSources.Gnss)
        {
            return current.SampledAtUtc is { } at && candidate.SampledAtUtc is { } now && now - at > TimeSpan.FromHours(6);
        }
        return candidateSource != LocationSources.Lbs || currentSource == LocationSources.Lbs;
    }

    public async Task<LocationOutcome> ProcessAsync(string deviceId, GeoSample reported, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var raw = ToWgs84(reported with { SampledAtUtc = reported.SampledAtUtc ?? now });
        var rawSource = LocationSources.Normalize(reported.Source);
        var cells = reported.Cells ?? [];
        var candidates = new List<GeoSample>
        {
            Annotate(raw with { Source = rawSource, Cells = cells.Length > 0 ? cells : null })
        };

        if (cells.Length > 0)
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.ProviderTimeoutSeconds, 2, 60));
            var tasks = _locators.Where(l => l.Enabled).Select(async locator =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                try
                {
                    return await locator.LocateAsync(cells, cts.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Cell locator {Source} failed", locator.Source);
                    return null;
                }
            });
            foreach (var estimate in await Task.WhenAll(tasks))
            {
                if (estimate is null)
                {
                    continue;
                }
                var (lat, lon) = estimate.CoordinateSystem.Equals("GCJ02", StringComparison.OrdinalIgnoreCase)
                    ? GeoConvert.Gcj02ToWgs84(estimate.Latitude, estimate.Longitude)
                    : (estimate.Latitude, estimate.Longitude);
                var sample = new GeoSample(lat, lon, raw.SampledAtUtc, null, "WGS84")
                {
                    Source = estimate.Source,
                    AccuracyMeters = estimate.RadiusMeters
                };
                if (sample.IsValid)
                {
                    candidates.Add(Annotate(sample));
                }
            }
        }

        // Self-learned cell table. The Luat single-cell answer is the position it associates with the
        // SERVING cell, so every past raw LBS sample teaches us where one cell is. Among the cells the
        // board sees now, the strongest one we already know is usually the nearest tower. Replay over
        // 135 recorded fixes (2026-09-14): serving-cell answer median 540 m, strongest known cell 103 m,
        // strongest known cell + trailing median 74 m.
        var history = await devices.ListLocationsAsync(deviceId, 1000, cancellationToken);
        var learned = BuildCellTable(history.Append(candidates[0]));
        GeoSample? learnedFix = null;
        if (cells.Length > 0 && learned.Count > 0)
        {
            var best = cells.Where(c => learned.ContainsKey(CellKey(c))).OrderByDescending(c => c.RsrpDbm).FirstOrDefault();
            if (best is not null)
            {
                var (lat, lon, radius) = learned[CellKey(best)];
                learnedFix = Annotate(new GeoSample(lat, lon, raw.SampledAtUtc, null, "WGS84")
                {
                    Source = LocationSources.CellLearned,
                    AccuracyMeters = radius
                });
                candidates.Add(learnedFix);
            }
        }

        // Fuse everything that is cell based (GNSS is not a cell estimate and is handled by the policy).
        var cellBased = candidates.Where(c => LocationSources.Normalize(c.Source) != LocationSources.Gnss).ToList();
        GeoSample? fused = null;
        if (cellBased.Count > 0)
        {
            var (lat, lon, radius) = InverseVarianceFuse(cellBased);
            fused = Annotate(new GeoSample(lat, lon, raw.SampledAtUtc, null, "WGS84")
            {
                Source = LocationSources.Fused,
                AccuracyMeters = radius
            });
            candidates.Add(fused);
        }

        // Robust medians over the recent history of a stationary trap (component-wise, window-limited).
        var window = TimeSpan.FromHours(Math.Max(1, _options.HistoryWindowHours));
        var minSamples = Math.Max(1, _options.HistoryMinSamples);
        var maxSamples = Math.Max(1, _options.HistoryMaxSamples - 1);
        GeoSample? median = TrailingMedian(history, fused, LocationSources.Fused, LocationSources.FusedMedian, raw.SampledAtUtc, now, window, minSamples, maxSamples);
        GeoSample? learnedMedian = TrailingMedian(history, learnedFix, LocationSources.CellLearned, LocationSources.CellLearnedMedian, raw.SampledAtUtc, now, window, minSamples, maxSamples);
        if (median is not null)
        {
            candidates.Add(median);
        }
        if (learnedMedian is not null)
        {
            candidates.Add(learnedMedian);
        }

        foreach (var candidate in candidates)
        {
            await devices.AddLocationAsync(deviceId, candidate, cancellationToken);
        }

        var chosen = Choose(candidates, rawSource, fused, median, learnedFix, learnedMedian);
        logger.LogInformation(
            "Location for {DeviceId}: raw={Raw} cells={Cells} candidates={Candidates} chosen={Chosen} error={Error}",
            deviceId, rawSource, cells.Length, string.Join(",", candidates.Select(c => c.Source)), chosen.Source,
            chosen.ReferenceErrorMeters?.ToString("F0", CultureInfo.InvariantCulture) ?? "n/a");
        return new LocationOutcome(chosen, candidates);
    }

    public async Task<LocationEvaluation> EvaluateAsync(string deviceId, int hours, CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 30));
        var now = DateTimeOffset.UtcNow;
        var samples = (await devices.ListLocationsAsync(deviceId, 1000, cancellationToken))
            .Where(s => s.IsValid && s.SampledAtUtc is { } at && now - at <= window)
            .ToList();
        var stats = samples
            .GroupBy(s => LocationSources.Normalize(s.Source), StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(s => s.SampledAtUtc).ToList();
                var errors = ordered.Where(s => s.ReferenceErrorMeters is not null).Select(s => s.ReferenceErrorMeters!.Value).OrderBy(e => e).ToArray();
                var radii = ordered.Where(s => s.AccuracyMeters is not null).Select(s => s.AccuracyMeters!.Value).ToArray();
                return new LocationSourceStats(
                    group.Key,
                    ordered.Count,
                    errors.Length,
                    errors.Length > 0 ? errors.Average() : null,
                    errors.Length > 0 ? Percentile(errors, 0.5) : null,
                    errors.Length > 0 ? Percentile(errors, 0.9) : null,
                    errors.Length > 0 ? errors[0] : null,
                    errors.Length > 0 ? errors[^1] : null,
                    ordered[0].ReferenceErrorMeters,
                    radii.Length > 0 ? radii.Average() : null,
                    ordered[0].SampledAtUtc);
            })
            .OrderBy(s => s.MedianErrorMeters ?? double.MaxValue)
            .ToArray();
        var device = await devices.GetDeviceAsync(deviceId, cancellationToken);
        return new LocationEvaluation(
            deviceId,
            HasReference,
            _options.ReferenceCoordinateSystem,
            (int)window.TotalHours,
            _options.Policy,
            device?.LastLocation?.Source,
            device?.LastLocation?.ReferenceErrorMeters,
            stats);
    }

    private GeoSample Choose(List<GeoSample> candidates, string rawSource, GeoSample? fused, GeoSample? median,
                             GeoSample? learnedFix, GeoSample? learnedMedian)
    {
        var policy = _options.Policy.Trim().ToUpperInvariant();
        if (policy is not ("" or "AUTO"))
        {
            var forced = candidates.FirstOrDefault(c => LocationSources.Normalize(c.Source) == policy);
            if (forced is not null)
            {
                return forced;
            }
        }
        if (rawSource == LocationSources.Gnss)
        {
            return candidates[0];
        }
        // auto: GNSS > learned-cell median > learned cell > fused median > fused > raw board answer.
        return learnedMedian ?? learnedFix ?? median ?? fused ?? candidates[0];
    }

    private static (int Mcc, int Mnc, long CellId) CellKey(CellObservation cell) => (cell.Mcc, cell.Mnc, cell.CellId);

    // cell -> (lat, lon, radius): component-wise median of every Luat answer recorded while that cell was
    // the serving cell. Radius is twice the median spread of those answers, floored at a city-cell 100 m.
    private static Dictionary<(int, int, long), (double Latitude, double Longitude, double Radius)> BuildCellTable(IEnumerable<GeoSample> samples)
    {
        var points = new Dictionary<(int, int, long), List<(double Lat, double Lon)>>();
        foreach (var sample in samples)
        {
            if (!sample.IsValid || LocationSources.Normalize(sample.Source) != LocationSources.Lbs || sample.Cells is null)
            {
                continue;
            }
            var serving = sample.Cells.FirstOrDefault(c => c.Serving);
            if (serving is null)
            {
                continue;
            }
            var key = CellKey(serving);
            if (!points.TryGetValue(key, out var list))
            {
                points[key] = list = [];
            }
            list.Add((sample.Latitude, sample.Longitude));
        }
        var table = new Dictionary<(int, int, long), (double, double, double)>();
        foreach (var (key, list) in points)
        {
            var lat = Percentile(list.Select(p => p.Lat).OrderBy(v => v).ToArray(), 0.5);
            var lon = Percentile(list.Select(p => p.Lon).OrderBy(v => v).ToArray(), 0.5);
            var spread = Percentile(list.Select(p => GeoConvert.DistanceMeters(p.Lat, p.Lon, lat, lon)).OrderBy(v => v).ToArray(), 0.5);
            table[key] = (lat, lon, Math.Max(100.0, Math.Round(2 * spread, 1)));
        }
        return table;
    }

    // Samples farther than this from the current estimate belong to a previous site (the trap was moved,
    // e.g. taken to a demo venue) and must not drag the median back there.
    private const double SameSiteMeters = 1000.0;

    // Component-wise median of the recent same-site samples of one source plus the current one; null until enough exist.
    private GeoSample? TrailingMedian(IReadOnlyList<GeoSample> history, GeoSample? current, string source, string medianSource,
                                      DateTimeOffset? sampledAt, DateTimeOffset now, TimeSpan window, int minSamples, int maxSamples)
    {
        if (current is null)
        {
            return null;
        }
        var recent = history
            .Where(s => LocationSources.Normalize(s.Source) == source && s.IsValid && s.SampledAtUtc is { } at && now - at <= window &&
                        GeoConvert.DistanceMeters(s.Latitude, s.Longitude, current.Latitude, current.Longitude) <= SameSiteMeters)
            .Take(maxSamples)
            .ToList();
        recent.Add(current);
        if (recent.Count < minSamples)
        {
            return null;
        }
        var (lat, lon, spread) = Median(recent);
        return Annotate(new GeoSample(lat, lon, sampledAt, null, "WGS84") { Source = medianSource, AccuracyMeters = spread });
    }

    private GeoSample Annotate(GeoSample wgs84)
    {
        if (!HasReference)
        {
            return wgs84;
        }
        var (refLat, refLon) = _options.ReferenceCoordinateSystem.Equals("GCJ02", StringComparison.OrdinalIgnoreCase)
            ? GeoConvert.Gcj02ToWgs84(_options.ReferenceLatitude!.Value, _options.ReferenceLongitude!.Value)
            : (_options.ReferenceLatitude!.Value, _options.ReferenceLongitude!.Value);
        return wgs84 with { ReferenceErrorMeters = Math.Round(GeoConvert.DistanceMeters(wgs84.Latitude, wgs84.Longitude, refLat, refLon), 1) };
    }

    private static GeoSample ToWgs84(GeoSample sample)
    {
        if (string.Equals(sample.CoordinateSystem, "GCJ02", StringComparison.OrdinalIgnoreCase))
        {
            var (lat, lon) = GeoConvert.Gcj02ToWgs84(sample.Latitude, sample.Longitude);
            return sample with { Latitude = lat, Longitude = lon, CoordinateSystem = "WGS84" };
        }
        return sample with { CoordinateSystem = "WGS84" };
    }

    // Weighted mean in a local tangent plane; weight = 1/sigma^2 with sigma = reported radius (or the
    // single-cell default). The combined radius is the usual sqrt(1/sum w) floored at 20 m.
    private (double Latitude, double Longitude, double Radius) InverseVarianceFuse(IReadOnlyList<GeoSample> samples)
    {
        var lat0 = samples[0].Latitude;
        var lon0 = samples[0].Longitude;
        var cos = Math.Cos(lat0 * Math.PI / 180.0);
        double sw = 0, sx = 0, sy = 0;
        foreach (var s in samples)
        {
            var sigma = Math.Max(20.0, s.AccuracyMeters ?? _options.SingleCellRadiusMeters);
            var w = 1.0 / (sigma * sigma);
            sw += w;
            sx += w * (s.Longitude - lon0) * 111320.0 * cos;
            sy += w * (s.Latitude - lat0) * 110574.0;
        }
        var x = sx / sw;
        var y = sy / sw;
        return (lat0 + y / 110574.0, lon0 + x / (111320.0 * cos), Math.Max(20.0, Math.Round(Math.Sqrt(1.0 / sw), 1)));
    }

    // Component-wise median (robust against the occasional wrong tower) plus the median absolute spread as radius.
    private static (double Latitude, double Longitude, double Spread) Median(IReadOnlyList<GeoSample> samples)
    {
        var lats = samples.Select(s => s.Latitude).OrderBy(v => v).ToArray();
        var lons = samples.Select(s => s.Longitude).OrderBy(v => v).ToArray();
        var lat = Percentile(lats, 0.5);
        var lon = Percentile(lons, 0.5);
        var distances = samples.Select(s => GeoConvert.DistanceMeters(s.Latitude, s.Longitude, lat, lon)).OrderBy(d => d).ToArray();
        return (lat, lon, Math.Max(20.0, Math.Round(Percentile(distances, 0.5), 1)));
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }
        var position = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

internal static class LocatorJson
{
    public static string FormatBts(CellObservation cell, bool withAge) =>
        withAge
            ? string.Create(CultureInfo.InvariantCulture, $"{cell.Mcc},{cell.Mnc:00},{cell.Tac},{cell.CellId},{cell.RsrpDbm},0")
            : string.Create(CultureInfo.InvariantCulture, $"{cell.Mcc},{cell.Mnc:00},{cell.Tac},{cell.CellId},{cell.RsrpDbm}");

    public static CellObservation Serving(IReadOnlyList<CellObservation> cells) =>
        cells.FirstOrDefault(c => c.Serving) ?? cells.OrderByDescending(c => c.RsrpDbm).First();

    public static bool TryParseLonLat(string? text, out double lat, out double lon)
    {
        lat = lon = double.NaN;
        var parts = text?.Split(',');
        return parts is { Length: 2 } &&
               double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
    }

    public static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };
}

// 高德智能硬件定位 v5 (restapi.amap.com/v5/position/IoT): serving cell in bts, neighbours in nearbts, GCJ02 out.
public sealed class AmapCellLocator(IHttpClientFactory httpClientFactory, IOptions<LocationOptions> options) : ICellLocator
{
    public string Source => LocationSources.Amap;
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.AmapKey);

    public async Task<LocatorEstimate?> LocateAsync(IReadOnlyList<CellObservation> cells, CancellationToken cancellationToken)
    {
        if (cells.Count == 0)
        {
            return null;
        }
        var serving = LocatorJson.Serving(cells);
        var query = new Dictionary<string, string>
        {
            ["key"] = options.Value.AmapKey,
            ["accesstype"] = "1",
            ["cdma"] = "0",
            ["network"] = "LTE",
            ["bts"] = LocatorJson.FormatBts(serving, true),
            ["output"] = "JSON"
        };
        var neighbours = cells.Where(c => !ReferenceEquals(c, serving)).Select(c => LocatorJson.FormatBts(c, false)).ToArray();
        if (neighbours.Length > 0)
        {
            query["nearbts"] = string.Join("|", neighbours);
        }
        var url = "https://restapi.amap.com/v5/position/IoT?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var client = httpClientFactory.CreateClient("locators");
        using var response = await client.PostAsync(url, new StringContent(string.Empty), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.ToString() != "1")
        {
            var info = root.TryGetProperty("info", out var i) ? i.ToString() : body;
            throw new InvalidOperationException($"amap: {info}");
        }
        var position = root.TryGetProperty("position", out var p) ? p : root.TryGetProperty("result", out var r) ? r : root;
        if (!position.TryGetProperty("location", out var location) ||
            !LocatorJson.TryParseLonLat(location.GetString(), out var lat, out var lon))
        {
            throw new InvalidOperationException("amap: no location in response");
        }
        var radius = position.TryGetProperty("radius", out var rad) ? LocatorJson.Number(rad) : null;
        return new LocatorEstimate(Source, lat, lon, "GCJ02", radius, $"cells={cells.Count}");
    }
}

// 百度智能硬件定位 v2 (api.map.baidu.com/locapi/v2). Documented as POST with the ak in the query and
// the cell list in bts; the default output is BD09LL, so GCJ02 is requested explicitly.
public sealed class BaiduCellLocator(IHttpClientFactory httpClientFactory, IOptions<LocationOptions> options) : ICellLocator
{
    public string Source => LocationSources.Baidu;
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.BaiduAk);

    public async Task<LocatorEstimate?> LocateAsync(IReadOnlyList<CellObservation> cells, CancellationToken cancellationToken)
    {
        if (cells.Count == 0)
        {
            return null;
        }
        var form = new Dictionary<string, string>
        {
            ["ak"] = options.Value.BaiduAk,
            ["accesstype"] = "0",
            ["bts"] = string.Join("|", cells.OrderByDescending(c => c.Serving).Select(c => LocatorJson.FormatBts(c, false))),
            ["imei"] = "000000000000000",
            ["ctime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["coor"] = "gcj02",
            ["need_rgc"] = "N",
            ["output"] = "json"
        };
        using var client = httpClientFactory.CreateClient("locators");
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync("https://api.map.baidu.com/locapi/v2?ak=" + Uri.EscapeDataString(options.Value.BaiduAk), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var result = root.TryGetProperty("result", out var r) ? r : root;
        if (result.TryGetProperty("error", out var error) && error.ToString() is not ("0" or "161"))
        {
            throw new InvalidOperationException($"baidu: error {error}");
        }
        double lat, lon;
        if (result.TryGetProperty("location", out var location))
        {
            if (location.ValueKind == JsonValueKind.String)
            {
                if (!LocatorJson.TryParseLonLat(location.GetString(), out lat, out lon))
                {
                    throw new InvalidOperationException("baidu: unparsable location");
                }
            }
            else
            {
                lat = LocatorJson.Number(location.GetProperty("lat")) ?? double.NaN;
                lon = LocatorJson.Number(location.GetProperty("lng")) ?? double.NaN;
            }
        }
        else
        {
            throw new InvalidOperationException("baidu: no location in response");
        }
        var radius = result.TryGetProperty("radius", out var rad) ? LocatorJson.Number(rad) : null;
        return new LocatorEstimate(Source, lat, lon, "GCJ02", radius, $"cells={cells.Count}");
    }
}

// OpenCelliD tower database: one lookup per cell, then an RSRP-weighted centroid of the tower positions.
// This is the classic "weighted centroid" algorithm run with our own weights; coverage in China is patchy.
public sealed class OpenCellIdLocator(IHttpClientFactory httpClientFactory, IOptions<LocationOptions> options) : ICellLocator
{
    public string Source => LocationSources.OpenCellId;
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.OpenCellIdKey);

    public async Task<LocatorEstimate?> LocateAsync(IReadOnlyList<CellObservation> cells, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("locators");
        var towers = new List<(double Lat, double Lon, double Range, double Weight)>();
        foreach (var cell in cells.Take(8))
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://opencellid.org/cell/get?key={Uri.EscapeDataString(options.Value.OpenCellIdKey)}&mcc={cell.Mcc}&mnc={cell.Mnc}&lac={cell.Tac}&cellid={cell.CellId}&radio=LTE&format=json");
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            if (!root.TryGetProperty("lat", out var latEl) || !root.TryGetProperty("lon", out var lonEl))
            {
                continue;
            }
            var lat = LocatorJson.Number(latEl);
            var lon = LocatorJson.Number(lonEl);
            if (lat is null || lon is null)
            {
                continue;
            }
            var range = root.TryGetProperty("range", out var rangeEl) ? LocatorJson.Number(rangeEl) ?? 1000 : 1000;
            towers.Add((lat.Value, lon.Value, range, Math.Pow(10.0, cell.RsrpDbm / 10.0)));
        }
        if (towers.Count == 0)
        {
            return null;
        }
        var sumW = towers.Sum(t => t.Weight);
        var cLat = towers.Sum(t => t.Lat * t.Weight) / sumW;
        var cLon = towers.Sum(t => t.Lon * t.Weight) / sumW;
        var spread = towers.Max(t => Math.Max(t.Range, GeoConvert.DistanceMeters(t.Lat, t.Lon, cLat, cLon)));
        return new LocatorEstimate(Source, cLat, cLon, "WGS84", Math.Round(spread, 1), $"towers={towers.Count}/{cells.Count}");
    }
}
