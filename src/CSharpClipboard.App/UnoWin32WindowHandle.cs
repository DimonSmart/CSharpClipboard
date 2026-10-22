using System.Reflection;

namespace CSharpClipboard.App;

internal static class UnoWin32WindowHandle
{
    internal static IntPtr Resolve(object? native)
    {
        // Uno.UI.NativeElementHosting.Win32NativeWindow exposes the actual HWND.
        // The WinRT.Interop.WindowNative alternative currently exposes an AppWindow ID.
        var property = native?.GetType().GetProperty("Hwnd", BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(IntPtr) ||
            property.GetValue(native) is not IntPtr hwnd || hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Uno native Win32 window HWND is unavailable.");
        return hwnd;
    }
}
