using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Mosquito.Client.Core;

public sealed record WorkflowProgress(string Stage, string Message, int Percent);
public sealed record LiveBoardStatus(EnvironmentReading? Environment, PowerReading? Power, string? Error);

public sealed partial class CaptureWorkflow(
    AppSettings settings,
    IAdbClient adb,
    KeyValueMetadataParser metadataParser,
    CloudApiClient cloud,
    OutboxRepository outbox)
{
    public async Task<bool> IsDeviceOnlineAsync(CancellationToken cancellationToken)
    {
        var devices = await adb.GetDevicesAsync(cancellationToken);
        return devices.Any(device =>
            device.Serial == settings.DeviceSerial &&
            device.State.Equals("device", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<LiveBoardStatus> ReadLiveStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var environmentResult = await adb.ShellAsync(
                settings.DeviceSerial,
                $"PATH={ValidatedCommandPath()} mosquito-environment --machine",
                cancellationToken);
            var powerResult = await adb.ShellAsync(
                settings.DeviceSerial,
                $"PATH={ValidatedCommandPath()} mosquito-power --machine",
                cancellationToken);
            var environmentValues = MachineValues.Parse(environmentResult.StandardOutput);
            var powerValues = MachineValues.Parse(powerResult.StandardOutput);
            return new LiveBoardStatus(
                MachineValues.ToEnvironment(environmentValues),
                MachineValues.ToPower(powerValues),
                environmentResult.ExitCode == 0 && powerResult.ExitCode == 0
                    ? null
                    : "一个或多个实时传感器返回警告或失败");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LiveBoardStatus(null, null, exception.Message);
        }
    }

    public async Task<CaptureArtifact> CaptureAndUploadAsync(
        UploadRoute route,
        int? manualFocus,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await IsDeviceOnlineAsync(cancellationToken))
        {
            throw new InvalidOperationException($"ADB device {settings.DeviceSerial} is not online.");
        }
        if (manualFocus is < 1 or > 1023)
        {
            throw new ArgumentOutOfRangeException(nameof(manualFocus), "Manual focus must be in 1..1023.");
        }

        var id = Guid.NewGuid();
        var hostStartedAt = DateTimeOffset.UtcNow;
        var focus = manualFocus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto";
        var command = route == UploadRoute.Windows
            ? $"PATH={ValidatedCommandPath()} mosquito-capture --id {id:D} --focus {focus}"
            : $"PATH={ValidatedCommandPath()} mosquito-capture-4g --id {id:D} --focus {focus}";
        progress?.Report(new WorkflowProgress("capture", "正在唤醒相机并拍照", 15));
        var capture = await adb.ShellAsync(settings.DeviceSerial, command, cancellationToken);
        var hostCompletedAt = DateTimeOffset.UtcNow;
        var acceptedExit = route == UploadRoute.Windows
            ? capture.ExitCode is 0 or 10
            : capture.ExitCode is 0 or 10 or 20;
        if (!acceptedExit)
        {
            throw new InvalidOperationException(
                $"Board capture failed with exit {capture.ExitCode}: {capture.StandardError}\n{capture.StandardOutput}");
        }
        var markers = MachineValues.Parse(capture.StandardOutput);
        var remotePhoto = RequiredMarker(markers, "PHOTO");
        var remoteMetadata = RequiredMarker(markers, "METADATA");
        var localDirectory = Path.Combine(settings.LocalDataRoot, "captures", id.ToString("D"));
        var localPhoto = Path.Combine(localDirectory, "photo.jpg");
        var localMetadata = Path.Combine(localDirectory, "metadata.txt");
        progress?.Report(new WorkflowProgress("pull", "正在从开发板读取照片和元数据", 45));
        var photoPull = await adb.PullAsync(settings.DeviceSerial, remotePhoto, localPhoto, cancellationToken);
        var metadataPull = await adb.PullAsync(settings.DeviceSerial, remoteMetadata, localMetadata, cancellationToken);
        if (photoPull.ExitCode != 0 || metadataPull.ExitCode != 0)
        {
            throw new InvalidOperationException("ADB pull failed; the original files remain on the board.");
        }

        progress?.Report(new WorkflowProgress("verify", "正在校验照片和传感器数据", 60));
        var parsed = metadataParser.Parse(await File.ReadAllTextAsync(localMetadata, cancellationToken), id);
        var photoSha = await Sha256Async(localPhoto, cancellationToken);
        var metadataSha = await Sha256Async(localMetadata, cancellationToken);
        var photoBytes = new FileInfo(localPhoto).Length;
        var metadataBytes = new FileInfo(localMetadata).Length;
        if (!OperatorFixedEquals(photoSha, parsed.JpegSha256) || photoBytes != parsed.JpegBytes)
        {
            throw new InvalidDataException("Pulled photo does not match board metadata SHA-256 or byte count.");
        }
        var dimensions = JpegInspector.ReadDimensions(localPhoto);
        if (dimensions.Width != parsed.JpegWidth || dimensions.Height != parsed.JpegHeight)
        {
            throw new InvalidDataException("JPEG dimensions do not match board metadata.");
        }

        var boardCloudStatus = markers.GetValueOrDefault("CLOUD_CAPTURE_STATUS");
        var initialState = route == UploadRoute.Windows
            ? CaptureState.PendingUpload
            : capture.ExitCode == 20 || boardCloudStatus == "FAILED"
                ? CaptureState.Failed
                : ToFinalState(parsed.RecordStatus);
        var artifact = new CaptureArtifact(
            id,
            settings.DeviceSerial,
            route,
            hostStartedAt + TimeSpan.FromTicks((hostCompletedAt - hostStartedAt).Ticks / 2),
            localPhoto,
            localMetadata,
            photoSha,
            metadataSha,
            photoBytes,
            metadataBytes,
            parsed,
            initialState,
            initialState == CaptureState.Failed ? "4G upload failed without Windows fallback." : null);
        await outbox.SaveAsync(artifact, cancellationToken);

        if (route == UploadRoute.Board4G)
        {
            progress?.Report(new WorkflowProgress("complete", "开发板 4G 上传完成", 100));
            return artifact;
        }

        try
        {
            progress?.Report(new WorkflowProgress("upload", "正在通过 Windows 网络上传", 75));
            await outbox.MarkAsync(artifact, CaptureState.Uploading, null, false, cancellationToken);
            var uploaded = await cloud.UploadArtifactAsync(artifact, cancellationToken);
            var finalState = uploaded.Status == "Complete" ? CaptureState.Complete : CaptureState.Partial;
            var completed = artifact with { State = finalState };
            await outbox.MarkAsync(completed, finalState, null, true, cancellationToken);
            progress?.Report(new WorkflowProgress("complete", "拍照、校验和上传全部完成", 100));
            return completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = artifact with { State = CaptureState.Failed, LastError = exception.Message };
            await outbox.MarkAsync(failed, CaptureState.Failed, exception.Message, true, cancellationToken);
            progress?.Report(new WorkflowProgress("queued", "上传失败，照片已进入本地重试队列", 100));
            return failed;
        }
    }

    public async Task<int> RetryPendingUploadsAsync(
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        var succeeded = 0;
        foreach (var artifact in await outbox.GetPendingAsync(cancellationToken))
        {
            if (artifact.UploadRoute != UploadRoute.Windows)
            {
                continue;
            }
            try
            {
                progress?.Report(new WorkflowProgress("retry", $"重试 {artifact.CaptureId:D}", 0));
                var uploaded = await cloud.UploadArtifactAsync(artifact, cancellationToken);
                var state = uploaded.Status == "Complete" ? CaptureState.Complete : CaptureState.Partial;
                await outbox.MarkAsync(artifact with { State = state }, state, null, true, cancellationToken);
                succeeded++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await outbox.MarkAsync(artifact, CaptureState.Failed, exception.Message, true, cancellationToken);
            }
        }
        return succeeded;
    }

    private string ValidatedCommandPath()
    {
        if (!CommandPathPattern().IsMatch(settings.BoardCommandPath))
        {
            throw new InvalidOperationException("BoardCommandPath contains unsupported characters.");
        }
        return settings.BoardCommandPath;
    }

    private static string RequiredMarker(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Board output did not contain {name}=... .");

    private static CaptureState ToFinalState(string recordStatus) =>
        recordStatus == "COMPLETE" ? CaptureState.Complete : CaptureState.Partial;

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool OperatorFixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));

    [GeneratedRegex("^[A-Za-z0-9_./:-]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandPathPattern();
}

internal static class MachineValues
{
    public static Dictionary<string, string> Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }
        return values;
    }

    public static EnvironmentReading ToEnvironment(IReadOnlyDictionary<string, string> values) => new(
        values.GetValueOrDefault("RESULT") ?? "FAIL",
        values.GetValueOrDefault("ERROR_CODE") ?? "NO_OUTPUT",
        Int(values, "TEMPERATURE_CENTI_C"),
        Int(values, "HUMIDITY_CENTI_RH"),
        Int(values, "CRC_OK") is int crc ? crc == 1 : null,
        Long(values, "SAMPLED_UPTIME_MS"));

    public static PowerReading ToPower(IReadOnlyDictionary<string, string> values) => new(
        values.GetValueOrDefault("RESULT") ?? "FAIL",
        values.GetValueOrDefault("ERROR_CODE") ?? "NO_OUTPUT",
        Int(values, "BATTERY_MV"),
        values.GetValueOrDefault("CHARGE_STATE"),
        Int(values, "VBUS_GOOD") is int vbus ? vbus == 1 : null,
        Int(values, "VBUS_MV"),
        Int(values, "CHARGE_CURRENT_MA"),
        values.GetValueOrDefault("FAULT_REG"),
        Long(values, "SAMPLED_UPTIME_MS"));

    private static int? Int(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;

    private static long? Long(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) && long.TryParse(raw, out var value) ? value : null;
}

internal static class JpegInspector
{
    public static (int Width, int Height) ReadDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
        {
            throw new InvalidDataException("Photo is not a JPEG file.");
        }
        while (stream.Position < stream.Length)
        {
            int prefix;
            do
            {
                prefix = stream.ReadByte();
            } while (prefix != 0xff && prefix >= 0);
            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda)
            {
                break;
            }
            if (marker is >= 0xd0 and <= 0xd7 || marker == 0x01)
            {
                continue;
            }
            var length = ReadUInt16BigEndian(stream);
            if (length < 2)
            {
                throw new InvalidDataException("JPEG contains an invalid segment length.");
            }
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                _ = stream.ReadByte();
                var height = ReadUInt16BigEndian(stream);
                var width = ReadUInt16BigEndian(stream);
                return (width, height);
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("JPEG dimensions were not found.");
    }

    private static int ReadUInt16BigEndian(Stream stream)
    {
        var high = stream.ReadByte();
        var low = stream.ReadByte();
        if (high < 0 || low < 0)
        {
            throw new EndOfStreamException();
        }
        return (high << 8) | low;
    }
}
