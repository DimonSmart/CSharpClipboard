using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.Windows;

// Snapshot contains identities only, never text or retained COM objects. UIA RCWs live on MTA workers.
public sealed class WindowsPasteSnapshot : IPasteTargetSnapshot
{
    internal IntPtr Window { get; init; }
    internal IntPtr Focus { get; init; }
    internal uint ProcessId { get; init; }
    internal uint ThreadId { get; init; }
    internal long Started { get; init; }
    internal int[] ElementId { get; init; } = [];
    internal bool ConsoleWindowFallback { get; init; }
    internal int Invalidated;
    internal Action? Release;
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref Invalidated, 1); Interlocked.Exchange(ref Release, null)?.Invoke(); return ValueTask.CompletedTask; }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPasteTargetPort : IPasteTargetPort<WindowsPasteSnapshot>
{
    private readonly WindowsClipboardPort clipboard;
    private readonly int _applicationProcessId;
    internal string? LastRefusal { get; private set; }
    public WindowsPasteTargetPort(WindowsClipboardPort clipboard) : this(clipboard, Environment.ProcessId) { }
    internal WindowsPasteTargetPort(WindowsClipboardPort clipboard, int applicationProcessId) { this.clipboard = clipboard; _applicationProcessId = applicationProcessId; }
    public async ValueTask<WindowsPasteSnapshot?> GetActiveTargetAsync(CancellationToken cancellationToken)
    {
        var window = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == _applicationProcessId || !TrustedDesktop() || !SameIntegrity(pid)) { LastRefusal = "foreground/desktop/integrity"; return null; }
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var gui = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
                if (!Native.GetGUIThreadInfo(thread, ref gui) || gui.Focus == IntPtr.Zero) { LastRefusal = "focus unavailable"; return null; }
                var identity = FocusedIdentity(pid, gui.Focus);
                var consoleFallback = identity.State == WindowsFocusIdentityState.Unavailable &&
                    IsRecognizedConsoleWindow(pid, window) && IsFocusWithinWindow(window, gui.Focus);
                if ((identity.State != WindowsFocusIdentityState.Verified && !consoleFallback) ||
                    Native.GetForegroundWindow() != window)
                {
                    LastRefusal = "UIA identity/console fallback/foreground unavailable";
                    return null;
                }
                var snapshot = new WindowsPasteSnapshot { Window = window, Focus = gui.Focus, ProcessId = pid,
                    ThreadId = thread, Started = ProcessStart(pid), ElementId = identity.RuntimeId ?? [],
                    ConsoleWindowFallback = consoleFallback };
                void Observe(IntPtr foreground)
                {
                    Native.GetWindowThreadProcessId(foreground, out var owner);
                    if (foreground != window && owner != _applicationProcessId) Interlocked.Exchange(ref snapshot.Invalidated, 1);
                }
                clipboard.ForegroundChanged += Observe;
                snapshot.Release = () => clipboard.ForegroundChanged -= Observe;
                LastRefusal = null;
                return snapshot;
            }
            catch (Exception error) { LastRefusal = error.GetType().Name; return null; }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryRestoreAsync(WindowsPasteSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref snapshot.Invalidated) != 0 || !TrustedDesktop() || !SameIntegrity(snapshot.ProcessId))
        {
            LastRefusal = "invalidated/desktop/integrity";
            return false;
        }
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var pid);
        if (foreground != snapshot.Window && pid != _applicationProcessId)
        {
            LastRefusal = "different foreground application";
            return false;
        }
        if (foreground != snapshot.Window && !Native.SetForegroundWindow(snapshot.Window))
        {
            LastRefusal = "foreground activation refused";
            return false;
        }
        // Restoring the foreground window and its focused child is asynchronous on
        // Windows 11 (including Notepad). Never force SetFocus: only wait for the
        // original HWND and UIA runtime identity to be restored naturally.
        var restored = await WindowsFocusRestoreWaiter.WaitAsync(
            () => CheckRestoredFocus(snapshot), cancellationToken).ConfigureAwait(false);
        LastRefusal = restored ? null : "original focus not restored";
        return restored;
    }

    private WindowsFocusRestoreState CheckRestoredFocus(WindowsPasteSnapshot snapshot)
    {
        try
        {
            if (Volatile.Read(ref snapshot.Invalidated) != 0 || !TrustedDesktop() || !SameIntegrity(snapshot.ProcessId))
                return WindowsFocusRestoreState.Rejected;
            var foreground = Native.GetForegroundWindow();
            if (foreground != snapshot.Window)
            {
                Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
                return foregroundPid == _applicationProcessId
                    ? WindowsFocusRestoreState.Pending : WindowsFocusRestoreState.Rejected;
            }
            var thread = Native.GetWindowThreadProcessId(snapshot.Window, out var pid);
            if (pid != snapshot.ProcessId || thread != snapshot.ThreadId || ProcessStart(pid) != snapshot.Started)
                return WindowsFocusRestoreState.Rejected;
            var gui = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref gui) || gui.Focus == IntPtr.Zero)
                return WindowsFocusRestoreState.Pending;
            // A different focused control is a user/context change, not a slow
            // restore; never wait for it to become the old field again.
            if (gui.Focus != snapshot.Focus) return WindowsFocusRestoreState.Rejected;
            if (snapshot.ConsoleWindowFallback)
                return ConsoleFocusMatches(snapshot, pid, gui.Focus)
                    ? WindowsFocusRestoreState.Matched : WindowsFocusRestoreState.Rejected;
            var identity = FocusedIdentity(pid, gui.Focus);
            return identity.State == WindowsFocusIdentityState.Unavailable ? WindowsFocusRestoreState.Pending
                : identity.State == WindowsFocusIdentityState.Verified &&
                  identity.RuntimeId is { } id && id.SequenceEqual(snapshot.ElementId)
                    ? WindowsFocusRestoreState.Matched : WindowsFocusRestoreState.Rejected;
        }
        catch
        {
            // A transient UIA failure may clear once the original window settles.
            // All attempts remain subject to the same PID, HWND and desktop checks.
            return WindowsFocusRestoreState.Pending;
        }
    }

    public async ValueTask<PasteDispatchResult> TryDispatchAsync(WindowsPasteSnapshot snapshot, ClipboardRevision expectedRevision, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool RevisionMatches() => WindowsClipboardPort.CreateRevision(Native.GetClipboardSequenceNumber()) == expectedRevision;
            if (!RevisionMatches()) return PasteDispatchResult.ClipboardChanged;
            if (!TrustedDesktop() || !SameIntegrity(snapshot.ProcessId) || !Matches(snapshot)) return PasteDispatchResult.TargetUnavailable;
            foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return PasteDispatchResult.TargetUnavailable;
            var layout = Native.GetKeyboardLayout(snapshot.ThreadId);
            var scan = Native.MapVirtualKeyEx(0x56, 0, layout); // Resolve logical Ctrl+V through the target HKL, including Russian/Dvorak.
            if (layout == IntPtr.Zero || scan == 0 || scan > 255) return PasteDispatchResult.TargetUnavailable;
            var inputs = new[] { Key(0x11, false), ScanKey((ushort)scan, false), ScanKey((ushort)scan, true), Key(0x11, true) };
            if (!RevisionMatches()) return PasteDispatchResult.ClipboardChanged;
            if (!Matches(snapshot) || !TrustedDesktop() || !SameIntegrity(snapshot.ProcessId))
                return RevisionMatches() ? PasteDispatchResult.TargetUnavailable : PasteDispatchResult.ClipboardChanged;
            if (!RevisionMatches()) return PasteDispatchResult.ClipboardChanged;
            var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent is > 0 and < 4)
            {
                // Release only keys introduced by this dispatch. Never retry a partial shortcut.
                Native.SendInput(2, [ScanKey((ushort)scan, true), Key(0x11, true)], Marshal.SizeOf<Input>());
            }
            // Sending only some key events is not a completed Ctrl+V shortcut.
            return WindowsPasteSendResult.FromSentCount(sent, (uint)inputs.Length);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(WindowsPasteSnapshot snapshot)
    {
        try
        {
            if (Volatile.Read(ref snapshot.Invalidated) != 0 || Native.GetForegroundWindow() != snapshot.Window) return false;
            var thread = Native.GetWindowThreadProcessId(snapshot.Window, out var pid);
            if (pid != snapshot.ProcessId || thread != snapshot.ThreadId || ProcessStart(pid) != snapshot.Started) return false;
            var gui = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref gui) || gui.Focus != snapshot.Focus) return false;
            if (snapshot.ConsoleWindowFallback) return ConsoleFocusMatches(snapshot, pid, gui.Focus);
            var identity = FocusedIdentity(pid, gui.Focus);
            return identity.State == WindowsFocusIdentityState.Verified &&
                   identity.RuntimeId is { } id && id.SequenceEqual(snapshot.ElementId);
        }
        catch { return false; }
    }

    private readonly record struct FocusIdentity(WindowsFocusIdentityState State, int[]? RuntimeId);

    // Console windows can be keyboard-focusable even when UIA exposes no usable element.
    // A positive password/disabled result still blocks the limited console fallback.
    private static FocusIdentity FocusedIdentity(uint pid, IntPtr focusedWindow)
    {
        object? automation = null;
        IElement? element = null;
        try
        {
            automation = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"), true)!);
            var className = new System.Text.StringBuilder(256);
            Native.GetClassName(focusedWindow, className, className.Capacity);
            if (className.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                className.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                ((IAutomation)automation!).ElementFromHandle(focusedWindow, out element);
            else ((IAutomation)automation!).GetFocusedElement(out element);
            if (element is null) return new(WindowsFocusIdentityState.Unavailable, null);
            element.GetCurrentPropertyValue(30019, out var password);
            element.GetCurrentPropertyValue(30010, out var enabled);
            element.GetCurrentPropertyValue(30002, out var process);
            if (password is true || enabled is false)
                return new(WindowsFocusIdentityState.Protected, null);
            if (password is not false || enabled is not true || process is not int id || id != pid)
                return new(WindowsFocusIdentityState.Unavailable, null);
            element.GetRuntimeId(out var runtimeId);
            return runtimeId is { Length: > 0 }
                ? new(WindowsFocusIdentityState.Verified, runtimeId)
                : new(WindowsFocusIdentityState.Unavailable, null);
        }
        catch (Exception)
        {
            // UIA availability is not an input permission check. Only allow a fallback
            // if the independent console-host window/process checks succeed.
            return new(WindowsFocusIdentityState.Unavailable, null);
        }
        finally
        {
            if (element is not null) Marshal.FinalReleaseComObject(element);
            if (automation is not null) Marshal.FinalReleaseComObject(automation);
        }
    }

    private static bool ConsoleFocusMatches(WindowsPasteSnapshot snapshot, uint pid, IntPtr focus)
    {
        if (!IsFocusWithinWindow(snapshot.Window, focus) ||
            !IsRecognizedConsoleWindow(pid, snapshot.Window)) return false;
        // Recheck UIA protection on every restore/dispatch; lack of UIA alone is allowed.
        return FocusedIdentity(pid, focus).State != WindowsFocusIdentityState.Protected;
    }

    private static bool IsFocusWithinWindow(IntPtr window, IntPtr focus) =>
        focus != IntPtr.Zero && (focus == window || Native.GetAncestor(focus, 2) == window);

    private static bool IsRecognizedConsoleWindow(uint pid, IntPtr window)
    {
        var className = new System.Text.StringBuilder(128);
        if (Native.GetClassName(window, className, className.Capacity) == 0) return false;
        using var process = Process.GetProcessById((int)pid);
        return WindowsConsoleTargetPolicy.IsRecognizedConsoleHost(className.ToString(), process.ProcessName);
    }

    private static bool TrustedDesktop()
    {
        if (Native.GetSystemMetrics(0x1000) != 0) return false; // Remote session identity is not established.
        var desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new System.Text.StringBuilder(256);
            return Native.GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseDesktop(desktop); }
    }

    private static long ProcessStart(uint id) { using var process = Process.GetProcessById((int)id); return process.StartTime.ToUniversalTime().Ticks; }

    private static bool SameIntegrity(uint pid)
    {
        uint Level(uint processId)
        {
            var process = Native.OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) throw new InvalidOperationException();
            try
            {
                if (!Native.OpenProcessToken(process, 8, out var token)) throw new InvalidOperationException();
                try
                {
                    Native.GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (!Native.GetTokenInformation(token, 25, buffer, size, out _)) throw new InvalidOperationException();
                        var sid = Marshal.ReadIntPtr(buffer);
                        var count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                        return (uint)Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { Native.CloseHandle(token); }
            }
            finally { Native.CloseHandle(process); }
        }
        try { return Level(pid) <= Level((uint)Environment.ProcessId); } catch { return false; }
    }

    private static Input Key(ushort key, bool up) => new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } };
    private static Input ScanKey(ushort scan, bool up) => new() { Type = 1, Keyboard = new KeyboardInput { Scan = scan, Flags = up ? 10u : 8u } };
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements(IntPtr a, IntPtr b, out int same);
        void CompareRuntimeIds(IntPtr a, IntPtr b, out int same);
        void GetRootElement(out IElement element);
        void ElementFromHandle(IntPtr handle, out IElement element);
        void ElementFromPoint(long point, out IElement element);
        void GetFocusedElement(out IElement element);
    }
    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void SetFocus();
        void GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] ids);
        void FindFirst(int scope, IntPtr condition, out IntPtr found);
        void FindAll(int scope, IntPtr condition, out IntPtr found);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr found);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr found);
        void BuildUpdatedCache(IntPtr cache, out IntPtr found);
        void GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
    private static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScanEx(char character, IntPtr layout);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint MapVirtualKeyEx(uint code, uint map, IntPtr layout);
        [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder name, uint size, out uint needed);
        [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll")] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] internal static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, uint length, out uint needed);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
