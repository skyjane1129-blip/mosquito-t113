using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mosquito.Cloud.Api;

// What one detector run returns, independent of how it was executed (python subprocess in production,
// a fake in the smoke tests).
public sealed record EggAnalysisResult(
    string Model,
    int EggCount,
    int MosquitoCount,
    int Width,
    int Height,
    int Tiles,
    int DurationMs,
    EggDetection[] Detections);

public interface IEggAnalysisRunner
{
    Task<EggAnalysisResult> RunAsync(string imagePath, string annotatedImagePath, CancellationToken cancellationToken);
}

// Runs ai/analyze.py (which wraps the model author's count.py logic) as a child process. Kept as a
// subprocess rather than an in-process runtime so the model, its Python environment and the .NET service
// can be updated independently and a crash in the detector never takes the API down.
public sealed class PythonEggAnalysisRunner(
    IOptions<AnalysisOptions> options,
    IWebHostEnvironment environment,
    ILogger<PythonEggAnalysisRunner> logger) : IEggAnalysisRunner
{
    private readonly AnalysisOptions _options = options.Value;

    public string ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(_options.PythonPath))
        {
            return Resolve(_options.PythonPath);
        }
        // Preferred: the self-contained embeddable CPython checked into ai/python311 (no installer, no admin);
        // then a classic venv; finally whatever "python" is on PATH.
        foreach (var candidate in new[] { "ai/python311/python.exe", "ai/.venv/Scripts/python.exe" })
        {
            var resolved = Resolve(candidate);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }
        return "python";
    }

    public async Task<EggAnalysisResult> RunAsync(string imagePath, string annotatedImagePath, CancellationToken cancellationToken)
    {
        var script = Resolve(_options.ScriptPath);
        var model = Resolve(_options.ModelPath);
        if (!File.Exists(script))
        {
            throw new FileNotFoundException("Analysis script is missing.", script);
        }
        if (!File.Exists(model))
        {
            throw new FileNotFoundException("Model weights are missing.", model);
        }
        var jsonPath = Path.ChangeExtension(annotatedImagePath, ".json");
        var start = new ProcessStartInfo(ResolvePython())
        {
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     script, "--weights", model, "--image", imagePath, "--out-json", jsonPath, "--out-image", annotatedImagePath,
                     "--conf", _options.Confidence.ToString(CultureInfo.InvariantCulture),
                     "--egg-tile", _options.EggTile.ToString(CultureInfo.InvariantCulture),
                     "--ref-width", _options.RefWidth.ToString(CultureInfo.InvariantCulture),
                     "--cls-thr", _options.MosquitoClassifierThreshold.ToString(CultureInfo.InvariantCulture),
                     "--det-conf", _options.MosquitoCandidateConfidence.ToString(CultureInfo.InvariantCulture)
                 })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["YOLO_VERBOSE"] = "False";

        using var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < 8000) stderr.AppendLine(e.Data); };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginErrorReadLine();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.TimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"Detector did not finish within {_options.TimeoutSeconds} s.");
        }
        var stdout = await stdoutTask;
        logger.LogInformation("analyze.py exit {Code} in {Elapsed} ms: {Stdout}", process.ExitCode, stopwatch.ElapsedMilliseconds, stdout.Trim());
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Detector exited with code {process.ExitCode}: {Tail(stderr.ToString())}");
        }
        if (!File.Exists(jsonPath) || !File.Exists(annotatedImagePath))
        {
            throw new InvalidOperationException($"Detector produced no output: {Tail(stderr.ToString())}");
        }
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(jsonPath, cancellationToken));
        var root = document.RootElement;
        var detections = root.GetProperty("detections").EnumerateArray().Select(d => new EggDetection(
            d.GetProperty("name").GetString() ?? "?",
            d.GetProperty("conf").GetDouble(),
            d.GetProperty("x1").GetDouble(), d.GetProperty("y1").GetDouble(),
            d.GetProperty("x2").GetDouble(), d.GetProperty("y2").GetDouble())).ToArray();
        return new EggAnalysisResult(
            root.GetProperty("model").GetString() ?? _options.ModelVersion,
            root.GetProperty("eggCount").GetInt32(),
            root.GetProperty("mosquitoCount").GetInt32(),
            root.GetProperty("width").GetInt32(),
            root.GetProperty("height").GetInt32(),
            root.GetProperty("tiles").GetInt32(),
            root.GetProperty("durationMs").GetInt32(),
            detections);
    }

    // Relative paths are looked up from the content root (src/Mosquito.Cloud.Api when run from the build
    // output) and then its parents, so "ai/..." resolves to the repository-level ai/ folder in development
    // and to a sibling ai/ folder next to a published build.
    private string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }
        var directory = new DirectoryInfo(environment.ContentRootPath);
        for (var depth = 0; directory is not null && depth < 4; depth++, directory = directory.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.FullName, path));
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1200 ? trimmed : trimmed[^1200..];
    }
}

// Orchestrates one analysis per capture: marks the record Running, runs the detector in the background
// (one at a time, the model is CPU-heavy), stores the annotated JPEG next to the photo and writes the
// counts and boxes back into the capture record. Operators poll GET .../analysis until it is final.
public sealed class EggAnalysisService(
    ICaptureRepository repository,
    IObjectStorage storage,
    IEggAnalysisRunner runner,
    IOptions<AnalysisOptions> options,
    ILogger<EggAnalysisService> logger)
{
    private readonly AnalysisOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public bool Enabled => _options.Enabled;

    public async Task<AnalysisResponse> StartAsync(Guid captureId, bool force, string requestedBy, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Photo analysis is disabled on this server.");
        }
        var capture = await repository.GetAsync(captureId, cancellationToken)
            ?? throw new KeyNotFoundException("Capture does not exist.");
        if (capture.Status is not (CaptureStatus.Complete or CaptureStatus.Partial))
        {
            throw new CaptureValidationException("The photo has not been uploaded yet.");
        }
        var existing = capture.Analysis;
        var staleRunning = existing is { Status: AnalysisStatus.Running } &&
                           existing.StartedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-_options.TimeoutSeconds - 60) &&
                           !_inFlight.ContainsKey(captureId);
        if (existing is not null && !staleRunning)
        {
            if (existing.Status == AnalysisStatus.Running || (existing.Status == AnalysisStatus.Completed && !force))
            {
                return Build(capture);
            }
        }

        var running = new EggAnalysis(AnalysisStatus.Running, _options.ModelVersion, _options.Confidence, DateTimeOffset.UtcNow)
        {
            RequestedBy = requestedBy
        };
        var updated = capture with { Analysis = running, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await repository.UpdateAsync(updated, cancellationToken);
        _inFlight[captureId] = Task.Run(() => RunAsync(updated), CancellationToken.None);
        return Build(updated);
    }

    public async Task<AnalysisResponse?> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var capture = await repository.GetAsync(captureId, cancellationToken);
        return capture is null ? null : Build(capture);
    }

    // Exposed for tests: wait for the background run of one capture to settle.
    public Task WaitAsync(Guid captureId) => _inFlight.TryGetValue(captureId, out var task) ? task : Task.CompletedTask;

    // Called once at startup: a run that was in progress when the previous process died would otherwise
    // stay "Running" (and keep the client waiting) until the stale-run timeout expires.
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var captures = await repository.QueryAsync(null, null, null, null, null, null, cancellationToken);
        var recovered = 0;
        foreach (var capture in captures.Where(c => c.Analysis is { Status: AnalysisStatus.Running }))
        {
            await repository.UpdateAsync(capture with
            {
                Analysis = capture.Analysis! with
                {
                    Status = AnalysisStatus.Failed,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Error = "服务端在识别过程中重启，请重新识别。"
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            recovered++;
        }
        if (recovered > 0)
        {
            logger.LogWarning("Marked {Count} interrupted analyses as failed at startup", recovered);
        }
    }

    public string? AnnotatedUrl(CaptureRecord capture) =>
        capture.Analysis is { Status: AnalysisStatus.Completed, AnnotatedObjectKey: { } key }
            ? storage.CreateDownloadTarget(key).Url
            : null;

    private AnalysisResponse Build(CaptureRecord capture) => new(capture.Id, capture.Analysis, AnnotatedUrl(capture));

    private async Task RunAsync(CaptureRecord capture)
    {
        var started = capture.Analysis!;
        await _gate.WaitAsync();
        var workDir = Path.Combine(Path.GetTempPath(), "mosquito-analysis", capture.Id.ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);
            var photoPath = Path.Combine(workDir, "photo.jpg");
            var annotatedPath = Path.Combine(workDir, "annotated.jpg");
            await using (var input = await storage.OpenReadAsync(capture.PhotoObjectKey, CancellationToken.None))
            await using (var output = File.Create(photoPath))
            {
                await input.CopyToAsync(output);
            }
            var result = await runner.RunAsync(photoPath, annotatedPath, CancellationToken.None);
            var annotatedKey = AnnotatedKey(capture.PhotoObjectKey, _options.ModelVersion);
            await using (var annotated = File.OpenRead(annotatedPath))
            {
                await storage.PutAsync(annotatedKey, annotated, "image/jpeg", CancellationToken.None);
            }
            await Finish(capture.Id, started with
            {
                Status = AnalysisStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = result.DurationMs,
                EggCount = result.EggCount,
                MosquitoCount = result.MosquitoCount,
                ImageWidth = result.Width,
                ImageHeight = result.Height,
                Tiles = result.Tiles,
                Detections = result.Detections,
                AnnotatedObjectKey = annotatedKey
            });
            logger.LogInformation("Capture {Id}: egg {Egg} mosquito {Mosquito} in {Ms} ms", capture.Id, result.EggCount, result.MosquitoCount, result.DurationMs);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis of capture {Id} failed", capture.Id);
            try
            {
                await Finish(capture.Id, started with
                {
                    Status = AnalysisStatus.Failed,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Error = exception.Message
                });
            }
            catch (Exception inner)
            {
                logger.LogError(inner, "Could not record analysis failure for capture {Id}", capture.Id);
            }
        }
        finally
        {
            _gate.Release();
            _inFlight.TryRemove(capture.Id, out _);
            try { Directory.Delete(workDir, true); } catch { /* best effort */ }
        }
    }

    // Re-read the record so a heartbeat/location update that raced with the detector is not overwritten.
    private async Task Finish(Guid id, EggAnalysis analysis)
    {
        var latest = await repository.GetAsync(id, CancellationToken.None)
            ?? throw new InvalidOperationException("Capture vanished during analysis.");
        await repository.UpdateAsync(latest with { Analysis = analysis, UpdatedAtUtc = DateTimeOffset.UtcNow }, CancellationToken.None);
    }

    public static string AnnotatedKey(string photoObjectKey, string modelVersion)
    {
        var slash = photoObjectKey.LastIndexOf('/');
        var prefix = slash < 0 ? string.Empty : photoObjectKey[..(slash + 1)];
        return $"{prefix}analysis-{modelVersion}.jpg";
    }
}
