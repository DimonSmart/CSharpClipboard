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
            // Uno's WindowNative.GetWindowHandle returns an AppWindow ID, not a Win32 HWND.
            var native = Uno.UI.Xaml.WindowHelper.GetNativeWindow(window);
            var hwnd = UnoWin32WindowHandle.Resolve(native);
            if (!IsWindow(hwnd)) throw new InvalidOperationException("Uno returned an invalid Win32 HWND.");
            _ = ShowWindow(hwnd, 0); // The return value is the PREVIOUS visibility, not success.
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
