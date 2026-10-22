using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CSharpClipboard.UnoLifecycleProbe;

// Uno 6.7.30 exposes AppWindow.Hide but marks it Uno0001 (not implemented).
// These native calls are deliberately isolated to the lifecycle experiment.
internal static partial class NativeWindowVisibility
{
    internal static void Show(Window window)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var native = Uno.UI.Xaml.WindowHelper.GetNativeWindow(window);
        var property = native?.GetType().GetProperty("Hwnd", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(native) is not IntPtr handle || handle == IntPtr.Zero || !IsWindow(handle))
            throw new InvalidOperationException("Win32 Uno window has no valid native HWND.");
        _ = ShowWindow(handle, 5); // SW_SHOW.
        if (!IsWindowVisible(handle))
            throw new InvalidOperationException("Win32 window remains hidden after SW_SHOW.");
        _ = SetForegroundWindow(handle);
        ProbeLog.Write($"Win32 ShowWindow(SW_SHOW) called: hwnd=0x{handle.ToInt64():X}");
    }

    internal static void Hide(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            // WindowNative.GetWindowHandle is an Uno AppWindow ID, not a Win32 HWND.
            var native = Uno.UI.Xaml.WindowHelper.GetNativeWindow(window);
            var property = native?.GetType().GetProperty("Hwnd", BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(native) is not IntPtr handle || handle == IntPtr.Zero)
                throw new InvalidOperationException("Win32 Uno window has no native HWND.");
            if (!IsWindow(handle))
                throw new InvalidOperationException("Uno returned an invalid Win32 HWND.");
            _ = ShowWindow(handle, 0); // SW_HIDE; BOOL reports previous visibility.
            if (IsWindowVisible(handle))
                throw new InvalidOperationException("Win32 window remains visible after SW_HIDE.");
            ProbeLog.Write($"Win32 ShowWindow(SW_HIDE) called: hwnd=0x{handle.ToInt64():X}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var native = Uno.UI.Xaml.WindowHelper.GetNativeWindow(window)
                ?? throw new InvalidOperationException("Uno did not return an AppKit native window.");

            // Avoid taking a compile-time dependency on an AppKit-specific target;
            // the returned NSObject is inspected only on macOS.
            var handleProperty = native.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("AppKit native window has no public Handle.");
            if (handleProperty.GetValue(native) is not IntPtr handle || handle == IntPtr.Zero)
                throw new InvalidOperationException("AppKit native window has no valid Objective-C handle.");

            var selector = SelRegisterName("orderOut:");
            ObjcMsgSend(handle, selector, IntPtr.Zero);
            ProbeLog.Write("NSWindow orderOut: sent on UI thread.");
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
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
