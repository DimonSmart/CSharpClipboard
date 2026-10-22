using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CSharpClipboard.UnoLifecycleProbe;

// Uno 6.7.30 exposes AppWindow.Hide but marks it Uno0001 (not implemented).
// These native calls are deliberately isolated to the lifecycle experiment.
internal static partial class NativeWindowVisibility
{
    internal static void Hide(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Win32 Uno window has no HWND.");
            ShowWindow(handle, 0); // SW_HIDE; BOOL result means *previous* visibility.
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

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SelRegisterName(string selectorName);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void ObjcMsgSend(IntPtr receiver, IntPtr selector, IntPtr sender);
}
