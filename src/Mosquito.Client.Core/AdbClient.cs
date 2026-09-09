using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private static readonly SemaphoreSlim DiagnosticLock = new(1, 1);

    public async Task<string> GetVersionAsync(CancellationToken token)
    {
        string[] arguments = ["version"];
        var result = await ExecuteAsync(arguments, TimeSpan.FromSeconds(8), token);
        await WriteDiagnosticAsync("version", arguments, null, result.ExitCode, null, result, null);
        if (result.ExitCode != 0) throw new InvalidOperationException("ADB 组件不能正常运行：" + result.StandardError.Trim());
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "ADB 可运行";
    }
    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        string[] arguments = ["devices", "-l"];
        var result = await ExecuteAsync(arguments, TimeSpan.FromSeconds(15), cancellationToken);
        await WriteDiagnosticAsync("devices", arguments, null, result.ExitCode, null, result, null);
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

    public async Task<CommandResult> ShellAsync(
        string serial,
        string remoteCommand,
        CancellationToken cancellationToken)
    {
        ValidateSerial(serial);
        var marker = $"__MOSQUITO_REMOTE_EXIT_{Guid.NewGuid():N}__";
        var wrappedCommand = $"{remoteCommand}; mosquito_remote_rc=$?; printf '\\n{marker}=%s\\n' \"$mosquito_remote_rc\"; exit \"$mosquito_remote_rc\"";
        var hostResult = await ExecuteAsync(
            ["-s", serial, "shell", wrappedCommand],
            TimeSpan.FromSeconds(settings.CommandTimeoutSeconds),
            cancellationToken);
        var normalized = hostResult.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        var lastNewline = normalized.LastIndexOf('\n');
        var finalLine = lastNewline >= 0 ? normalized[(lastNewline + 1)..] : normalized;
        var expectedPrefix = marker + "=";
        var markerLines = normalized.Split('\n')
            .Where(line => line.StartsWith(expectedPrefix, StringComparison.Ordinal))
            .ToArray();
        if (markerLines.Length != 1 ||
            markerLines[0] != finalLine ||
            !int.TryParse(finalLine[expectedPrefix.Length..], out var remoteExitCode) ||
            remoteExitCode is < 0 or > 255)
        {
            var protocolError = hostResult.ExitCode == 0
                ? "missing-or-invalid-remote-exit-marker"
                : "adb-transport-failure-without-remote-exit-marker";
            await WriteDiagnosticAsync("shell", ["-s", serial, "shell"], remoteCommand,
                hostResult.ExitCode, null, hostResult, protocolError);
            if (hostResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ADB transport failed with host exit {hostResult.ExitCode}: {hostResult.StandardError.Trim()}");
            }
            throw new InvalidDataException("ADB shell did not report a valid board command exit status.");
        }

        var businessOutput = lastNewline >= 0 ? normalized[..lastNewline].TrimEnd('\n') : string.Empty;
        var result = hostResult with { ExitCode = remoteExitCode, StandardOutput = businessOutput };
        await WriteDiagnosticAsync("shell", ["-s", serial, "shell"], remoteCommand,
            hostResult.ExitCode, remoteExitCode, result, null);
        return result;
    }

    public async Task<CommandResult> PullAsync(
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
        string[] arguments = ["-s", serial, "pull", remotePath, localPath];
        var result = await ExecuteAsync(
            arguments,
            TimeSpan.FromSeconds(60),
            cancellationToken);
        await WriteDiagnosticAsync("pull", arguments, null, result.ExitCode, null, result, null);
        return result;
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
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"ADB command exceeded {timeout.TotalSeconds:F0} seconds.");
        }
        started.Stop();
        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask,
            started.Elapsed);
    }

    private async Task WriteDiagnosticAsync(
        string operation,
        IReadOnlyList<string> arguments,
        string? remoteCommand,
        int hostExitCode,
        int? remoteExitCode,
        CommandResult result,
        string? protocolError)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalDataRoot))
        {
            return;
        }
        try
        {
            await DiagnosticLock.WaitAsync(CancellationToken.None);
            try
            {
                var directory = Path.Combine(settings.LocalDataRoot, "diagnostics");
                Directory.CreateDirectory(directory);
                var logPath = Path.Combine(directory, "adb-commands.jsonl");
                var previousLogPath = logPath + ".1";
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= 5 * 1024 * 1024)
                {
                    if (File.Exists(previousLogPath))
                        File.Delete(previousLogPath);
                    File.Move(logPath, previousLogPath);
                }
                var completedAt = DateTimeOffset.UtcNow;
                var entry = JsonSerializer.Serialize(new
                {
                    startedAtUtc = completedAt - result.Duration,
                    completedAtUtc = completedAt,
                    operation,
                    arguments = arguments.Select(Redact).ToArray(),
                    remoteCommand = remoteCommand is null ? null : Redact(remoteCommand),
                    hostExitCode,
                    remoteExitCode,
                    durationMs = Math.Round(result.Duration.TotalMilliseconds, 3),
                    standardOutput = Redact(result.StandardOutput),
                    standardError = Redact(result.StandardError),
                    protocolError
                });
                await File.AppendAllTextAsync(
                    logPath,
                    entry + Environment.NewLine,
                    new UTF8Encoding(false),
                    CancellationToken.None);
            }
            finally
            {
                DiagnosticLock.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must not turn a successful board operation into a failed capture.
        }
    }

    private static string Redact(string value)
    {
        var withoutAuthorization = Regex.Replace(
            value,
            @"(\bAuthorization\b\s*[:=]\s*)(?:Bearer\s+)?[^\s""'&;]+",
            "$1[REDACTED]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            withoutAuthorization,
            @"(\b(?:access[_-]?token|token|signature|sig)\b\s*[:=]\s*)[^\s""'&;]+",
            "$1[REDACTED]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
