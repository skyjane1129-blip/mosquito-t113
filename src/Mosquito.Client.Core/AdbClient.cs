using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Mosquito.Client.Core;

public interface IAdbClient
{
    Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<CommandResult> ShellAsync(string serial, string remoteCommand, CancellationToken cancellationToken);
    Task<CommandResult> PullAsync(string serial, string remotePath, string localPath, CancellationToken cancellationToken);
}

public sealed partial class AdbClient(AppSettings settings) : IAdbClient
{
    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["devices", "-l"], TimeSpan.FromSeconds(15), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ADB devices failed: {result.StandardError.Trim()}");
        }
        return result.StandardOutput
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(ParseDevice)
            .Where(device => device is not null)
            .Cast<AdbDevice>()
            .ToArray();
    }

    public Task<CommandResult> ShellAsync(
        string serial,
        string remoteCommand,
        CancellationToken cancellationToken)
    {
        ValidateSerial(serial);
        return ExecuteAsync(
            ["-s", serial, "shell", remoteCommand],
            TimeSpan.FromSeconds(settings.CommandTimeoutSeconds),
            cancellationToken);
    }

    public Task<CommandResult> PullAsync(
        string serial,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken)
    {
        ValidateSerial(serial);
        if (!remotePath.StartsWith("/mnt/UDISK/mosquito-test/camera/", StringComparison.Ordinal) ||
            remotePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Remote capture path is outside the approved camera directory.", nameof(remotePath));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        return ExecuteAsync(
            ["-s", serial, "pull", remotePath, localPath],
            TimeSpan.FromSeconds(60),
            cancellationToken);
    }

    private async Task<CommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.AdbPath))
        {
            throw new FileNotFoundException("ADB executable was not found.", settings.AdbPath);
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.AdbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("ADB process did not start.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"ADB command exceeded {timeout.TotalSeconds:F0} seconds.");
        }
        started.Stop();
        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask,
            started.Elapsed);
    }

    private static AdbDevice? ParseDevice(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : new AdbDevice(parts[0], parts[1], string.Join(' ', parts.Skip(2)));
    }

    private static void ValidateSerial(string serial)
    {
        if (!SerialPattern().IsMatch(serial))
        {
            throw new ArgumentException("ADB serial contains unsupported characters.", nameof(serial));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
}
