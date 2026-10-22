using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CSharpClipboard.Core;
using Microsoft.Win32;

namespace CSharpClipboard.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopServices : IDesktopPlatformServices
{
    private readonly WindowsClipboardPort _clipboard;
    private int _hotkeyId;
    private GlobalShortcut? _shortcut;
    public WindowsDesktopServices(WindowsClipboardPort clipboard) : this(clipboard, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSharpClipboard")) { }
    internal WindowsDesktopServices(WindowsClipboardPort clipboard, string dataDirectory)
    {
        _clipboard = clipboard;
        _clipboard.ShortcutPressed += OnShortcut;
        DataDirectory = dataDirectory;
        var directory = Directory.CreateDirectory(DataDirectory);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("User identity unavailable.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    public event Action? HistoryRequested;
    private void OnShortcut() => HistoryRequested?.Invoke();
    public string DataDirectory { get; }
    public bool UsesCommandKey => false;
    public string PlatformName => "Windows";
    public string ApplicationIdentifier => "dev.dimonsmart.CSharpClipboard";
    public string PermissionDescription => "Windows does not use a Clipboard permission prompt. Protected, elevated, remote or uncertain targets use Copy Only. Sources and exclusions are best-effort. Windows exclusions use an exact full executable path.";
    public string StartupDescription => "Per-user Windows Run registration; no elevated privileges.";
    public ValueTask<PermissionStatus> GetClipboardAccessAsync(CancellationToken cancellationToken) => ValueTask.FromResult(PermissionStatus.Allowed);
    public bool IsValidApplicationIdentifier(string identifier) => Path.IsPathFullyQualified(identifier) && identifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public void RequestClipboardAccess() { }
    public void RequestPasteAccess() { }
    public void OpenClipboardSettings() => OpenUrl("ms-settings:privacy");
    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public (int X, int Y) GetActivationPoint() { Native.GetCursorPos(out var point); return (point.X, point.Y); }
    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return ValueTask.FromResult(key?.GetValue("CSharpClipboard") as string == StartupCommand);
    }
    private static string StartupCommand => $"\"{Environment.ProcessPath}\"";
    public ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CSharpClipboard", StartupCommand); else key.DeleteValue("CSharpClipboard", false);
        return ValueTask.CompletedTask;
    }
    public async ValueTask RegisterAsync(GlobalShortcut shortcut, CancellationToken cancellationToken)
    {
        var key = shortcut.Key.ToUpperInvariant();
        if (shortcut.Modifiers == ShortcutModifiers.None || shortcut.Modifiers.HasFlag(ShortcutModifiers.Meta) || key == "V" || key is "TAB" or "ESCAPE" or "DELETE" || key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0]))
            throw new ArgumentException("Choose Ctrl/Alt/Shift with a letter or number. Paste and system shortcuts are reserved.");
        if (_shortcut == shortcut) return;
        uint modifiers = 0x4000;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers |= 2;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= 1;
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= 4;
        await _clipboard.OnNativeThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = _hotkeyId == 1 ? 2 : 1;
            if (!Native.RegisterHotKey(_clipboard.NativeWindow, id, modifiers, key[0])) throw new Win32Exception("Shortcut is unavailable. The previous shortcut is still active.");
            if (_hotkeyId != 0) Native.UnregisterHotKey(_clipboard.NativeWindow, _hotkeyId);
            _hotkeyId = id;
            _shortcut = shortcut;
        }).ConfigureAwait(false);
    }
    public async ValueTask UnregisterAsync(CancellationToken cancellationToken)
    {
        await _clipboard.OnNativeThreadAsync(() => { if (_hotkeyId != 0) Native.UnregisterHotKey(_clipboard.NativeWindow, _hotkeyId); _hotkeyId = 0; _shortcut = null; }).ConfigureAwait(false);
    }
    public void Dispose() { UnregisterAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); _clipboard.ShortcutPressed -= OnShortcut; }
    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
}
