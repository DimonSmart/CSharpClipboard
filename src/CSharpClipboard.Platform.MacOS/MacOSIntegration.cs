using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.MacOS;

public sealed class MacOSPasteSnapshot : IPasteTargetSnapshot
{
    internal string Id { get; }
    private readonly Func<string, Task> _release;
    private int _disposed;
    internal MacOSPasteSnapshot(string id, Func<string, Task> release) { Id = id; _release = release; }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _release(Id).ConfigureAwait(false); }
}

// Dispatcher is supplied at composition: AppKit C ABI calls always run on the real main thread.
// Timer callbacks only post work. UTF-8 JSON ownership ends at cc_free, before returning managed values.
public sealed class MacOSIntegration : IClipboardPort, IClipboardWritePort, IPasteTargetPort<MacOSPasteSnapshot>, IDesktopPlatformServices, IStatusItemPlatform
{
    private readonly Func<Action, Task> _dispatch;
    private readonly Timer _timer;
    private bool _ticking;
    private ClipboardRevision _previous;
    private int _disposed;
    private string _permission = "Default";
    private bool _accessibility;
    private string _startup = "Not registered";
    private Action<StatusItemAction>? _statusItemAction;
    static MacOSIntegration()
    {
        if (OperatingSystem.IsMacOS()) NativeLibrary.SetDllImportResolver(typeof(MacOSIntegration).Assembly, (name, assembly, searchPath) =>
        {
            if (name != "CSharpClipboardBridge") return IntPtr.Zero;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libCSharpClipboardBridge.dylib"));
            return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        });
    }
    public static void VerifyBridge()
    {
        if (Call("version").GetProperty("value").GetInt32() != 2)
            throw new InvalidOperationException("macOS bridge version mismatch. Reinstall the matching application bundle.");
    }
    public static string GetDataDirectory() => Call("path").GetProperty("value").GetString()!;
    public MacOSIntegration(Func<Action, Task> dispatch)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _dispatch = dispatch;
        VerifyBridge();
        Native.Umask(0x3F); // New history, WAL and preferences files are user-only (077 process mask).
        DataDirectory = GetDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _previous = new(Call("revision").GetProperty("value").GetString()!);
        _timer = new Timer(_ => _ = TickAsync(), null, 500, 500);
    }
    public string DataDirectory { get; }
    public bool UsesCommandKey => true;
    public string PlatformName => "macOS";
    public string ApplicationIdentifier => "dev.dimonsmart.CSharpClipboard";
    public event EventHandler<ClipboardSignal>? Changed;
    public event Action? HistoryRequested;
    public string PermissionDescription => $"Clipboard: {_permission}. Accessibility: {(_accessibility ? "Allowed" : "Not allowed")}. Only Always Allow enables automatic capture. Accessibility independently enables guarded paste. Default means the app may not appear in System Settings until Request Clipboard Access Once is used. Sources are best-effort.";
    public string StartupDescription => $"Launch at Login: {_startup}. Approval may be required in System Settings → General → Login Items. Run the installed .app bundle.";
    private async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _dispatch(() =>
        {
            if (_ticking || Volatile.Read(ref _disposed) != 0) return;
            _ticking = true;
            try
            {
                var revision = new ClipboardRevision(Call("revision").GetProperty("value").GetString()!);
                if (revision != _previous) { _previous = revision; Changed?.Invoke(this, new ClipboardSignal(revision)); }
                var pending = Call("poll");
                if (pending.GetProperty("value").GetInt32() > 0) HistoryRequested?.Invoke();
                if (pending.TryGetProperty("actions", out var actions))
                    foreach (var action in actions.EnumerateArray())
                        _statusItemAction?.Invoke(action.GetString() switch
                        {
                            "openHistory" => StatusItemAction.OpenHistory,
                            "toggleCapture" => StatusItemAction.ToggleCapture,
                            "settings" => StatusItemAction.Settings,
                            "clearRecentHistory" => StatusItemAction.ClearRecentHistory,
                            "about" => StatusItemAction.About,
                            "quit" => StatusItemAction.Quit,
                            _ => throw new InvalidOperationException("macOS status-item action is incompatible. Reinstall the complete matching .app bundle.")
                        });
            }
            finally { _ticking = false; }
        }).ConfigureAwait(false);
    }
    private static JsonElement Call(string operation, object? arguments = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arguments ?? new { });
        var pointer = Native.Call(operation, bytes, bytes.Length);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("macOS bridge is unavailable.");
        try
        {
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(pointer)!);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("macOS operation was refused. Check permissions and retry.");
            return root;
        }
        finally { Native.Free(pointer); }
    }
    private async Task<JsonElement> InvokeAsync(string operation, object? args = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        JsonElement result = default;
        await _dispatch(() => result = Call(operation, args)).ConfigureAwait(false);
        return result;
    }
    public async ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new((await InvokeAsync("revision").ConfigureAwait(false)).GetProperty("value").GetString()!);
    }
    public async ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InvokeAsync("read", new { revision = expectedRevision.Value }).ConfigureAwait(false);
        return new(new(result.GetProperty("revision").GetString()!), result.GetProperty("count").GetInt32(),
            result.TryGetProperty("text", out var text) ? text.GetString() : null,
            result.TryGetProperty("source", out var source) ? new ClipboardSource(source.GetString()!) : null,
            (ClipboardContentFlags)result.GetProperty("flags").GetInt32());
    }
    public async ValueTask<ClipboardRevision> WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new((await InvokeAsync("write", new { text }).ConfigureAwait(false)).GetProperty("value").GetString()!);
    }
    public async ValueTask<MacOSPasteSnapshot?> GetActiveTargetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = await InvokeAsync("takeStatusSnapshot").ConfigureAwait(false);
        if (pending.TryGetProperty("value", out var pendingId))
            return new MacOSPasteSnapshot(pendingId.GetString()!, async value => { await InvokeAsync("release", new { id = value }).ConfigureAwait(false); });
        var result = await InvokeAsync("snapshot").ConfigureAwait(false);
        return result.TryGetProperty("value", out var id) ? new MacOSPasteSnapshot(id.GetString()!, async value => { await InvokeAsync("release", new { id = value }).ConfigureAwait(false); }) : null;
    }
    public async ValueTask<bool> TryRestoreAsync(MacOSPasteSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!(await InvokeAsync("restore", new { id = snapshot.Id }).ConfigureAwait(false)).GetProperty("value").GetBoolean()) return false;
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        return true; // Dispatch freshly confirms AX identity after activation has completed.
    }
    public async ValueTask<PasteDispatchResult> TryDispatchAsync(MacOSPasteSnapshot snapshot, ClipboardRevision revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InvokeAsync("dispatch", new { id = snapshot.Id, revision = revision.Value }).ConfigureAwait(false);
        return Enum.Parse<PasteDispatchResult>(result.GetProperty("value").GetString()!);
    }
    public async ValueTask RegisterAsync(GlobalShortcut shortcut, CancellationToken cancellationToken) =>
        _ = await InvokeAsync("hotkey", new { key = shortcut.Key, modifiers = (int)shortcut.Modifiers }).ConfigureAwait(false);
    public async ValueTask UnregisterAsync(CancellationToken cancellationToken) => _ = await InvokeAsync("clearHotkey").ConfigureAwait(false);
    public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("startup").ConfigureAwait(false);
        _startup = result.GetProperty("status").GetString()!;
        return result.GetProperty("value").GetBoolean();
    }
    public async ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken) => _ = await InvokeAsync("setStartup", new { enabled }).ConfigureAwait(false);
    public async ValueTask<PermissionStatus> GetClipboardAccessAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("permission").ConfigureAwait(false);
        _permission = result.GetProperty("value").GetString()!;
        _accessibility = result.GetProperty("accessibility").GetBoolean();
        return _permission switch { "Allowed" => PermissionStatus.Allowed, "Denied" => PermissionStatus.Denied, _ => PermissionStatus.RequiresAccess };
    }
    public void RequestClipboardAccess() => Call("requestClipboard");
    public void RequestPasteAccess() => Call("requestAX");
    public void OpenClipboardSettings() => OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_Pasteboard");
    public void OpenUrl(string url) => Call("open", new { url });
    public bool IsValidApplicationIdentifier(string identifier) => Regex.IsMatch(identifier, @"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$");
    public (int X, int Y) GetActivationPoint() { var result = Call("point"); return (result.GetProperty("x").GetInt32(), result.GetProperty("y").GetInt32()); }
    public async ValueTask CreateStatusItemAsync(Action<StatusItemAction> onAction, bool capturePaused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onAction);
        cancellationToken.ThrowIfCancellationRequested();
        _statusItemAction = onAction;
        await InvokeAsync("createStatusItem", new { capturePaused }).ConfigureAwait(false);
    }
    public async ValueTask SetStatusItemCapturePausedAsync(bool capturePaused, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeAsync("setStatusItemCapturePaused", new { capturePaused }).ConfigureAwait(false);
    }
    public async ValueTask DiscardPendingStatusItemSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeAsync("discardStatusSnapshot").ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _statusItemAction = null;
        _timer.Dispose();
        _dispatch(() => Call("dispose")).GetAwaiter().GetResult();
    }
    private static class Native
    {
        [DllImport("libc", EntryPoint = "umask")] internal static extern uint Umask(uint mask);
        [DllImport("CSharpClipboardBridge", EntryPoint = "cc_call", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Call([MarshalAs(UnmanagedType.LPUTF8Str)] string operation, byte[] input, int length);
        [DllImport("CSharpClipboardBridge", EntryPoint = "cc_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr pointer);
    }
}
