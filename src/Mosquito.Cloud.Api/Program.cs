using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Mosquito.Cloud.Api;

public partial class Program
{
    private const int PageSize = 50;

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Machine-local secrets and the evaluation reference point live outside Git (see .gitignore).
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
        builder.Services.Configure<DeviceAuthOptions>(builder.Configuration.GetSection("DeviceAuth"));
        builder.Services.Configure<RepositoryOptions>(builder.Configuration.GetSection("Repository"));
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
        builder.Services.Configure<DeviceOptions>(builder.Configuration.GetSection("Device"));
        builder.Services.Configure<LocationOptions>(builder.Configuration.GetSection("Location"));
        builder.Services.AddHttpClient("locators", client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton<ICellLocator, AmapCellLocator>();
        builder.Services.AddSingleton<ICellLocator, BaiduCellLocator>();
        builder.Services.AddSingleton<ICellLocator, OpenCellIdLocator>();
        builder.Services.AddSingleton<LocationEngine>();
        builder.Services.AddSingleton<OperatorTokenService>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<CaptureService>();
        builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection("Analysis"));
        builder.Services.AddSingleton<IEggAnalysisRunner, PythonEggAnalysisRunner>();
        builder.Services.AddSingleton<EggAnalysisService>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("login", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
        });

        var repositoryOptions = builder.Configuration.GetSection("Repository").Get<RepositoryOptions>() ?? new();
        if (repositoryOptions.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<ICaptureRepository, PostgresCaptureRepository>();
            builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
        }
        else if (repositoryOptions.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton(SqliteDatabase.FromOptions(
                repositoryOptions.ConnectionString,
                builder.Environment.ContentRootPath));
            builder.Services.AddSingleton<ICaptureRepository, SqliteCaptureRepository>();
            builder.Services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
        }
        else
        {
            builder.Services.AddSingleton<ICaptureRepository, InMemoryCaptureRepository>();
            builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
        }

        var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
        if (storageOptions.Provider.Equals("AliyunOss", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IObjectStorage, AliyunOssObjectStorage>();
        }
        else
        {
            builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
        }

        ValidateProductionConfiguration(builder.Environment, builder.Configuration);
        var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (CaptureValidationException exception)
            {
                await Results.Problem(exception.Message, statusCode: 400).ExecuteAsync(context);
            }
            catch (CaptureConflictException exception)
            {
                await Results.Problem(exception.Message, statusCode: 409).ExecuteAsync(context);
            }
            catch (KeyNotFoundException exception)
            {
                await Results.Problem(exception.Message, statusCode: 404).ExecuteAsync(context);
            }
            catch (UnauthorizedAccessException exception)
            {
                await Results.Problem(exception.Message, statusCode: 401).ExecuteAsync(context);
            }
            catch (InvalidDataException exception)
            {
                await Results.Problem(exception.Message, statusCode: 400).ExecuteAsync(context);
            }
        });

        app.MapGet("/health", (
            IWebHostEnvironment environment,
            IOptions<RepositoryOptions> repository,
            IOptions<StorageOptions> storage) => Results.Ok(new
        {
            status = "ok",
            environment = environment.EnvironmentName,
            repository = repository.Value.Provider,
            storage = storage.Value.Provider,
            utc = DateTimeOffset.UtcNow
        }));

        app.MapPost("/api/auth/login", (LoginRequest request, OperatorTokenService tokens) =>
        {
            var response = tokens.Login(request);
            return response is null ? Results.Unauthorized() : Results.Ok(response);
        }).RequireRateLimiting("login");

        // ---- Capture upload (operator token or device key) ----

        app.MapPost("/api/captures", async (
            HttpContext context,
            CreateCaptureRequest request,
            CaptureService captures,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            var response = await captures.CreateAsync(request, cancellationToken);
            return Results.Ok(response);
        });

        app.MapPost("/api/captures/{id:guid}/complete", async (
            HttpContext context,
            Guid id,
            CompleteCaptureRequest request,
            CaptureService captures,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(CaptureService.ForOperator(await captures.CompleteAsync(id, request, cancellationToken)));
        });

        app.MapGet("/api/captures", async (
            HttpContext context,
            string? deviceSerial,
            CaptureStatus? status,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            CaptureService captures,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var items = await captures.ListAsync(deviceSerial, status, from, to, limit ?? 100, cancellationToken);
            return Results.Ok(items.Select(CaptureService.ForOperator).ToArray());
        });

        app.MapGet("/api/captures/{id:guid}", async (
            HttpContext context,
            Guid id,
            CaptureService captures,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var response = await captures.GetDetailAsync(id, cancellationToken);
            return response is null
                ? Results.NotFound()
                : Results.Ok(response with { Capture = CaptureService.ForOperator(response.Capture) });
        });

        app.MapPut("/api/uploads/{token}", async (
            HttpContext context,
            string token,
            IObjectStorage storage,
            CancellationToken cancellationToken) =>
        {
            if (storage is not LocalObjectStorage)
            {
                return Results.NotFound();
            }
            if (context.Request.ContentLength is null or <= 0 or > 20 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Content-Length must be between 1 byte and 20 MiB." });
            }
            var info = await storage.AcceptLocalUploadAsync(token, context.Request.Body, cancellationToken);
            return Results.Ok(info);
        });

        app.MapGet("/api/downloads/{token}", (string token, IObjectStorage storage) =>
        {
            if (storage is not LocalObjectStorage local)
            {
                return Results.NotFound();
            }
            var path = local.ResolveDownloadToken(token);
            return File.Exists(path)
                ? Results.File(path, "image/jpeg", enableRangeProcessing: true)
                : Results.NotFound();
        });

        // ---- Device side: heartbeat, command polling, acknowledgement, location ----

        app.MapPost("/api/device/heartbeat", async (
            HttpContext context,
            HeartbeatRequest request,
            DeviceService devices,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(devices.ToView(await devices.HeartbeatAsync(request, cancellationToken)));
        });

        app.MapPost("/api/device/location", async (
            HttpContext context,
            LocationReport report,
            DeviceService devices,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(devices.ToView(await devices.RecordLocationAsync(report, cancellationToken)));
        });

        app.MapGet("/api/device/commands", async (
            HttpContext context,
            string? deviceId,
            int? wait,
            DeviceService devices,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.BadRequest(new { error = "deviceId is required." });
            }
            var command = await devices.WaitForCommandAsync(deviceId, wait ?? 0, cancellationToken);
            return command is null ? Results.NoContent() : Results.Ok(command);
        });

        app.MapPost("/api/device/commands/{id:guid}/ack", async (
            HttpContext context,
            Guid id,
            CommandAckRequest request,
            DeviceService devices,
            OperatorTokenService tokens,
            IOptions<DeviceAuthOptions> deviceAuth,
            CancellationToken cancellationToken) =>
        {
            if (!IsCaptureWriter(context, tokens, deviceAuth.Value))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await devices.AcknowledgeAsync(id, request, cancellationToken));
        });

        // ---- Operator v2: contract consumed by the Windows client (docs/HISTORY_REPORT_API.md) ----

        app.MapGet("/api/v2/devices", async (
            HttpContext context,
            int? limit,
            string? cursor,
            string? snapshot,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var items = await devices.ListDeviceViewsAsync(cancellationToken);
            return Results.Ok(Page(items, limit, cursor, snapshot ?? NewSnapshot()));
        });

        app.MapGet("/api/v2/devices/{deviceId}", async (
            HttpContext context,
            string deviceId,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var view = await devices.GetDeviceViewAsync(deviceId, cancellationToken);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        app.MapGet("/api/v2/captures", async (
            HttpContext context,
            DateTimeOffset? from,
            DateTimeOffset? toExclusive,
            string? deviceId,
            string? status,
            int? limit,
            string? cursor,
            string? snapshot,
            CaptureService captures,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            CaptureStatus? statusFilter = status?.ToUpperInvariant() switch
            {
                null or "" => null,
                "COMPLETE" => CaptureStatus.Complete,
                "PARTIAL" => CaptureStatus.Partial,
                "FAILED" => CaptureStatus.Failed,
                _ => throw new CaptureValidationException("status must be Complete, Partial or Failed.")
            };
            var effectiveSnapshot = snapshot ?? NewSnapshot();
            var asOf = ParseSnapshot(effectiveSnapshot);
            var to = toExclusive?.AddTicks(-1);
            var items = (await captures.QueryAsync(deviceId, statusFilter, from, to, cancellationToken))
                .Where(c => c.Status is CaptureStatus.Complete or CaptureStatus.Partial or CaptureStatus.Failed)
                .Where(c => c.CreatedAtUtc <= asOf)
                .Select(CaptureService.ForOperator)
                .ToArray();
            return Results.Ok(Page(items, limit, cursor, effectiveSnapshot));
        });

        app.MapPost("/api/v2/devices/{deviceId}/commands", async (
            HttpContext context,
            string deviceId,
            CreateCommandRequest request,
            DeviceService devices,
            OperatorTokenService tokens,
            IOptions<AuthOptions> auth,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await devices.CreateCommandAsync(deviceId, request, auth.Value.Username, cancellationToken));
        });

        app.MapGet("/api/v2/devices/{deviceId}/commands", async (
            HttpContext context,
            string deviceId,
            int? limit,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await devices.ListCommandsAsync(deviceId, limit ?? 20, cancellationToken));
        });

        app.MapGet("/api/v2/devices/{deviceId}/locations", async (
            HttpContext context,
            string deviceId,
            int? limit,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await devices.ListLocationsAsync(deviceId, limit ?? 100, cancellationToken));
        });

        // Per-source accuracy against the configured reference point (Location:Reference* in appsettings.Local.json).
        app.MapGet("/api/v2/devices/{deviceId}/location-evaluation", async (
            HttpContext context,
            string deviceId,
            int? hours,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await devices.EvaluateLocationsAsync(deviceId, hours ?? 24, cancellationToken));
        });

        // ---- Mosquito / egg detection on an uploaded photo ----
        // POST starts (or returns the existing) run; the detector is CPU-heavy so the call returns at once
        // with Status=Running and the client polls GET until Completed/Failed. force=true re-runs.

        app.MapPost("/api/v2/captures/{id:guid}/analysis", async (
            HttpContext context,
            Guid id,
            bool? force,
            EggAnalysisService analysis,
            OperatorTokenService tokens,
            IOptions<AuthOptions> auth,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            if (!analysis.Enabled)
            {
                return Results.Problem("Photo analysis is disabled on this server.", statusCode: 501);
            }
            var response = await analysis.StartAsync(id, force ?? false, auth.Value.Username, cancellationToken);
            return response.Analysis?.Status == AnalysisStatus.Running
                ? Results.Accepted($"/api/v2/captures/{id:D}/analysis", response)
                : Results.Ok(response);
        });

        app.MapGet("/api/v2/captures/{id:guid}/analysis", async (
            HttpContext context,
            Guid id,
            EggAnalysisService analysis,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var response = await analysis.GetAsync(id, cancellationToken);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        app.MapGet("/api/v2/commands/{id:guid}", async (
            HttpContext context,
            Guid id,
            DeviceService devices,
            OperatorTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!IsOperator(context, tokens))
            {
                return Results.Unauthorized();
            }
            var command = await devices.GetCommandAsync(id, cancellationToken);
            return command is null ? Results.NotFound() : Results.Ok(command);
        });

        // Hourly report checks need a confirmed schedule; not part of the demo scope. The client treats 501 as pending.
        app.MapGet("/api/v2/report-checks", (HttpContext context, OperatorTokenService tokens) =>
            IsOperator(context, tokens)
                ? Results.Problem("Report checks are not implemented on this server.", statusCode: 501)
                : Results.Unauthorized());

        await app.Services.GetRequiredService<ICaptureRepository>()
            .InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<IDeviceRepository>()
            .InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<EggAnalysisService>()
            .RecoverAsync(app.Lifetime.ApplicationStopping);
        await app.RunAsync();
    }

    // Offset paging over an in-memory snapshot; cursor is the next offset, snapshot pins the as-of instant.
    private static PageResponse<T> Page<T>(IReadOnlyList<T> items, int? limit, string? cursor, string snapshot)
    {
        var size = Math.Clamp(limit ?? PageSize, 1, 500);
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            throw new CaptureValidationException("cursor is invalid.");
        }
        var page = items.Skip(offset).Take(size).ToArray();
        var next = offset + size < items.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return new PageResponse<T>(page, next, snapshot, true);
    }

    private static string NewSnapshot() =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset ParseSnapshot(string snapshot)
    {
        try
        {
            var padded = snapshot.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            var ticks = long.Parse(Encoding.ASCII.GetString(Convert.FromBase64String(padded)), CultureInfo.InvariantCulture);
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new CaptureValidationException("snapshot is invalid; query again.");
        }
    }

    private static bool IsCaptureWriter(
        HttpContext context,
        OperatorTokenService tokens,
        DeviceAuthOptions deviceAuth) =>
        IsOperator(context, tokens) ||
        (context.Request.Headers.TryGetValue("X-Device-Key", out var key) &&
         OperatorTokenService.FixedEquals(key.ToString(), deviceAuth.ApiKey));

    private static bool IsOperator(HttpContext context, OperatorTokenService tokens)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               tokens.Validate(authorization[prefix.Length..].Trim());
    }

    private static void ValidateProductionConfiguration(
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsProduction())
        {
            return;
        }
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new();
        var device = configuration.GetSection("DeviceAuth").Get<DeviceAuthOptions>() ?? new();
        var repository = configuration.GetSection("Repository").Get<RepositoryOptions>() ?? new();
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
        var errors = new List<string>();
        if (auth.Password == "change-me" || auth.SigningKey.Length < 32)
        {
            errors.Add("configure a non-default operator password and a signing key of at least 32 characters");
        }
        if (device.ApiKey.Length < 32 || device.ApiKey.Contains("change-me", StringComparison.Ordinal))
        {
            errors.Add("configure a unique device API key of at least 32 characters");
        }
        if (!repository.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(repository.ConnectionString))
        {
            errors.Add("use the Postgres repository with a connection string");
        }
        if (!storage.Provider.Equals("AliyunOss", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(storage.Bucket) ||
            string.IsNullOrWhiteSpace(storage.EcsRamRoleName))
        {
            errors.Add("use AliyunOss with a private bucket and ECS RAM role");
        }
        if (!Uri.TryCreate(storage.PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
            publicUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("set an HTTPS public base URL");
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Unsafe production configuration: {string.Join("; ", errors)}.");
        }
    }
}
