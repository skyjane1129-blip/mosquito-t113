using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mosquito.Cloud.Api;

public sealed class SqliteCaptureRepository(SqliteDatabase database) : ICaptureRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS captures (
                id TEXT PRIMARY KEY,
                device_serial TEXT NOT NULL,
                device_id TEXT NULL,
                status TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_captures_device_time ON captures (device_serial, captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_captures_device_id_time ON captures (device_id, captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_captures_status_time ON captures (status, captured_at_utc DESC);
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CaptureRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM captures WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize<CaptureRecord>(json, JsonOptions)
            : null;
    }

    public async Task<bool> AddAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT OR IGNORE INTO captures
                (id, device_serial, device_id, status, captured_at_utc, created_at_utc, updated_at_utc, payload)
            VALUES
                ($id, $serial, $device, $status, $captured, $created, $updated, $payload)
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, capture);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateAsync(CaptureRecord capture, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE captures SET
                device_serial = $serial,
                device_id = $device,
                status = $status,
                captured_at_utc = $captured,
                updated_at_utc = $updated,
                payload = $payload
            WHERE id = $id
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, capture);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Capture {capture.Id} no longer exists.");
        }
    }

    public Task<IReadOnlyList<CaptureRecord>> ListAsync(
        string? deviceSerial,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(deviceSerial, null, status, from, to, Math.Clamp(limit, 1, 500), cancellationToken);

    public async Task<IReadOnlyList<CaptureRecord>> QueryAsync(
        string? deviceSerial,
        string? deviceId,
        CaptureStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        CancellationToken cancellationToken)
    {
        // Timestamps are stored as UTC ISO-8601 "O" strings, so text comparison sorts chronologically.
        var sql = """
            SELECT payload FROM captures
            WHERE ($serial IS NULL OR device_serial = $serial)
              AND ($device IS NULL OR device_id = $device)
              AND ($status IS NULL OR status = $status)
              AND ($from IS NULL OR captured_at_utc >= $from)
              AND ($to IS NULL OR captured_at_utc <= $to)
            ORDER BY captured_at_utc DESC, id ASC
            """;
        if (limit is not null)
        {
            sql += " LIMIT $limit";
        }
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$serial", (object?)deviceSerial ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status.Value.ToString());
        command.Parameters.AddWithValue("$from", (object?)from?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        if (limit is not null)
        {
            command.Parameters.AddWithValue("$limit", limit.Value);
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

    private static void AddParameters(SqliteCommand command, CaptureRecord capture)
    {
        command.Parameters.AddWithValue("$id", capture.Id.ToString("D"));
        command.Parameters.AddWithValue("$serial", capture.DeviceSerial);
        command.Parameters.AddWithValue("$device", (object?)capture.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", capture.Status.ToString());
        command.Parameters.AddWithValue("$captured", capture.CapturedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$created", capture.CreatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", capture.UpdatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(capture, JsonOptions));
    }
}
