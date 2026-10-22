using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using CSharpClipboard.Core;
using CSharpClipboard.Services;
using CSharpClipboard.Storage;

[assembly: AvaloniaTestApplication(typeof(CSharpClipboard.App.Tests.TestAppBuilder))]
namespace CSharpClipboard.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public sealed class TestApplication : Application { public override void Initialize() => Styles.Add(new FluentTheme()); }

public sealed class DesktopShellTests
{
    [AvaloniaFact]
    public Task Actual_shell_keyboard_copy_and_cleanup() => ExerciseActionAsync(true);
    [AvaloniaFact]
    public Task Actual_shell_keyboard_paste_and_cleanup() => ExerciseActionAsync(false);

    private static async Task ExerciseActionAsync(bool copy)
    {
        var path = Path.Combine(Path.GetTempPath(), "CSharpClipboard.ShellTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var settings = new JsonSettingsStore(Path.Combine(path, "settings.json"));
        await settings.SetAsync("preferences", new AppPreferences { Onboarded = true }, default);
        await using (var seed = await SqliteHistoryRepository.OpenAsync(Path.Combine(path, "history.sqlite")))
            await seed.UpsertCaptureAsync(new HistoryCapture("smoke-test-value\r\n", null), default);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var port = new TestPort(path);
        var shell = new DesktopShell<TestSnapshot>(port, port, port, port, lifetime);
        try
        {
            await shell.StartAsync();
            Assert.Equal(0, port.Reads);
            await shell.ToggleHistoryAsync();
            Assert.True(port.SnapshotBeforeShow);
            var panel = shell.HistoryWindow;
            Assert.NotNull(panel); Assert.True(panel.IsVisible);
            panel.KeyPressQwerty(PhysicalKey.Enter, copy ? RawInputModifiers.Control : RawInputModifiers.None);
            await Task.Delay(200);
            Assert.Equal("smoke-test-value\r\n", port.Written);
            Assert.False(panel.IsVisible); Assert.Equal(copy ? 0 : 1, port.Dispatches);
            await shell.ToggleHistoryAsync();
            await shell.CloseHistoryAsync(); Assert.Equal(1, port.Writes);
        }
        finally { await shell.DisposeAsync(); }
        Assert.True(port.Disposed);
        Assert.True(port.Released >= 2);
        Directory.Delete(path, true);
    }

    [AvaloniaFact]
    public async Task Corrupt_settings_preserve_file_and_shutdown_releases_platform_and_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), "CSharpClipboard.FailedStartup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); var settingsPath = Path.Combine(path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "corrupt");
        var port = new TestPort(path);
        var shell = new DesktopShell<TestSnapshot>(port, port, port, port, new ClassicDesktopStyleApplicationLifetime());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(shell.StartAsync);
        await shell.DisposeAsync(); await shell.DisposeAsync();
        Assert.True(port.Disposed); Assert.Equal("corrupt", await File.ReadAllTextAsync(settingsPath));
        using var instance = await SingleInstanceLease.AcquireAsync(path);
        Assert.NotNull(instance); instance.Dispose();
        Directory.Delete(path, true);
    }

    [AvaloniaFact]
    public async Task Native_status_item_actions_are_routed_through_the_cross_platform_shell()
    {
        var path = Path.Combine(Path.GetTempPath(), "CSharpClipboard.NativeStatusItem", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var settings = new JsonSettingsStore(Path.Combine(path, "settings.json"));
        await settings.SetAsync("preferences", new AppPreferences { Onboarded = true }, default);
        await using (var seed = await SqliteHistoryRepository.OpenAsync(Path.Combine(path, "history.sqlite")))
            await seed.UpsertCaptureAsync(new HistoryCapture("menu-item", null), default);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var port = new NativeStatusItemTestPort(path);
        var shell = new DesktopShell<TestSnapshot>(port, port, port, port, lifetime);
        try
        {
            await shell.StartAsync();
            Assert.True(port.StatusItemCreated);

            port.Invoke(StatusItemAction.OpenHistory);
            await Task.Delay(100);
            Assert.True(port.SnapshotBeforeShow);
            Assert.True(shell.HistoryWindow?.IsVisible);

            port.Invoke(StatusItemAction.ToggleCapture);
            await Task.Delay(100);
            Assert.Contains(true, port.CapturePausedUpdates);
            Assert.True((await settings.GetAsync<AppPreferences>("preferences", default))!.CapturePaused);

            port.Invoke(StatusItemAction.ClearRecentHistory);
            await Task.Delay(100);
            await using var verify = await SqliteHistoryRepository.OpenAsync(Path.Combine(path, "history.sqlite"));
            Assert.Empty(await verify.GetEntriesAsync(null, default));

            port.Invoke(StatusItemAction.Settings);
            port.Invoke(StatusItemAction.About);
            await Task.Delay(100);
            Assert.NotNull(shell.SettingsWindow);
            Assert.NotNull(shell.AboutWindow);

            var quitRequested = false;
            lifetime.ShutdownRequested += (_, eventArgs) => { quitRequested = true; eventArgs.Cancel = true; };
            port.Invoke(StatusItemAction.Quit);
            await Task.Delay(100);
            Assert.True(quitRequested);
        }
        finally
        {
            await shell.DisposeAsync();
            Directory.Delete(path, true);
        }
    }

    private sealed class TestSnapshot(Action release) : IPasteTargetSnapshot { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
    private class TestPort(string directory) : IClipboardPort, IClipboardWritePort, IPasteTargetPort<TestSnapshot>, IDesktopPlatformServices
    {
        public int Reads, Writes, Dispatches, Released;
        public bool Disposed, SnapshotBeforeShow;
        public string? Written;
        private int _revision;
        public event EventHandler<ClipboardSignal>? Changed;
        public event Action? HistoryRequested { add { } remove { } }
        public string DataDirectory => directory;
        public bool UsesCommandKey => false;
        public string PlatformName => "Test";
        public string ApplicationIdentifier => "test";
        public string PermissionDescription => "Test permissions";
        public string StartupDescription => "Test startup";
        public (int X, int Y) GetActivationPoint() => (10, 10);
        public bool IsValidApplicationIdentifier(string value) => true;
        public void OpenUrl(string url) { }
        public void RequestClipboardAccess() { }
        public void RequestPasteAccess() { }
        public void OpenClipboardSettings() { }
        public ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken token) => ValueTask.FromResult(new ClipboardRevision(_revision.ToString()));
        public ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision revision, CancellationToken token) { Reads++; return ValueTask.FromResult(new ClipboardReadResult(revision, 0, null, null, 0)); }
        public ValueTask<ClipboardRevision> WriteTextAsync(string text, CancellationToken token) { Written = text; Writes++; var revision = new ClipboardRevision((++_revision).ToString()); Changed?.Invoke(this, new(revision)); return ValueTask.FromResult(revision); }
        public ValueTask<TestSnapshot?> GetActiveTargetAsync(CancellationToken token) { SnapshotBeforeShow = true; return ValueTask.FromResult<TestSnapshot?>(new TestSnapshot(() => Released++)); }
        public ValueTask<bool> TryRestoreAsync(TestSnapshot snapshot, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<PasteDispatchResult> TryDispatchAsync(TestSnapshot snapshot, ClipboardRevision revision, CancellationToken token) { Dispatches++; return ValueTask.FromResult(PasteDispatchResult.ShortcutSent); }
        public ValueTask RegisterAsync(GlobalShortcut shortcut, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask UnregisterAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<bool> IsEnabledAsync(CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask SetEnabledAsync(bool enabled, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<PermissionStatus> GetClipboardAccessAsync(CancellationToken token) => ValueTask.FromResult(PermissionStatus.Allowed);
        public void Dispose() => Disposed = true;
    }

    private sealed class NativeStatusItemTestPort(string directory) : TestPort(directory), IStatusItemPlatform
    {
        private Action<StatusItemAction>? _action;
        public bool StatusItemCreated { get; private set; }
        public List<bool> CapturePausedUpdates { get; } = [];
        public ValueTask CreateStatusItemAsync(Action<StatusItemAction> onAction, bool capturePaused, CancellationToken token)
        {
            _action = onAction;
            StatusItemCreated = true;
            CapturePausedUpdates.Add(capturePaused);
            return ValueTask.CompletedTask;
        }
        public ValueTask SetStatusItemCapturePausedAsync(bool capturePaused, CancellationToken token)
        {
            CapturePausedUpdates.Add(capturePaused);
            return ValueTask.CompletedTask;
        }
        public ValueTask DiscardPendingStatusItemSnapshotAsync(CancellationToken token) => ValueTask.CompletedTask;
        public void Invoke(StatusItemAction action) => _action!.Invoke(action);
    }
}
