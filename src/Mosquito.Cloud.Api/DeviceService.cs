using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Mosquito.Cloud.Api;

public sealed class DeviceOptions
{
    // A device is shown online while its last heartbeat or poll is younger than this window.
    public int OnlineWindowSeconds { get; set; } = 180;
    public int CommandTtlMinutes { get; set; } = 10;
    public int MaxLongPollSeconds { get; set; } = 30;
}

public sealed partial class DeviceService(IDeviceRepository devices, LocationEngine locations, IOptions<DeviceOptions> options)
{
    private readonly DeviceOptions _options = options.Value;

    public LocationEngine Locations => locations;

    public static void ValidateDeviceId(string? deviceId)
    {
        if (deviceId is null || !DeviceIdPattern().IsMatch(deviceId))
        {
            throw new CaptureValidationException("deviceId may contain only 1-64 letters, digits, dot, underscore, colon and hyphen.");
        }
    }

    public async Task<DeviceRecord> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken)
    {
        ValidateDeviceId(request.DeviceId);
        var now = DateTimeOffset.UtcNow;
        var existing = await devices.GetDeviceAsync(request.DeviceId, cancellationToken)
            ?? new DeviceRecord(request.DeviceId, request.DeviceSerial, now, now);
        GeoSample? location = existing.LastLocation;
        if (request.Location is { IsValid: true } reported)
        {
            // Heartbeats only echo the board's last fix; they never demote a fused/GNSS registry position.
            var sample = reported with { SampledAtUtc = reported.SampledAtUtc ?? now, Source = LocationSources.Normalize(reported.Source) };
            if (LocationEngine.ShouldReplace(existing.LastLocation, sample))
            {
                location = sample.Cells is { Length: > 0 }
                    ? (await locations.ProcessAsync(request.DeviceId, sample, now, cancellationToken)).Chosen
                    : sample;
                if (sample.Cells is not { Length: > 0 } && existing.LastLocation is null)
                {
                    await devices.AddLocationAsync(request.DeviceId, location, cancellationToken);
                }
            }
        }
        var updated = existing with
        {
            DeviceSerial = request.DeviceSerial ?? existing.DeviceSerial,
            UpdatedAtUtc = now,
            LastSeenAtUtc = now,
            LastEnvironment = request.Environment ?? existing.LastEnvironment,
            LastPower = request.Power ?? existing.LastPower,
            ImageVersion = request.ImageVersion ?? existing.ImageVersion,
            BoardUptimeSeconds = request.BoardUptimeSeconds ?? existing.BoardUptimeSeconds,
            LastLocation = location
        };
        await devices.UpsertDeviceAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DeviceRecord> RecordLocationAsync(LocationReport report, CancellationToken cancellationToken)
    {
        ValidateDeviceId(report.DeviceId);
        var now = DateTimeOffset.UtcNow;
        var sample = new GeoSample(
            report.Latitude,
            report.Longitude,
            report.SampledAtUtc ?? now,
            null,
            (report.CoordinateSystem ?? "WGS84").ToUpperInvariant())
        {
            Source = LocationSources.Normalize(report.Source),
            AccuracyMeters = report.AccuracyMeters,
            Cells = report.Cells is { Length: > 0 } ? report.Cells.Take(16).ToArray() : null
        };
        if (!sample.IsValid)
        {
            throw new CaptureValidationException("Location latitude/longitude are out of range.");
        }
        var existing = await devices.GetDeviceAsync(report.DeviceId, cancellationToken)
            ?? new DeviceRecord(report.DeviceId, null, now, now);
        // Every explicit report is a fresh board fix: run the locators and fusion, store all candidates.
        var outcome = await locations.ProcessAsync(report.DeviceId, sample, now, cancellationToken);
        var chosen = LocationEngine.ShouldReplace(existing.LastLocation, outcome.Chosen) ? outcome.Chosen : existing.LastLocation;
        var updated = existing with { UpdatedAtUtc = now, LastSeenAtUtc = now, LastLocation = chosen };
        await devices.UpsertDeviceAsync(updated, cancellationToken);
        return updated;
    }

    public Task<LocationEvaluation> EvaluateLocationsAsync(string deviceId, int hours, CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        return locations.EvaluateAsync(deviceId, hours, cancellationToken);
    }

    public async Task<DeviceCommand> CreateCommandAsync(
        string deviceId,
        CreateCommandRequest request,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        var type = request.Type?.Trim().ToLowerInvariant() switch
        {
            "capture" => CommandType.Capture,
            "learn" => CommandType.Learn,
            _ => throw new CaptureValidationException("type must be capture or learn.")
        };
        if (type == CommandType.Capture && request.Focus is < 1 or > 1023)
        {
            throw new CaptureValidationException("focus must be omitted (automatic) or within 1..1023.");
        }
        if (type == CommandType.Learn && request.Rounds is < 1 or > 12)
        {
            throw new CaptureValidationException("rounds must be omitted (default) or within 1..12.");
        }
        _ = await devices.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new KeyNotFoundException("Device has not registered with the server yet.");
        var now = DateTimeOffset.UtcNow;
        var command = new DeviceCommand(
            Guid.NewGuid(),
            deviceId,
            type,
            CommandStatus.Pending,
            now,
            now.AddMinutes(Math.Max(1, _options.CommandTtlMinutes)),
            requestedBy)
        {
            Focus = type == CommandType.Capture ? request.Focus : null,
            Rounds = type == CommandType.Learn ? request.Rounds : null
        };
        await devices.AddCommandAsync(command, cancellationToken);
        return command;
    }

    // Long-polls for the next command so the board sees a new command within about a second.
    public async Task<DeviceCommand?> WaitForCommandAsync(string deviceId, int waitSeconds, CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        var now = DateTimeOffset.UtcNow;
        var device = await devices.GetDeviceAsync(deviceId, cancellationToken)
            ?? new DeviceRecord(deviceId, null, now, now);
        await devices.UpsertDeviceAsync(device with { UpdatedAtUtc = now, LastSeenAtUtc = now }, cancellationToken);

        var deadline = now.AddSeconds(Math.Clamp(waitSeconds, 0, Math.Max(0, _options.MaxLongPollSeconds)));
        while (true)
        {
            var command = await devices.DispatchNextCommandAsync(deviceId, DateTimeOffset.UtcNow, cancellationToken);
            if (command is not null || DateTimeOffset.UtcNow >= deadline)
            {
                return command;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    public async Task<DeviceCommand> AcknowledgeAsync(Guid id, CommandAckRequest request, CancellationToken cancellationToken)
    {
        var command = await devices.GetCommandAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Command does not exist.");
        var status = request.Status?.Trim().ToUpperInvariant() switch
        {
            "COMPLETED" or "COMPLETE" => CommandStatus.Completed,
            "FAILED" => CommandStatus.Failed,
            _ => throw new CaptureValidationException("status must be Completed or Failed.")
        };
        if (command.Status is CommandStatus.Completed)
        {
            return command;
        }
        var acknowledged = command with
        {
            Status = status,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            CaptureId = request.CaptureId ?? command.CaptureId,
            Error = status == CommandStatus.Failed ? request.Error ?? "unspecified" : null
        };
        await devices.UpdateCommandAsync(acknowledged, cancellationToken);
        return acknowledged;
    }

    public Task<DeviceCommand?> GetCommandAsync(Guid id, CancellationToken cancellationToken) =>
        devices.GetCommandAsync(id, cancellationToken);

    public Task<IReadOnlyList<DeviceCommand>> ListCommandsAsync(string deviceId, int limit, CancellationToken cancellationToken) =>
        devices.ListCommandsAsync(deviceId, limit, cancellationToken);

    public async Task<IReadOnlyList<GeoSample>> ListLocationsAsync(string deviceId, int limit, CancellationToken cancellationToken) =>
        (await devices.ListLocationsAsync(deviceId, limit, cancellationToken)).Select(GeoConvert.ToGcj02).ToArray();

    // Called when a capture becomes Complete/Partial so the registry and any originating command follow it.
    public async Task NoteCaptureAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        if (capture.DeviceId is not null && DeviceIdPattern().IsMatch(capture.DeviceId))
        {
            var now = DateTimeOffset.UtcNow;
            var device = await devices.GetDeviceAsync(capture.DeviceId, cancellationToken)
                ?? new DeviceRecord(capture.DeviceId, capture.DeviceSerial, now, now);
            var newer = device.LatestCaptureAtUtc is null || capture.CapturedAtUtc >= device.LatestCaptureAtUtc;
            var lastLocation = device.LastLocation;
            if (capture.Location is { IsValid: true } location)
            {
                var sample = location with { Source = LocationSources.Normalize(location.Source), SampledAtUtc = location.SampledAtUtc ?? now };
                if (sample.Cells is { Length: > 0 })
                {
                    var outcome = await locations.ProcessAsync(capture.DeviceId, sample, now, cancellationToken);
                    sample = outcome.Chosen;
                }
                else if (device.LastLocation is null)
                {
                    await devices.AddLocationAsync(capture.DeviceId, sample, cancellationToken);
                }
                if (LocationEngine.ShouldReplace(device.LastLocation, sample))
                {
                    lastLocation = sample;
                }
            }
            var updated = device with
            {
                UpdatedAtUtc = now,
                LastSeenAtUtc = capture.UploadRoute == "BOARD_4G" ? now : device.LastSeenAtUtc,
                LatestCaptureId = newer ? capture.Id : device.LatestCaptureId,
                LatestCaptureAtUtc = newer ? capture.CapturedAtUtc : device.LatestCaptureAtUtc,
                LastEnvironment = capture.Environment ?? device.LastEnvironment,
                LastPower = capture.Power ?? device.LastPower,
                LastLocation = lastLocation
            };
            await devices.UpsertDeviceAsync(updated, cancellationToken);
        }
        if (capture.CommandId is Guid commandId &&
            await devices.GetCommandAsync(commandId, cancellationToken) is { } command &&
            command.Status is not CommandStatus.Completed)
        {
            await devices.UpdateCommandAsync(command with
            {
                Status = CommandStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                CaptureId = capture.Id,
                Error = null
            }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RemoteDeviceView>> ListDeviceViewsAsync(CancellationToken cancellationToken) =>
        (await devices.ListDevicesAsync(cancellationToken)).Select(ToView).ToArray();

    public async Task<RemoteDeviceView?> GetDeviceViewAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = await devices.GetDeviceAsync(deviceId, cancellationToken);
        return device is null ? null : ToView(device);
    }

    public RemoteDeviceView ToView(DeviceRecord device) => new(
        device.DeviceId,
        device.DeviceSerial,
        device.LastLocation is null ? null : GeoConvert.ToGcj02(device.LastLocation),
        device.LatestCaptureId)
    {
        LastLocationWgs84 = device.LastLocation,
        LastSeenAtUtc = device.LastSeenAtUtc,
        Online = device.LastSeenAtUtc is { } seen &&
                 DateTimeOffset.UtcNow - seen <= TimeSpan.FromSeconds(Math.Max(1, _options.OnlineWindowSeconds)),
        LatestCaptureAtUtc = device.LatestCaptureAtUtc,
        LastEnvironment = device.LastEnvironment,
        LastPower = device.LastPower,
        ImageVersion = device.ImageVersion
    };

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();
}
