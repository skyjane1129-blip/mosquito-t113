using System.Globalization;

namespace Mosquito.Client.Core;

public sealed class KeyValueMetadataParser
{
    public ParsedMetadata Parse(string text, Guid expectedCaptureId)
    {
        var sections = ParseSections(text);
        var root = sections[string.Empty];
        var environment = sections.GetValueOrDefault("environment") ?? new Dictionary<string, string>();
        var power = sections.GetValueOrDefault("power") ?? new Dictionary<string, string>();

        var version = RequiredInt(root, "metadata_version");
        if (version != 3)
        {
            throw new InvalidDataException($"Unsupported metadata version {version}; expected 3.");
        }
        var captureId = Guid.Parse(Required(root, "capture_id"));
        if (captureId != expectedCaptureId)
        {
            throw new InvalidDataException("Metadata capture_id does not match the requested capture UUID.");
        }
        var recordStatus = Required(root, "record_status");
        if (recordStatus is not ("COMPLETE" or "PARTIAL"))
        {
            throw new InvalidDataException("record_status must be COMPLETE or PARTIAL.");
        }

        var readonlySections = sections.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return new ParsedMetadata(
            version,
            captureId,
            recordStatus,
            Required(root, "photo"),
            RequiredSha(root, "jpeg_sha256"),
            RequiredLong(root, "jpeg_bytes"),
            RequiredInt(root, "jpeg_width"),
            RequiredInt(root, "jpeg_height"),
            string.Equals(root.GetValueOrDefault("system_time_valid"), "yes", StringComparison.OrdinalIgnoreCase),
            OptionalDouble(root, "uptime_seconds"),
            root.GetValueOrDefault("mode") ?? "unknown",
            OptionalInt(root, "focus_selected"),
            new EnvironmentReading(
                environment.GetValueOrDefault("RESULT") ?? "FAIL",
                environment.GetValueOrDefault("ERROR_CODE") ?? "NO_SAMPLE_OUTPUT",
                OptionalInt(environment, "TEMPERATURE_CENTI_C"),
                OptionalInt(environment, "HUMIDITY_CENTI_RH"),
                OptionalInt(environment, "CRC_OK") is int crc ? crc == 1 : null,
                OptionalLong(environment, "SAMPLED_UPTIME_MS")),
            new PowerReading(
                power.GetValueOrDefault("RESULT") ?? "FAIL",
                power.GetValueOrDefault("ERROR_CODE") ?? "NO_SAMPLE_OUTPUT",
                OptionalInt(power, "BATTERY_MV"),
                power.GetValueOrDefault("CHARGE_STATE"),
                OptionalInt(power, "VBUS_GOOD") is int vbus ? vbus == 1 : null,
                OptionalInt(power, "VBUS_MV"),
                OptionalInt(power, "CHARGE_CURRENT_MA"),
                power.GetValueOrDefault("FAULT_REG"),
                OptionalLong(power, "SAMPLED_UPTIME_MS")),
            readonlySections);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new(StringComparer.OrdinalIgnoreCase)
        };
        var section = string.Empty;
        using var reader = new StringReader(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                result.TryAdd(section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            result[section][line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Required metadata field '{key}' is missing.");

    private static int RequiredInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(Required(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Metadata field '{key}' is not an integer.");

    private static long RequiredLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.TryParse(Required(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Metadata field '{key}' is not an integer.");

    private static int? OptionalInt(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? OptionalLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) &&
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? OptionalDouble(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string RequiredSha(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = Required(values, key);
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException($"Metadata field '{key}' is not a SHA-256 value.");
        }
        return value.ToLowerInvariant();
    }
}
