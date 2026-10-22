using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CSharpClipboard.Core;
using CSharpClipboard.Services;

namespace CSharpClipboard.Platform.Windows.Tests;

public sealed class InteractiveWindowsFactAttribute : FactAttribute
{
    public InteractiveWindowsFactAttribute(bool requiresForeground = false)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CSHARPCLIPBOARD_DESKTOP_TESTS") != "1")
            Skip = "Requires an interactive Windows desktop; set CSHARPCLIPBOARD_DESKTOP_TESTS=1. Uses a harmless test field and restores supported clipboard formats.";
        else if (requiresForeground && Environment.GetEnvironmentVariable("CSHARPCLIPBOARD_FOCUS_TESTS") != "1")
            Skip = "Requires permission to foreground the test window. Set CSHARPCLIPBOARD_FOCUS_TESTS=1 in an interactive terminal; background host foreground lock cannot establish this evidence.";
    }
}

[CollectionDefinition("Desktop", DisableParallelization = true)] public sealed class DesktopCollection;
[Collection("Desktop")]
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopIntegrationTests
{
    [InteractiveWindowsFact]
    public async Task Native_writer_receipt_roundtrips_exact_text_and_is_generated()
    {
        using var port = new WindowsClipboardPort();
        using var backup = new ClipboardBackup(port);
        const string value = "test\r\n\0🙂e\u0301  ";
        var revision = await port.WriteTextAsync(value, default);
        backup.Expected = revision;
        var result = await port.ReadAsync(revision, default);
        Assert.True(value == result.PlainText, $"Exact synthetic value failed: flags={result.ContentFlags}; length={result.PlainText?.Length}.");
        Assert.Equal(revision, result.Revision);
        Assert.True(result.ContentFlags.HasFlag(ClipboardContentFlags.Generated));
        Assert.False(ClipboardCaptureEligibility.TryCreate(result, out _));
    }

    [InteractiveWindowsFact(true)]
    public async Task Native_focus_identity_allows_one_ctrl_v_and_refuses_changed_focus_and_revision()
    {
        using var port = new WindowsClipboardPort();
        using var backup = new ClipboardBackup(port);
        IntPtr window = IntPtr.Zero, edit = IntPtr.Zero, other = IntPtr.Zero, password = IntPtr.Zero;
        try
        {
            await port.OnNativeThreadAsync(() =>
            {
                window = Native.CreateWindowEx(0, "STATIC", "CSharpClipboard integration test", 0x10CF0000, 80, 80, 420, 260, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                edit = Native.CreateWindowEx(0, "EDIT", "", 0x50810004, 10, 10, 380, 80, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                other = Native.CreateWindowEx(0, "EDIT", "", 0x50810004, 10, 110, 380, 80, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                password = Native.CreateWindowEx(0, "EDIT", "", 0x50810020, 10, 200, 380, 20, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, edit);
                var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                var currentThread = Native.GetCurrentThreadId();
                var attached = foregroundThread != currentThread && Native.AttachThreadInput(currentThread, foregroundThread, true);
                try { Native.SetForegroundWindow(window); Native.SetFocus(edit); }
                finally { if (attached) Native.AttachThreadInput(currentThread, foregroundThread, false); }
            });
            await Task.Delay(200);
            await port.OnNativeThreadAsync(() => { Native.SetFocus(edit); Assert.Equal(edit, Native.GetFocus()); });
            await Task.Delay(100);
            Assert.Equal(window, Native.GetForegroundWindow());
            var targets = new WindowsPasteTargetPort(port, -1); // This test's harmless edit control is intentionally in-process.
            await using var snapshot = await targets.GetActiveTargetAsync(default);
            Assert.True(snapshot is not null, targets.LastRefusal);
            Assert.NotNull(snapshot);
            var revision = await port.WriteTextAsync("CSharpClipboard test", default); backup.Expected = revision;
            Assert.True(await targets.TryRestoreAsync(snapshot, default));
            Assert.Equal(PasteDispatchResult.ShortcutSent, await targets.TryDispatchAsync(snapshot, revision, default));
            await Task.Delay(150);
            var text = new System.Text.StringBuilder(100);
            await port.OnNativeThreadAsync(() => Native.GetWindowText(edit, text, text.Capacity));
            Assert.True(text.ToString() == "CSharpClipboard test", "Ctrl+V did not reach the original harmless field.");
            var previousLayout = IntPtr.Zero;
            await port.OnNativeThreadAsync(() => previousLayout = Native.GetKeyboardLayout(0));
            try
            {
                foreach (var layoutId in new[] { "00000409", "00000419", "00010409" })
                {
                    await port.OnNativeThreadAsync(() =>
                    {
                        var layout = Native.LoadKeyboardLayout(layoutId, 0x80);
                        Assert.NotEqual(IntPtr.Zero, layout);
                        Native.ActivateKeyboardLayout(layout, 0);
                        Native.SetWindowText(edit, ""); Native.SetFocus(edit);
                    });
                    Assert.Equal(PasteDispatchResult.ShortcutSent, await targets.TryDispatchAsync(snapshot, revision, default));
                    await Task.Delay(100);
                    await port.OnNativeThreadAsync(() => Native.GetWindowText(edit, text, text.Capacity));
                    Assert.True(text.ToString() == "CSharpClipboard test", "Test paste did not reach the field for layout " + layoutId);
                }
            }
            finally { await port.OnNativeThreadAsync(() => Native.ActivateKeyboardLayout(previousLayout, 0)); }
            await port.OnNativeThreadAsync(() => Native.SetFocus(other));
            Assert.False(await targets.TryRestoreAsync(snapshot, default));
            Assert.Equal(PasteDispatchResult.TargetUnavailable, await targets.TryDispatchAsync(snapshot, revision, default));
            var next = await port.WriteTextAsync("next test revision", default); backup.Expected = next;
            Assert.Equal(PasteDispatchResult.ClipboardChanged, await targets.TryDispatchAsync(snapshot, revision, default));
            await port.OnNativeThreadAsync(() => Native.SetFocus(password));
            Assert.Null(await targets.GetActiveTargetAsync(default));
        }
        finally { if (window != IntPtr.Zero) await port.OnNativeThreadAsync(() => Native.DestroyWindow(window)); }
    }

    [InteractiveWindowsFact]
    public async Task Shortcut_conflict_keeps_the_previous_registration_and_unregister_releases_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CSharpClipboard.ShortcutTest", Guid.NewGuid().ToString("N"));
        using var port = new WindowsClipboardPort(); using var competitor = new WindowsClipboardPort();
        using var services = new WindowsDesktopServices(port, directory);
        try
        {
            await services.RegisterAsync(new("H", ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift), default);
            await competitor.OnNativeThreadAsync(() => Assert.True(Native.RegisterHotKey(competitor.NativeWindow, 99, 0x4007, 'J')));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => services.RegisterAsync(new("J", ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift), default).AsTask());
            await competitor.OnNativeThreadAsync(() => Assert.False(Native.RegisterHotKey(competitor.NativeWindow, 98, 0x4007, 'H')));
            await services.UnregisterAsync(default);
            await competitor.OnNativeThreadAsync(() => { Assert.True(Native.RegisterHotKey(competitor.NativeWindow, 98, 0x4007, 'H')); Native.UnregisterHotKey(competitor.NativeWindow, 98); });
            await Assert.ThrowsAsync<ArgumentException>(() => services.RegisterAsync(new("V", ShortcutModifiers.Control), default).AsTask());
        }
        finally
        {
            await competitor.OnNativeThreadAsync(() => Native.UnregisterHotKey(competitor.NativeWindow, 99));
            Directory.Delete(directory, true);
        }
    }

    [InteractiveWindowsFact]
    public async Task Native_self_write_is_omitted_and_immediate_external_copy_is_captured()
    {
        using var port = new WindowsClipboardPort(); using var backup = new ClipboardBackup(port);
        var repository = new CaptureRepository();
        await using var capture = new ClipboardCaptureCoordinator(port, repository);
        await capture.StartAsync();
        backup.Expected = await capture.WriteTextAsync(port, "own synthetic value");
        await capture.NotifyClipboardChangedAsync(new(backup.Expected));
        Assert.Empty(repository.Captures);
        if (!Native.OpenClipboard(port.NativeWindow)) throw new InvalidOperationException("Clipboard busy during test.");
        try
        {
            Native.EmptyClipboard();
            var bytes = System.Text.Encoding.Unicode.GetBytes("external synthetic value\0");
            var handle = Native.GlobalAlloc(0x42, (UIntPtr)bytes.Length);
            var pointer = Native.GlobalLock(handle);
            Marshal.Copy(bytes, 0, pointer, bytes.Length); Native.GlobalUnlock(handle);
            if (Native.SetClipboardData(13, handle) == IntPtr.Zero) { Native.GlobalFree(handle); throw new InvalidOperationException("Test write failed."); }
        }
        finally { Native.CloseClipboard(); }
        backup.Expected = await port.GetRevisionAsync(default);
        await capture.NotifyClipboardChangedAsync(new(backup.Expected));
        if (repository.Captures.Count == 0)
        {
            var sample = await port.ReadAsync(await port.GetRevisionAsync(default), default);
            Assert.Fail($"External capture failed: flags={sample.ContentFlags}; length={sample.PlainText?.Length}; revisionMatches={sample.Revision == backup.Expected}.");
        }
        Assert.Single(repository.Captures);
        Assert.True(repository.Captures[0].Text == "external synthetic value");
    }

    private sealed class CaptureRepository : IHistoryRepository
    {
        internal List<HistoryCapture> Captures { get; } = [];
        public ValueTask UpsertCaptureAsync(HistoryCapture capture, CancellationToken token) { Captures.Add(capture); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<HistoryEntry>> GetEntriesAsync(string? search, CancellationToken token) => ValueTask.FromResult<IReadOnlyList<HistoryEntry>>([]);
        public ValueTask<bool> PinAsync(Guid id, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<bool> UnpinAsync(Guid id, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<bool> DeleteAsync(Guid id, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask ClearRecentAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ClearAllAsync(CancellationToken token) => ValueTask.CompletedTask;
    }


    // Preserve every HGLOBAL-backed format, never log it, never restore over an intervening external copy.
    // Non-memory formats refuse the destructive test rather than discard the user's clipboard data.
    private sealed class ClipboardBackup : IDisposable
    {
        private readonly WindowsClipboardPort _port;
        private readonly List<(uint Format, byte[] Data)> _formats = [];
        public ClipboardRevision Expected;
        public ClipboardBackup(WindowsClipboardPort port)
        {
            _port = port;
            if (!Open(IntPtr.Zero)) throw new InvalidOperationException("Clipboard busy; test did not change it.");
            try
            {
                uint format = 0;
                while ((format = Native.EnumClipboardFormats(format)) != 0)
                {
                    if (format is 2 or 3 or 9 or 14 or 0x80 or 0x81) throw new InvalidOperationException("Non-memory clipboard format; test did not change it.");
                    var handle = Native.GetClipboardData(format); var length = Native.GlobalSize(handle).ToUInt64();
                    if (handle == IntPtr.Zero || length == 0 || length > 16_777_216) throw new InvalidOperationException("Clipboard format cannot be backed up safely; test did not change it.");
                    var pointer = Native.GlobalLock(handle);
                    if (pointer == IntPtr.Zero) throw new InvalidOperationException("Clipboard backup failed; test did not change it.");
                    try { var data = new byte[(int)length]; Marshal.Copy(pointer, data, 0, data.Length); _formats.Add((format, data)); }
                    finally { Native.GlobalUnlock(handle); }
                }
            }
            finally { Native.CloseClipboard(); }
        }
        public void Dispose()
        {
            if (!Open(_port.NativeWindow)) return;
            try
            {
                if (WindowsClipboardPort.CreateRevision(Native.GetClipboardSequenceNumber()) != Expected) return;
                Native.EmptyClipboard();
                foreach (var (format, data) in _formats)
                {
                    var handle = Native.GlobalAlloc(0x42, (UIntPtr)data.Length); if (handle == IntPtr.Zero) continue;
                    var pointer = Native.GlobalLock(handle);
                    if (pointer == IntPtr.Zero) { Native.GlobalFree(handle); continue; }
                    try { Marshal.Copy(data, 0, pointer, data.Length); } finally { Native.GlobalUnlock(handle); }
                    if (Native.SetClipboardData(format, handle) == IntPtr.Zero) Native.GlobalFree(handle);
                }
            }
            finally { Native.CloseClipboard(); }
        }
        private static bool Open(IntPtr window)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (Native.OpenClipboard(window)) return true;
                Thread.Sleep(10);
            }
            return false;
        }
    }
    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateWindowEx(uint ex, string type, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr args);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetFocus();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(IntPtr window, string text);
        [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadKeyboardLayout(string layout, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool CloseClipboard();
        [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint previous);
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr handle);
    }
}
