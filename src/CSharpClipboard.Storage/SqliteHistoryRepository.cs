using System.Security.Cryptography;
using System.Text;
using CSharpClipboard.Core;
using Microsoft.Data.Sqlite;

namespace CSharpClipboard.Storage;

public sealed record HistoryRepositoryOptions
{
    public int MaximumUnpinnedItems { get; init; } = 500;

    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class HistoryDatabaseVersionException(string message) : InvalidOperationException(message);

public sealed class HistoryStorageBusyException(string message, Exception innerException) : InvalidOperationException(message, innerException);

public sealed class SqliteHistoryRepository : IHistoryRepository, IHistoryMaintenance, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private HistoryRepositoryOptions _options;
    private readonly TimeProvider _clock;
    private bool _disposed;

    private SqliteHistoryRepository(SqliteConnection connection, HistoryRepositoryOptions options, TimeProvider clock)
    {
        _connection = connection;
        _options = options;
        _clock = clock;
    }

    public static async ValueTask<SqliteHistoryRepository> OpenAsync(
        string databasePath,
        HistoryRepositoryOptions? options = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        options ??= new HistoryRepositoryOptions();
        ValidateOptions(options);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await VerifyAndMigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return new SqliteHistoryRepository(connection, options, clock ?? TimeProvider.System);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask UpsertCaptureAsync(HistoryCapture capture, CancellationToken cancellationToken)
    {
        _ = await ExecuteMutationAsync(async transaction =>
        {
            var content = Encoding.UTF8.GetBytes(capture.Text);
            var digest = SHA256.HashData(content);
            var existingId = await FindExactEntryIdAsync(digest, content, transaction, cancellationToken).ConfigureAwait(false);
            var now = ToUnixSeconds(_clock.GetUtcNow());

            if (existingId is { } id)
            {
                await using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE history_entries
                    SET copied = $copied,
                        source = CASE WHEN $source IS NULL THEN source ELSE $source END
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$copied", now);
                update.Parameters.AddWithValue("$source", (object?)capture.Source?.Identifier ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO history_entries (id, content, digest, created, copied, source, pinned)
                    VALUES ($id, $content, $digest, $created, $copied, $source, NULL);
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insert.Parameters.Add("$content", SqliteType.Blob).Value = content;
                insert.Parameters.Add("$digest", SqliteType.Blob).Value = digest;
                insert.Parameters.AddWithValue("$created", now);
                insert.Parameters.AddWithValue("$copied", now);
                insert.Parameters.AddWithValue("$source", (object?)capture.Source?.Identifier ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ApplyRetentionAsync(transaction, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<HistoryEntry>> GetEntriesAsync(string? searchText, CancellationToken cancellationToken) =>
        ExecuteReadAsync(async () =>
        {
            var entries = new List<HistoryEntry>();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, content, created, copied, source, pinned
                FROM history_entries
                ORDER BY pinned IS NULL ASC, pinned DESC, copied DESC, id ASC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(1));
                if (searchText is not null && !text.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new HistoryEntry(
                    Guid.Parse(reader.GetString(0)),
                    text,
                    FromUnixSeconds(reader.GetDouble(2)),
                    FromUnixSeconds(reader.GetDouble(3)),
                    reader.IsDBNull(4) ? null : new ClipboardSource(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : FromUnixSeconds(reader.GetDouble(5))));
            }

            return (IReadOnlyList<HistoryEntry>)entries;
        }, cancellationToken);

    public ValueTask<bool> PinAsync(Guid id, CancellationToken cancellationToken) =>
        SetPinnedAsync(id, ToUnixSeconds(_clock.GetUtcNow()), applyRetention: false, cancellationToken);

    public ValueTask<bool> UnpinAsync(Guid id, CancellationToken cancellationToken) =>
        SetPinnedAsync(id, null, applyRetention: true, cancellationToken);

    public ValueTask<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async transaction =>
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history_entries WHERE lower(id) = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }, cancellationToken);

    public async ValueTask ClearRecentAsync(CancellationToken cancellationToken)
    {
        _ = await ExecuteMutationAsync(async transaction =>
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history_entries WHERE pinned IS NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAllAsync(CancellationToken cancellationToken)
    {
        _ = await ExecuteMutationAsync(async transaction =>
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history_entries;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRetentionAsync(int maximum, CancellationToken cancellationToken = default)
    {
        if (maximum is < 50 or > 2000) throw new ArgumentOutOfRangeException(nameof(maximum));
        _ = await ExecuteMutationAsync(async transaction =>
        {
            _options = _options with { MaximumUnpinnedItems = maximum };
            await ApplyRetentionAsync(transaction, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _connectionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private ValueTask<bool> SetPinnedAsync(Guid id, double? pinnedAt, bool applyRetention, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async transaction =>
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE history_entries SET pinned = $pinned WHERE lower(id) = $id;";
            command.Parameters.AddWithValue("$pinned", (object?)pinnedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            if (changed && applyRetention)
            {
                await ApplyRetentionAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }, cancellationToken);

    private async ValueTask<string?> FindExactEntryIdAsync(
        byte[] digest,
        byte[] content,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, content FROM history_entries WHERE digest = $digest;";
        command.Parameters.Add("$digest", SqliteType.Blob).Value = digest;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (content.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(1)))
            {
                return reader.GetString(0);
            }
        }

        return null;
    }

    private async ValueTask ApplyRetentionAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var unpinnedCount = 0;
        await using (var count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM history_entries WHERE pinned IS NULL;";
            unpinnedCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var expiredCount = unpinnedCount - _options.MaximumUnpinnedItems;
        if (expiredCount <= 0)
        {
            return;
        }

        var expiredIds = new List<string>();
        await using (var candidates = _connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText = """
                SELECT id
                FROM history_entries
                WHERE pinned IS NULL
                ORDER BY copied ASC, id ASC
                LIMIT $count;
                """;
            candidates.Parameters.AddWithValue("$count", expiredCount);
            await using var reader = await candidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expiredIds.Add(reader.GetString(0));
            }
        }

        foreach (var id in expiredIds)
        {
            await using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM history_entries WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<T> ExecuteMutationAsync<T>(Func<SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        try
        {
            ThrowIfDisposed();
            transaction = _connection.BeginTransaction(deferred: false);
            var result = await operation(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            throw new HistoryStorageBusyException("The history database is busy. Retry the operation.", exception);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            _connectionGate.Release();
        }
    }

    private async ValueTask<T> ExecuteReadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await operation().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            throw new HistoryStorageBusyException("The history database is busy. Retry the operation.", exception);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static async ValueTask VerifyAndMigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (version > SchemaVersion)
        {
            throw new HistoryDatabaseVersionException(
                $"History database schema version {version} is newer than supported version {SchemaVersion}.");
        }

        if (version == SchemaVersion)
        {
            return;
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS history_entries (
                    id TEXT PRIMARY KEY,
                    content BLOB NOT NULL,
                    digest BLOB NOT NULL,
                    created REAL NOT NULL,
                    copied REAL NOT NULL,
                    source TEXT NULL,
                    pinned REAL NULL
                );
                CREATE INDEX IF NOT EXISTS ix_history_entries_digest ON history_entries (digest);
                PRAGMA user_version = 1;
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        HistoryRepositoryOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds}; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA secure_delete = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptions(HistoryRepositoryOptions options)
    {
        if (options.MaximumUnpinnedItems is < 50 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumUnpinnedItems), "The unpinned history limit must be from 50 through 2000.");
        }

        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options.BusyTimeout), "The busy timeout must be greater than zero and at most one minute.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static double ToUnixSeconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds() / 1000d;

    private static DateTimeOffset FromUnixSeconds(double value) => DateTimeOffset.UnixEpoch.AddSeconds(value);
}
