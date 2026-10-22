using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.Windows;

// A real Win32 notification-area item. Independent from Uno and from the
// clipboard listener's message-only window. Explorer restart recreates its icon.
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayIcon : IDisposable
{
    private const uint TrayCallback = 0x8002;
    private const uint WmClose = 0x0010, WmDestroy = 0x0002;
    private const uint WmLeftUp = 0x0202, WmRightUp = 0x0205, WmContextMenu = 0x007B;
    private static readonly WindowProc WndProcHandler = WndProc;
    private static readonly Dictionary<IntPtr, WindowsTrayIcon> Instances = new();
    private static readonly object InstancesGate = new();

    private readonly Action<StatusItemAction> _onAction;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _className = "CSharpClipboard.Tray." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd;
    private ushort _atom;
    private Exception? _error;
    private int _disposed;
    private volatile bool _paused;

    internal WindowsTrayIcon(Action<StatusItemAction> onAction, bool paused)
    {
        _onAction = onAction;
        _paused = paused;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _thread = new Thread(Run) { IsBackground = true, Name = "CSharpClipboard.Win32Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_error is not null) throw new InvalidOperationException("Failed to create Windows tray icon.", _error);
    }

    internal void UpdateCaptureState(bool paused)
    {
        _paused = paused;
        if (Volatile.Read(ref _disposed) == 0 && _hwnd != IntPtr.Zero)
            PostMessage(_hwnd, 0x8003, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var wc = new WndClass
            {
                Size = (uint)Marshal.SizeOf<WndClass>(),
                Instance = instance,
                WindowProcedure = WndProcHandler,
                ClassName = _className
            };
            _atom = RegisterClassEx(ref wc);
            if (_atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _hwnd = CreateWindowEx(0x80, _className, _className, 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            lock (InstancesGate) Instances[_hwnd] = this;
            AddIcon();
            _ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { _error = e; _ready.Set(); }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                DeleteIcon();
                lock (InstancesGate) Instances.Remove(_hwnd);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_atom != 0) UnregisterClass(_className, GetModuleHandle(null));
            _ready.Set();
        }
    }

    private NotifyIconData Icon()
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _hwnd,
            Id = 1,
            Flags = 0x1 | 0x2 | 0x4,
            CallbackMessage = TrayCallback,
            Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            Tip = _paused ? "CSharpClipboard — Capture paused" : "CSharpClipboard — Capture active",
            Info = "",
            InfoTitle = ""
        };
    }
    private void AddIcon() { var data = Icon(); if (!Shell_NotifyIcon(0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Shell_NotifyIcon(NIM_ADD) failed."); }
    private void DeleteIcon() { var data = Icon(); Shell_NotifyIcon(2, ref data); }
    private void ModifyIcon() { var data = Icon(); Shell_NotifyIcon(1, ref data); }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp)
    {
        WindowsTrayIcon? owner;
        lock (InstancesGate) Instances.TryGetValue(hwnd, out owner);
        if (owner is not null)
        {
            if (message == owner._taskbarCreated) { owner.AddIcon(); return IntPtr.Zero; }
            if (message == 0x8003) { owner.ModifyIcon(); return IntPtr.Zero; }
            if (message == TrayCallback)
            {
                var action = unchecked((uint)lp.ToInt64());
                if (action == WmLeftUp) owner._onAction(StatusItemAction.OpenHistory);
                else if (action is WmRightUp or WmContextMenu) owner.ShowMenu();
                return IntPtr.Zero;
            }
            if (message == WmClose) { DestroyWindow(hwnd); return IntPtr.Zero; }
            if (message == WmDestroy) { PostQuitMessage(0); return IntPtr.Zero; }
        }
        return DefWindowProc(hwnd, message, wp, lp);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, 1, "Clipboard History");
            AppendMenu(menu, 0, 2, _paused ? "Resume Capture" : "Pause Capture");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 3, "Settings…");
            AppendMenu(menu, 0, 4, "Clear Recent History");
            AppendMenu(menu, 0, 5, "About…");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 6, "Quit");
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            var action = command switch
            {
                1 => StatusItemAction.OpenHistory,
                2 => StatusItemAction.ToggleCapture,
                3 => StatusItemAction.Settings,
                4 => StatusItemAction.ClearRecentHistory,
                5 => StatusItemAction.About,
                6 => StatusItemAction.Quit,
                _ => (StatusItemAction?)null
            };
            if (action is { } selected) _onAction(selected);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join();
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Size, Style;
        public WindowProc? WindowProcedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? label);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserve, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
}
