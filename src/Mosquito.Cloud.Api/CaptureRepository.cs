using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Mosquito.Cloud.Api;

public interface ICaptureRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<CaptureRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> AddAsync(CaptureRecord capture, CancellationToken cancellationToken);
    Task UpdateAsync(CaptureRecord capture, CancellationToken cancellationToken);
    Task<IReadOnlyList<CaptureRecord>> ListAsync(
        string? deviceSerial,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken);
    // Unbounded, stably ordered query used by the paged v2 endpoints.
    Task<IReadOnlyList<CaptureRecord>> QueryAsync(
        string? deviceSerial,
        string? deviceId,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        CancellationToken cancellationToken);
}

public sealed class InMemoryCaptureRepository : ICaptureRepository
{
    private readonly ConcurrentDictionary<Guid, CaptureRecord> _captures = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<CaptureRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_captures.GetValueOrDefault(id));

    public Task<bool> AddAsync(CaptureRecord capture, CancellationToken cancellationToken) =>
        Task.FromResult(_captures.TryAdd(capture.Id, capture));

    public Task UpdateAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        _captures[capture.Id] = capture;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CaptureRecord>> ListAsync(
        string? deviceSerial,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        var values = _captures.Values
            .Where(c => deviceSerial is null || c.DeviceSerial == deviceSerial)
            .Where(c => status is null || c.Status == status)
            .Where(c => from is null || c.CapturedAtUtc >= from)
            .Where(c => to is null || c.CapturedAtUtc <= to)
            .OrderByDescending(c => c.CapturedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();
        return Task.FromResult<IReadOnlyList<CaptureRecord>>(values);
    }

    public Task<IReadOnlyList<CaptureRecord>> QueryAsync(
        string? deviceSerial,
        string? deviceId,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        CancellationToken cancellationToken)
    {
        IEnumerable<CaptureRecord> values = _captures.Values
            .Where(c => deviceSerial is null || c.DeviceSerial == deviceSerial)
            .Where(c => deviceId is null || c.DeviceId == deviceId)
            .Where(c => status is null || c.Status == status)
            .Where(c => from is null || c.CapturedAtUtc >= from)
            .Where(c => to is null || c.CapturedAtUtc <= to)
            .OrderByDescending(c => c.CapturedAtUtc)
            .ThenBy(c => c.Id);
        if (limit is not null)
        {
            values = values.Take(Math.Max(1, limit.Value));
        }
        return Task.FromResult<IReadOnlyList<CaptureRecord>>(values.ToArray());
    }
}

public sealed class PostgresCaptureRepository(IOptions<RepositoryOptions> options) : ICaptureRepository
{
    private readonly string _connectionString = options.Value.ConnectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS captures (
                id uuid PRIMARY KEY,
                device_serial text NOT NULL,
                status text NOT NULL,
                captured_at_utc timestamptz NOT NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL,
                payload jsonb NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_captures_device_time
                ON captures (device_serial, captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_captures_status_time
                ON captures (status, captured_at_utc DESC);
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CaptureRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT payload FROM captures WHERE id = @id";
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? JsonSerializer.Deserialize<CaptureRecord>(json, JsonOptions) : null;
    }

    public async Task<bool> AddAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO captures
                (id, device_serial, status, captured_at_utc, created_at_utc, updated_at_utc, payload)
            VALUES
                (@id, @device, @status, @captured, @created, @updated, @payload)
            ON CONFLICT (id) DO NOTHING
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, capture);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE captures SET
                device_serial = @device,
                status = @status,
                captured_at_utc = @captured,
                updated_at_utc = @updated,
                payload = @payload
            WHERE id = @id
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, capture);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Capture {capture.Id} no longer exists.");
        }
    }

    public async Task<IReadOnlyList<CaptureRecord>> ListAsync(
        string? deviceSerial,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload FROM captures
            WHERE (@device IS NULL OR device_serial = @device)
              AND (@status IS NULL OR status = @status)
              AND (@from_time IS NULL OR captured_at_utc >= @from_time)
              AND (@to_time IS NULL OR captured_at_utc <= @to_time)
            ORDER BY captured_at_utc DESC
            LIMIT @limit
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("device", NpgsqlDbType.Text, (object?)deviceSerial ?? DBNull.Value);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status is null ? DBNull.Value : status.Value.ToString());
        command.Parameters.AddWithValue("from_time", NpgsqlDbType.TimestampTz, (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("to_time", NpgsqlDbType.TimestampTz, (object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var result = new List<CaptureRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var capture = JsonSerializer.Deserialize<CaptureRecord>(reader.GetString(0), JsonOptions);
            if (capture is not null)
            {
                result.Add(capture);
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<CaptureRecord>> QueryAsync(
        string? deviceSerial,
        string? deviceId,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT payload FROM captures
            WHERE (@device IS NULL OR device_serial = @device)
              AND (@device_id IS NULL OR payload->>'deviceId' = @device_id)
              AND (@status IS NULL OR status = @status)
              AND (@from_time IS NULL OR captured_at_utc >= @from_time)
              AND (@to_time IS NULL OR captured_at_utc <= @to_time)
            ORDER BY captured_at_utc DESC, id ASC
            """;
        if (limit is not null)
        {
            sql += " LIMIT @limit";
        }
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("device", NpgsqlDbType.Text, (object?)deviceSerial ?? DBNull.Value);
        command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status is null ? DBNull.Value : status.Value.ToString());
        command.Parameters.AddWithValue("from_time", NpgsqlDbType.TimestampTz, (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("to_time", NpgsqlDbType.TimestampTz, (object?)to ?? DBNull.Value);
        if (limit is not null)
        {
            command.Parameters.AddWithValue("limit", Math.Max(1, limit.Value));
        }
        var result = new List<CaptureRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var capture = JsonSerializer.Deserialize<CaptureRecord>(reader.GetString(0), JsonOptions);
            if (capture is not null)
            {
                result.Add(capture);
            }
        }
        return result;
    }

    private static void AddParameters(NpgsqlCommand command, CaptureRecord capture)
    {
        command.Parameters.AddWithValue("id", capture.Id);
        command.Parameters.AddWithValue("device", capture.DeviceSerial);
        command.Parameters.AddWithValue("status", capture.Status.ToString());
        command.Parameters.AddWithValue("captured", capture.CapturedAtUtc);
        command.Parameters.AddWithValue("created", capture.CreatedAtUtc);
        command.Parameters.AddWithValue("updated", capture.UpdatedAtUtc);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(capture, JsonOptions));
    }
}
