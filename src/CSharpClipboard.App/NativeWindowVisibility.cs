using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CSharpClipboard.App;

// Uno 6.7.30 has no implementation of AppWindow.Hide (Uno0001).
// The native hide must run on the Uno UI thread and does not close the Uno host.
internal static partial class NativeWindowVisibility
{
    internal static void Hide(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Uno window has no HWND.");
            ShowWindow(hwnd, 0);
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

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SelRegisterName(string selectorName);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void ObjcMsgSend(IntPtr receiver, IntPtr selector, IntPtr sender);
}
