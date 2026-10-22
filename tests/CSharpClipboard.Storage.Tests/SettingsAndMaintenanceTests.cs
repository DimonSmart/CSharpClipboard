using CSharpClipboard.Core;
using CSharpClipboard.Storage;

namespace CSharpClipboard.Storage.Tests;

public sealed class SettingsAndMaintenanceTests
{
    [Fact]
    public async Task Atomic_settings_preserve_other_keys_and_refuse_corrupt_data_without_overwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CSharpClipboard.SettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            await store.SetAsync("preferences", new AppPreferences { Retention = 150, Shortcut = new("H", ShortcutModifiers.Control | ShortcutModifiers.Shift) }, default);
            await store.SetAsync("other", 42, default);
            var restarted = new JsonSettingsStore(path);
            Assert.Equal(150, (await restarted.GetAsync<AppPreferences>("preferences", default))!.Retention);
            Assert.Equal(42, await restarted.GetAsync<int>("other", default));
            await File.WriteAllTextAsync(path, "corrupt");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => restarted.SetAsync("other", 99, default).AsTask());
            Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Retention_changes_apply_immediately_and_clear_maintenance_reclaims_storage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CSharpClipboard.MaintenanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "history.sqlite");
        try
        {
            await using var repository = await SqliteHistoryRepository.OpenAsync(path);
            for (var index = 0; index < 65; index++) await repository.UpsertCaptureAsync(new HistoryCapture(new string('x', 5000) + index, null), default);
            var entries = await repository.GetEntriesAsync(null, default);
            await repository.PinAsync(entries[0].Id, default);
            await repository.SetRetentionAsync(50);
            Assert.Equal(51, (await repository.GetEntriesAsync(null, default)).Count);
            await repository.ClearRecentAsync(default); await repository.MaintainAsync();
            Assert.Single(await repository.GetEntriesAsync(null, default));
            Assert.True(new FileInfo(path).Length < 100_000);
        }
        finally { Directory.Delete(directory, true); }
    }
}
