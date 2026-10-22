using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CSharpClipboard.App;

// Uno 6.7.30 has no implementation of AppWindow.Hide (Uno0001).
// The native hide must run on the Uno UI thread and does not close the Uno host.
internal static partial class NativeWindowVisibility
{
    // Uno calls ShowCore only on the first Window.Activate(). Subsequent Activate()
    // calls do not restore a window hidden directly with Win32 SW_HIDE.
    internal static void Show(Window window)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var hwnd = GetWindowsHandle(window);
        _ = ShowWindow(hwnd, 5); // SW_SHOW; BOOL reports previous visibility, not success.
        if (!IsWindowVisible(hwnd))
            throw new InvalidOperationException("Could not show the native Win32 window.");
        // The tray click is a user gesture; foreground permission is OS-controlled.
        _ = SetForegroundWindow(hwnd);
    }

    internal static void Hide(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = GetWindowsHandle(window);
            _ = ShowWindow(hwnd, 0); // SW_HIDE; BOOL reports previous visibility, not success.
            if (IsWindowVisible(hwnd))
                throw new InvalidOperationException("Could not hide the native Win32 window.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var native = Uno.UI.Xaml.WindowHelper.GetNativeWindow(window)
                ?? throw new InvalidOperationException("Uno window has no AppKit peer.");
            var property = native.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("AppKit window Handle unavailable.");
            if (property.GetValue(native) is not IntPtr handle || handle == IntPtr.Zero)
                throw new InvalidOperationException("AppKit window Handle invalid.");
            ObjcMsgSend(handle, SelRegisterName("orderOut:"), IntPtr.Zero);
        }
    }

    private static IntPtr GetWindowsHandle(Window window)
    {
        // Uno's WindowNative.GetWindowHandle returns an AppWindow ID, not a Win32 HWND.
        var hwnd = UnoWin32WindowHandle.Resolve(Uno.UI.Xaml.WindowHelper.GetNativeWindow(window));
        if (!IsWindow(hwnd)) throw new InvalidOperationException("Uno returned an invalid Win32 HWND.");
        return hwnd;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SelRegisterName(string selectorName);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void ObjcMsgSend(IntPtr receiver, IntPtr selector, IntPtr sender);
}
