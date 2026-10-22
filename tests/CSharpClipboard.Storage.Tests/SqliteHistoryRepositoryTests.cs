using CSharpClipboard.Core;
using CSharpClipboard.Storage;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CSharpClipboard.Storage.Tests;

public sealed class SqliteHistoryRepositoryTests
{
    [Fact]
    public async Task Upsert_preserves_created_and_pin_but_updates_copy_time_and_known_source()
    {
        await using var database = new TemporaryDatabase();
        var clock = new TestClock();
        await using var repository = await OpenAsync(database.Path, clock);

        await repository.UpsertCaptureAsync(new HistoryCapture("same", new ClipboardSource("first")), CancellationToken.None);
        var original = Assert.Single(await repository.GetEntriesAsync(null, CancellationToken.None));
        Assert.True(await repository.PinAsync(original.Id, CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(2));
        await repository.UpsertCaptureAsync(new HistoryCapture("same", null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        await repository.UpsertCaptureAsync(new HistoryCapture("same", new ClipboardSource("second")), CancellationToken.None);

        var updated = Assert.Single(await repository.GetEntriesAsync(null, CancellationToken.None));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.Equal("second", updated.Source?.Identifier);
        Assert.NotNull(updated.PinnedAt);
        Assert.True(updated.CopiedAt > original.CopiedAt);
    }

    [Fact]
    public async Task Exact_unicode_line_ending_nul_and_whitespace_variants_are_distinct_utf8_blobs()
    {
        await using var database = new TemporaryDatabase();
        await using var repository = await OpenAsync(database.Path, new TestClock());
        var variants = new[] { "e\u0301", "é", "line\n", "line\r\n", "text\0tail", "text ", "text" };

        foreach (var text in variants)
        {
            await repository.UpsertCaptureAsync(new HistoryCapture(text, null), CancellationToken.None);
        }

        var entries = await repository.GetEntriesAsync(null, CancellationToken.None);
        Assert.Equal(variants.Length, entries.Count);
        Assert.All(variants, text => Assert.Contains(entries, entry => entry.Text == text));

        await using var connection = OpenRaw(database.Path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(content), length(digest) FROM history_entries LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("blob", reader.GetString(0));
        Assert.Equal(32, reader.GetInt32(1));
    }

    [Fact]
    public async Task Digest_match_is_only_a_candidate_and_full_blob_comparison_keeps_distinct_text()
    {
        await using var database = new TemporaryDatabase();
        await using var repository = await OpenAsync(database.Path, new TestClock());
        var target = "target";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(target));

        await using (var connection = OpenRaw(database.Path))
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO history_entries (id, content, digest, created, copied, source, pinned)
                VALUES ($id, $content, $digest, 0, 0, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.Add("$content", SqliteType.Blob).Value = Encoding.UTF8.GetBytes("different");
            insert.Parameters.Add("$digest", SqliteType.Blob).Value = digest;
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await repository.UpsertCaptureAsync(new HistoryCapture(target, null), CancellationToken.None);
        var entries = await repository.GetEntriesAsync(null, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Text == "different");
        Assert.Contains(entries, entry => entry.Text == target);
    }

    [Fact]
    public async Task Retention_keeps_pins_and_unpin_applies_the_limit()
    {
        await using var database = new TemporaryDatabase();
        var clock = new TestClock();
        await using var repository = await SqliteHistoryRepository.OpenAsync(
            database.Path,
            new HistoryRepositoryOptions { MaximumUnpinnedItems = 50 },
            clock,
            CancellationToken.None);

        for (var index = 0; index < 50; index++)
        {
            await repository.UpsertCaptureAsync(new HistoryCapture($"entry-{index}", null), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var oldest = (await repository.GetEntriesAsync(null, CancellationToken.None)).Single(entry => entry.Text == "entry-0");
        Assert.True(await repository.PinAsync(oldest.Id, CancellationToken.None));
        await repository.UpsertCaptureAsync(new HistoryCapture("entry-50", null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await repository.UpsertCaptureAsync(new HistoryCapture("entry-51", null), CancellationToken.None);

        var afterRetention = await repository.GetEntriesAsync(null, CancellationToken.None);
        Assert.Equal(51, afterRetention.Count);
        Assert.Contains(afterRetention, entry => entry.Id == oldest.Id);
        Assert.DoesNotContain(afterRetention, entry => entry.Text == "entry-1");

        Assert.True(await repository.UnpinAsync(oldest.Id, CancellationToken.None));
        var afterUnpin = await repository.GetEntriesAsync(null, CancellationToken.None);
        Assert.Equal(50, afterUnpin.Count);
        Assert.DoesNotContain(afterUnpin, entry => entry.Id == oldest.Id);
    }

    [Fact]
    public async Task Pinned_and_recent_entries_have_stable_order_and_search_does_not_reorder_them()
    {
        await using var database = new TemporaryDatabase();
        var clock = new TestClock();
        await using var repository = await OpenAsync(database.Path, clock);

        await repository.UpsertCaptureAsync(new HistoryCapture("привет мир", null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await repository.UpsertCaptureAsync(new HistoryCapture("éclair", null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await repository.UpsertCaptureAsync(new HistoryCapture("other", null), CancellationToken.None);
        var eclair = (await repository.GetEntriesAsync(null, CancellationToken.None)).Single(entry => entry.Text == "éclair");
        Assert.True(await repository.PinAsync(eclair.Id, CancellationToken.None));

        var all = await repository.GetEntriesAsync(null, CancellationToken.None);
        Assert.Equal(new[] { "éclair", "other", "привет мир" }, all.Select(entry => entry.Text));

        var accented = await repository.GetEntriesAsync("ÉCL", CancellationToken.None);
        var cyrillic = await repository.GetEntriesAsync("ПРИВЕТ", CancellationToken.None);
        Assert.Equal(new[] { eclair.Id }, accented.Select(entry => entry.Id));
        Assert.Equal(new[] { all[2].Id }, cyrillic.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Delete_clear_recent_clear_all_and_restart_preserve_the_expected_entries()
    {
        await using var database = new TemporaryDatabase();
        Guid pinnedId;
        await using (var repository = await OpenAsync(database.Path, new TestClock()))
        {
            await repository.UpsertCaptureAsync(new HistoryCapture("pinned", null), CancellationToken.None);
            await repository.UpsertCaptureAsync(new HistoryCapture("recent", null), CancellationToken.None);
            var pinned = (await repository.GetEntriesAsync(null, CancellationToken.None)).Single(entry => entry.Text == "pinned");
            pinnedId = pinned.Id;
            Assert.True(await repository.PinAsync(pinnedId, CancellationToken.None));
            var recent = (await repository.GetEntriesAsync(null, CancellationToken.None)).Single(entry => entry.Text == "recent");
            Assert.True(await repository.DeleteAsync(recent.Id, CancellationToken.None));
            Assert.False(await repository.DeleteAsync(recent.Id, CancellationToken.None));
            await repository.UpsertCaptureAsync(new HistoryCapture("another recent", null), CancellationToken.None);
            await repository.ClearRecentAsync(CancellationToken.None);
        }

        await using (var restarted = await OpenAsync(database.Path, new TestClock()))
        {
            var persisted = Assert.Single(await restarted.GetEntriesAsync(null, CancellationToken.None));
            Assert.Equal(pinnedId, persisted.Id);
            Assert.Equal("pinned", persisted.Text);
            await restarted.ClearAllAsync(CancellationToken.None);
            Assert.Empty(await restarted.GetEntriesAsync(null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Failed_mutation_rolls_back_without_changing_existing_entry()
    {
        await using var database = new TemporaryDatabase();
        var clock = new TestClock();
        await using var repository = await OpenAsync(database.Path, clock);
        await repository.UpsertCaptureAsync(new HistoryCapture("entry", new ClipboardSource("original")), CancellationToken.None);
        var original = Assert.Single(await repository.GetEntriesAsync(null, CancellationToken.None));

        await using (var connection = OpenRaw(database.Path))
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_history_update BEFORE UPDATE ON history_entries BEGIN SELECT RAISE(ABORT, 'test rollback'); END;";
            await trigger.ExecuteNonQueryAsync(CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await repository.UpsertCaptureAsync(new HistoryCapture("entry", new ClipboardSource("changed")), CancellationToken.None));

        var afterFailure = Assert.Single(await repository.GetEntriesAsync(null, CancellationToken.None));
        Assert.Equal(original.CopiedAt, afterFailure.CopiedAt);
        Assert.Equal("original", afterFailure.Source?.Identifier);
    }

    [Fact]
    public async Task Busy_writer_returns_a_clear_retryable_storage_error()
    {
        await using var database = new TemporaryDatabase();
        await using var repository = await SqliteHistoryRepository.OpenAsync(
            database.Path,
            new HistoryRepositoryOptions { BusyTimeout = TimeSpan.FromMilliseconds(50) },
            new TestClock(),
            CancellationToken.None);
        await using var lockConnection = OpenRaw(database.Path);
        await using var begin = lockConnection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync(CancellationToken.None);
        try
        {
            var exception = await Assert.ThrowsAsync<HistoryStorageBusyException>(async () =>
                await repository.UpsertCaptureAsync(new HistoryCapture("blocked", null), CancellationToken.None));
            Assert.Contains("Retry", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var rollback = lockConnection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Migrates_v0_and_refuses_an_unknown_future_version_without_changing_it()
    {
        await using var migratedDatabase = new TemporaryDatabase();
        await using (var connection = OpenRaw(migratedDatabase.Path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 0;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var repository = await OpenAsync(migratedDatabase.Path, new TestClock()))
        {
            await repository.UpsertCaptureAsync(new HistoryCapture("migrated", null), CancellationToken.None);
        }

        await using (var connection = OpenRaw(migratedDatabase.Path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

        await using var futureDatabase = new TemporaryDatabase();
        await using (var connection = OpenRaw(futureDatabase.Path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<HistoryDatabaseVersionException>(async () =>
            await SqliteHistoryRepository.OpenAsync(futureDatabase.Path, cancellationToken: CancellationToken.None));
        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var futureConnection = OpenRaw(futureDatabase.Path);
        await using var futureVersion = futureConnection.CreateCommand();
        futureVersion.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, (long)(await futureVersion.ExecuteScalarAsync(CancellationToken.None))!);
    }

    private static ValueTask<SqliteHistoryRepository> OpenAsync(string databasePath, TimeProvider clock) =>
        SqliteHistoryRepository.OpenAsync(databasePath, clock: clock, cancellationToken: CancellationToken.None);

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", null, global::System.Globalization.DateTimeStyles.RoundtripKind);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now = _now.Add(interval);
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _directory = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "CSharpClipboard.Storage.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Path => System.IO.Path.Combine(_directory, "history.sqlite");

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
