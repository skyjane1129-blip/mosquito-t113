using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mosquito.Cloud.Api;

public interface IDeviceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken);
    Task AddCommandAsync(DeviceCommand command, CancellationToken cancellationToken);
    Task<DeviceCommand?> GetCommandAsync(Guid id, CancellationToken cancellationToken);
    Task UpdateCommandAsync(DeviceCommand command, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceCommand>> ListCommandsAsync(string deviceId, int limit, CancellationToken cancellationToken);
    // Atomically hands the oldest pending, unexpired command to the device.
    Task<DeviceCommand?> DispatchNextCommandAsync(string deviceId, DateTimeOffset now, CancellationToken cancellationToken);
    Task AddLocationAsync(string deviceId, GeoSample location, CancellationToken cancellationToken);
    Task<IReadOnlyList<GeoSample>> ListLocationsAsync(string deviceId, int limit, CancellationToken cancellationToken);
}

public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DeviceCommand> _commands = new();
    private readonly ConcurrentDictionary<string, List<GeoSample>> _locations = new(StringComparer.Ordinal);
    private readonly object _dispatchLock = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(_devices.GetValueOrDefault(deviceId));

    public Task UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        _devices[device.DeviceId] = device;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceRecord>>(_devices.Values.OrderBy(d => d.DeviceId, StringComparer.Ordinal).ToArray());

    public Task AddCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        _commands[command.Id] = command;
        return Task.CompletedTask;
    }

    public Task<DeviceCommand?> GetCommandAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_commands.GetValueOrDefault(id));

    public Task UpdateCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        _commands[command.Id] = command;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceCommand>> ListCommandsAsync(string deviceId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceCommand>>(_commands.Values
            .Where(c => c.DeviceId == deviceId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray());

    public Task<DeviceCommand?> DispatchNextCommandAsync(string deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_dispatchLock)
        {
            foreach (var stale in _commands.Values.Where(c => c.Status == CommandStatus.Pending && c.ExpiresAtUtc <= now).ToArray())
            {
                _commands[stale.Id] = stale with { Status = CommandStatus.Expired, CompletedAtUtc = now };
            }
            var next = _commands.Values
                .Where(c => c.DeviceId == deviceId && c.Status == CommandStatus.Pending)
                .OrderBy(c => c.CreatedAtUtc)
                .FirstOrDefault();
            if (next is null)
            {
                return Task.FromResult<DeviceCommand?>(null);
            }
            var dispatched = next with { Status = CommandStatus.Dispatched, DispatchedAtUtc = now };
            _commands[next.Id] = dispatched;
            return Task.FromResult<DeviceCommand?>(dispatched);
        }
    }

    public Task AddLocationAsync(string deviceId, GeoSample location, CancellationToken cancellationToken)
    {
        var list = _locations.GetOrAdd(deviceId, _ => []);
        lock (list)
        {
            list.Add(location);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GeoSample>> ListLocationsAsync(string deviceId, int limit, CancellationToken cancellationToken)
    {
        if (!_locations.TryGetValue(deviceId, out var list))
        {
            return Task.FromResult<IReadOnlyList<GeoSample>>([]);
        }
        lock (list)
        {
            return Task.FromResult<IReadOnlyList<GeoSample>>(list
                .OrderByDescending(l => l.SampledAtUtc ?? DateTimeOffset.MinValue)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray());
        }
    }
}

public sealed class SqliteDeviceRepository(SqliteDatabase database) : IDeviceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                updated_at_utc TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS commands (
                id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_commands_device_status ON commands (device_id, status, created_at_utc);
            CREATE TABLE IF NOT EXISTS locations (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT NOT NULL,
                sampled_at_utc TEXT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_locations_device ON locations (device_id, seq DESC);
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM devices WHERE device_id = $id";
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize<DeviceRecord>(json, JsonOptions)
            : null;
    }

    public async Task UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO devices (device_id, updated_at_utc, payload) VALUES ($id, $updated, $payload)
            ON CONFLICT(device_id) DO UPDATE SET updated_at_utc = excluded.updated_at_utc, payload = excluded.payload
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$updated", device.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(device, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM devices ORDER BY device_id";
        return await ReadAllAsync<DeviceRecord>(command, cancellationToken);
    }

    public async Task AddCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO commands (id, device_id, status, created_at_utc, expires_at_utc, payload)
            VALUES ($id, $device, $status, $created, $expires, $payload)
            """;
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = sql;
        AddCommandParameters(insert, command);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceCommand?> GetCommandAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM commands WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize<DeviceCommand>(json, JsonOptions)
            : null;
    }

    public async Task UpdateCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE commands SET status = $status, expires_at_utc = $expires, payload = $payload WHERE id = $id";
        AddCommandParameters(update, command);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Command {command.Id} does not exist.");
        }
    }

    public async Task<IReadOnlyList<DeviceCommand>> ListCommandsAsync(string deviceId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM commands WHERE device_id = $device ORDER BY created_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        return await ReadAllAsync<DeviceCommand>(command, cancellationToken);
    }

    public async Task<DeviceCommand?> DispatchNextCommandAsync(string deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        // The write transaction serializes concurrent device polls so one command is never dispatched twice.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var nowText = now.ToString("O");

        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = "SELECT payload FROM commands WHERE status = 'Pending' AND expires_at_utc <= $now";
            expire.Parameters.AddWithValue("$now", nowText);
            foreach (var stale in await ReadAllAsync<DeviceCommand>(expire, cancellationToken))
            {
                await using var mark = connection.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE commands SET status = $status, payload = $payload WHERE id = $id";
                var expired = stale with { Status = CommandStatus.Expired, CompletedAtUtc = now };
                mark.Parameters.AddWithValue("$status", expired.Status.ToString());
                mark.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(expired, JsonOptions));
                mark.Parameters.AddWithValue("$id", expired.Id.ToString("D"));
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        DeviceCommand? next;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT payload FROM commands WHERE device_id = $device AND status = 'Pending' ORDER BY created_at_utc LIMIT 1";
            select.Parameters.AddWithValue("$device", deviceId);
            next = await select.ExecuteScalarAsync(cancellationToken) is string json
                ? JsonSerializer.Deserialize<DeviceCommand>(json, JsonOptions)
                : null;
        }
        if (next is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var dispatched = next with { Status = CommandStatus.Dispatched, DispatchedAtUtc = now };
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE commands SET status = $status, expires_at_utc = $expires, payload = $payload WHERE id = $id";
            AddCommandParameters(update, dispatched);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return dispatched;
    }

    public async Task AddLocationAsync(string deviceId, GeoSample location, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO locations (device_id, sampled_at_utc, payload) VALUES ($device, $sampled, $payload)";
        insert.Parameters.AddWithValue("$device", deviceId);
        insert.Parameters.AddWithValue("$sampled", (object?)location.SampledAtUtc?.ToString("O") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(location, JsonOptions));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GeoSample>> ListLocationsAsync(string deviceId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = database.Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM locations WHERE device_id = $device ORDER BY seq DESC LIMIT $limit";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAllAsync<GeoSample>(command, cancellationToken);
    }

    private static void AddCommandParameters(SqliteCommand statement, DeviceCommand command)
    {
        statement.Parameters.AddWithValue("$id", command.Id.ToString("D"));
        statement.Parameters.AddWithValue("$device", command.DeviceId);
        statement.Parameters.AddWithValue("$status", command.Status.ToString());
        statement.Parameters.AddWithValue("$created", command.CreatedAtUtc.ToString("O"));
        statement.Parameters.AddWithValue("$expires", command.ExpiresAtUtc.ToString("O"));
        statement.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(command, JsonOptions));
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions);
            if (item is not null)
            {
                result.Add(item);
            }
        }
        return result;
    }
}

// Shared SQLite connection factory; the database file lives under the content root by default.
public sealed class SqliteDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;
    public SqliteConnection Open() => new(ConnectionString);

    public static SqliteDatabase FromOptions(string? connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(connectionString) ? "Data Source=data/mosquito-cloud.db" : connectionString);
        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, builder.DataSource));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        return new SqliteDatabase(builder.ToString());
    }
}
