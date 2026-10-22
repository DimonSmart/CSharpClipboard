using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.Windows;

/// <summary>
/// Receives Windows clipboard notifications from its own message-only window.
/// All window lifetime work occurs on the message-loop thread; clipboard reads
/// are synchronous and pair every successful native acquisition with its release.
/// </summary>
public sealed class WindowsClipboardPort : IClipboardPort, IClipboardWritePort, IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const int CfUnicodeText = 13;
    private const int CfHdrop = 15;
    private const int MaxOpenAttempts = 3;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly ConcurrentDictionary<IntPtr, WindowsClipboardPort> Windows = new();
    private static readonly WindowProcedure WindowProcedureCallback = DispatchWindowMessage;

    private readonly Thread _messageThread;
    private readonly ManualResetEventSlim _windowReady = new();
    private readonly ManualResetEventSlim _messageLoopStopped = new();
    private readonly string _windowClassName = $"CSharpClipboard.{Guid.NewGuid():N}";
    private Exception? _initializationException;
    private IntPtr _window;
    private ushort _windowClass;
    private int _disposed;
    private readonly ConcurrentQueue<Action> _nativeWork = new();
    private WinEventCallback? _foregroundCallback;
    private IntPtr _foregroundHook;
    private readonly object _access = new();
    private static uint ExactFormat => NativeMethods.RegisterClipboardFormat("dev.dimonsmart.CSharpClipboard.ExactUtf8.v1");
    private static uint GeneratedFormat => NativeMethods.RegisterClipboardFormat("dev.dimonsmart.CSharpClipboard.Generated.v1");

    public WindowsClipboardPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsClipboardPort requires Windows.");
        }

        _messageThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = nameof(WindowsClipboardPort)
        };
        _messageThread.Start();
        _windowReady.Wait();

        if (_initializationException is not null)
        {
            _messageLoopStopped.Wait();
            throw new InvalidOperationException("The Windows clipboard listener could not start.", _initializationException);
        }
    }

    public event EventHandler<ClipboardSignal>? Changed;
    public event Action? ShortcutPressed;
    public event Action<IntPtr>? ForegroundChanged;

    internal Task OnNativeThreadAsync(Action action)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeWork.Enqueue(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        if (!NativeMethods.PostMessage(_window, 0x8001, IntPtr.Zero, IntPtr.Zero))
            completion.TrySetException(new Win32Exception("Native window is unavailable."));
        return completion.Task;
    }

    internal IntPtr NativeWindow => _window;

    // Memory is prepared before EmptyClipboard. Windows owns each handle only after SetClipboardData succeeds.
    // The post-publication revision is attributed only while the original owner remains current.
    public ValueTask<ClipboardRevision> WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_access)
        {
            ThrowIfDisposed();
            var unicode = Allocate(Encoding.Unicode.GetBytes(text + '\0'));
            var utf8 = Encoding.UTF8.GetBytes(text);
            var exactBytes = new byte[utf8.Length + 4];
            BitConverter.GetBytes(utf8.Length).CopyTo(exactBytes, 0);
            utf8.CopyTo(exactBytes, 4);
            var exact = IntPtr.Zero;
            var marker = IntPtr.Zero;
            try
            {
                exact = Allocate(exactBytes);
                marker = Allocate([1]);
                var exactFormat = ExactFormat;
                var generatedFormat = GeneratedFormat;
                if (exactFormat == 0 || generatedFormat == 0) throw new Win32Exception("Clipboard format registration failed.");
                var opened = false;
                for (var attempt = 0; attempt < MaxOpenAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (NativeMethods.OpenClipboard(_window)) { opened = true; break; }
                    if (attempt < MaxOpenAttempts - 1) Thread.Sleep(10);
                }
                if (!opened) throw new Win32Exception("Clipboard is busy. Try again.");
                ClipboardRevision beforeClose;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!NativeMethods.EmptyClipboard()) throw new Win32Exception("Could not prepare clipboard.");
                    if (NativeMethods.SetClipboardData(CfUnicodeText, unicode) == IntPtr.Zero)
                        throw new Win32Exception("Could not write clipboard text.");
                    unicode = IntPtr.Zero;
                    // Text is committed. Ancillary failures cannot turn this into a retryable precommit failure.
                    if (NativeMethods.SetClipboardData(exactFormat, exact) != IntPtr.Zero) exact = IntPtr.Zero;
                    if (NativeMethods.SetClipboardData(generatedFormat, marker) != IntPtr.Zero) marker = IntPtr.Zero;
                    beforeClose = CurrentRevision();
                }
                finally { NativeMethods.CloseClipboard(); }
                // Windows may synthesize formats/update its sequence when CloseClipboard publishes the write.
                // Attribute the post-close number only while our owner still holds the same stable revision.
                // An intervening external owner yields a stale receipt, causing conservative paste cancellation;
                // it can never suppress that external owner's revision as our own.
                var published = CurrentRevision();
                return ValueTask.FromResult(NativeMethods.GetClipboardOwner() == _window && CurrentRevision() == published ? published : beforeClose);
            }
            finally
            {
                if (unicode != IntPtr.Zero) NativeMethods.GlobalFree(unicode);
                if (exact != IntPtr.Zero) NativeMethods.GlobalFree(exact);
                if (marker != IntPtr.Zero) NativeMethods.GlobalFree(marker);
            }
        }
    }

    private static IntPtr Allocate(byte[] bytes)
    {
        var memory = NativeMethods.GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = NativeMethods.GlobalLock(memory);
        if (pointer == IntPtr.Zero) { NativeMethods.GlobalFree(memory); throw new Win32Exception("Clipboard memory lock failed."); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { NativeMethods.GlobalUnlock(memory); }
        return memory;
    }

    public ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CurrentRevision());
    }

    public ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision expectedRevision, CancellationToken cancellationToken)
    {
        lock (_access) return ReadCore(expectedRevision, cancellationToken);
    }

    private ValueTask<ClipboardReadResult> ReadCore(ClipboardRevision expectedRevision, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (CurrentRevision() != expectedRevision)
        {
            throw new InvalidOperationException("The clipboard revision changed before it could be read.");
        }

        for (var attempt = 0; attempt < MaxOpenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CurrentRevision() != expectedRevision)
            {
                throw new InvalidOperationException("The clipboard revision changed while waiting to read it.");
            }

            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (CurrentRevision() != expectedRevision)
                    {
                        throw new InvalidOperationException("The clipboard revision changed while it was being read.");
                    }

                    var result = ReadOpenedClipboard(expectedRevision);
                    if (CurrentRevision() != expectedRevision)
                    {
                        throw new InvalidOperationException("The clipboard revision changed while it was being read.");
                    }

                    return ValueTask.FromResult(result);
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (CurrentRevision() != expectedRevision)
            {
                throw new InvalidOperationException("The clipboard revision changed while waiting to read it.");
            }

            if (attempt < MaxOpenAttempts - 1)
            {
                Thread.Sleep(10);
            }
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenClipboard remained unavailable.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var window = Volatile.Read(ref _window);
        if (window != IntPtr.Zero)
        {
            NativeMethods.PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _messageLoopStopped.Wait();
        _windowReady.Dispose();
        _messageLoopStopped.Dispose();
    }

    internal static ClipboardReadResult CreateReadResult(
        ClipboardRevision revision,
        string? plainText,
        bool containsFileDrop,
        bool monitorExcluded)
    {
        var flags = ClipboardContentFlags.None;
        if (containsFileDrop)
        {
            flags |= ClipboardContentFlags.ContainsFiles;
        }

        if (monitorExcluded)
        {
            flags |= ClipboardContentFlags.ProtectedOrExcluded;
        }

        return new ClipboardReadResult(revision, 1, plainText, null, flags);
    }

    internal static ClipboardRevision CreateRevision(uint sequenceNumber) =>
        new(sequenceNumber.ToString(CultureInfo.InvariantCulture));

    private ClipboardReadResult ReadOpenedClipboard(ClipboardRevision expectedRevision)
    {
        var containsFileDrop = NativeMethods.IsClipboardFormatAvailable(CfHdrop);
        var monitorExcluded = IsMonitorExcluded();
        string? plainText = null;

        if (NativeMethods.IsClipboardFormatAvailable(CfUnicodeText))
        {
            var data = NativeMethods.GetClipboardData(CfUnicodeText);
            if (data != IntPtr.Zero)
            {
                plainText = ReadUnicodeText(data);
            }
        }

        var generated = NativeMethods.IsClipboardFormatAvailable(GeneratedFormat);
        if (generated && NativeMethods.IsClipboardFormatAvailable(ExactFormat))
        {
            var data = NativeMethods.GetClipboardData(ExactFormat);
            if (data != IntPtr.Zero && NativeMethods.GlobalSize(data).ToUInt64() is >= 4 and <= 1048580)
            {
                var pointer = NativeMethods.GlobalLock(data);
                if (pointer != IntPtr.Zero)
                {
                    try
                    {
                        var length = Marshal.ReadInt32(pointer);
                        if (length >= 0 && length <= (long)NativeMethods.GlobalSize(data).ToUInt64() - 4)
                        {
                            var bytes = new byte[length];
                            Marshal.Copy(pointer + 4, bytes, 0, length);
                            plainText = new UTF8Encoding(false, true).GetString(bytes);
                        }
                    }
                    finally { NativeMethods.GlobalUnlock(data); }
                }
            }
        }
        var result = CreateReadResult(expectedRevision, plainText, containsFileDrop, monitorExcluded);
        try
        {
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetClipboardOwner(), out var owner);
            if (owner != 0)
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)owner);
                if (process.MainModule?.FileName is { } path) result = result with { Source = new ClipboardSource(Path.GetFullPath(path)) };
            }
        }
        catch { /* Source ownership is best-effort and never required to capture text. */ }
        return generated ? result with { ContentFlags = result.ContentFlags | ClipboardContentFlags.Generated } : result;
    }

    private static string? ReadUnicodeText(IntPtr globalMemory)
    {
        var byteCount = NativeMethods.GlobalSize(globalMemory).ToUInt64();
        if (byteCount == 0 || byteCount > 2097154 || (byteCount & 1) != 0)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(globalMemory);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(pointer, checked((int)byteCount / sizeof(char)));
            var terminator = text?.IndexOf('\0') ?? -1;
            return terminator >= 0 ? text![..terminator] : null;
        }
        finally
        {
            NativeMethods.GlobalUnlock(globalMemory);
        }
    }

    private static bool IsMonitorExcluded()
    {
        var excludeFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        var historyFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        return (excludeFormat != 0 && NativeMethods.IsClipboardFormatAvailable(excludeFormat)) ||
               (historyFormat != 0 && IsClipboardHistoryDisabled(historyFormat));
    }

    private static bool IsClipboardHistoryDisabled(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return false;
        }

        var data = NativeMethods.GetClipboardData(format);
        if (data == IntPtr.Zero || NativeMethods.GlobalSize(data).ToUInt64() < sizeof(uint))
        {
            return false;
        }

        var pointer = NativeMethods.GlobalLock(data);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Marshal.ReadInt32(pointer) == 0;
        }
        finally
        {
            NativeMethods.GlobalUnlock(data);
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            var instance = NativeMethods.GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = WindowProcedureCallback,
                Instance = instance,
                ClassName = _windowClassName
            };
            _windowClass = NativeMethods.RegisterClassEx(ref windowClass);
            if (_windowClass == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
            }

            var window = NativeMethods.CreateWindowEx(
                0,
                _windowClassName,
                _windowClassName,
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
            }

            Windows[window] = this;
            Volatile.Write(ref _window, window);
            if (!NativeMethods.AddClipboardFormatListener(window))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed.");
            }

            _windowReady.Set();
            _foregroundCallback = (_, _, foreground, _, _, _, _) => ForegroundChanged?.Invoke(foreground);
            _foregroundHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 0);
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _initializationException = exception;
            _windowReady.Set();
        }
        finally
        {
            if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
            var window = Interlocked.Exchange(ref _window, IntPtr.Zero);
            if (window != IntPtr.Zero)
            {
                Windows.TryRemove(window, out _);
                NativeMethods.RemoveClipboardFormatListener(window);
                NativeMethods.DestroyWindow(window);
            }

            if (_windowClass != 0)
            {
                NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            }

            _messageLoopStopped.Set();
        }
    }

    private static IntPtr DispatchWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(window, out var port))
        {
            if (message == 0x8001) { while (port._nativeWork.TryDequeue(out var action)) action(); return IntPtr.Zero; }
            if (message == 0x0312) { port.ShortcutPressed?.Invoke(); return IntPtr.Zero; }
            if (message == WmClipboardUpdate)
            {
                port.Changed?.Invoke(port, new ClipboardSignal(CurrentRevision()));
                return IntPtr.Zero;
            }

            if (message == WmClose)
            {
                NativeMethods.RemoveClipboardFormatListener(window);
                NativeMethods.DestroyWindow(window);
                return IntPtr.Zero;
            }

            if (message == WmDestroy)
            {
                Windows.TryRemove(window, out _);
                Interlocked.CompareExchange(ref port._window, IntPtr.Zero, window);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private static ClipboardRevision CurrentRevision() => CreateRevision(NativeMethods.GetClipboardSequenceNumber());

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint threadId, uint time);

    private static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(out Message message, IntPtr window, uint minimumFilter, uint maximumFilter);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterClipboardFormat(string format);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UIntPtr GlobalSize(IntPtr memory);
    }
}
