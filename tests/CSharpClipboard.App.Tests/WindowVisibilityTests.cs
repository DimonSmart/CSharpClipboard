namespace CSharpClipboard.App.Tests;

public sealed class WindowVisibilityTests
{
    [Fact]
    public void Hiding_removes_visibility_only_after_native_hide_succeeds()
    {
        var window = new object();
        var attempts = 0;
        var tracker = new WindowVisibilityTracker<object>(_ =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("Native hide failed.");
        });

        tracker.MarkVisible(window);
        Assert.Throws<InvalidOperationException>(() => tracker.Hide(window));
        Assert.True(tracker.IsVisible(window));

        tracker.Hide(window);
        Assert.False(tracker.IsVisible(window));
        tracker.Hide(window);
        Assert.Equal(2, attempts);

        tracker.MarkVisible(window);
        Assert.True(tracker.IsVisible(window));
        tracker.Hide(window);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Hiding_ignores_reentrant_deactivation_and_recovers_for_next_show()
    {
        var window = new object();
        var nativeHides = 0;
        WindowVisibilityTracker<object>? tracker = null;
        tracker = new WindowVisibilityTracker<object>(w =>
        {
            nativeHides++;
            Assert.True(tracker!.IsVisible(w));
            tracker.Hide(w); // Deactivation during ShowWindow(SW_HIDE).
        });
        tracker.MarkVisible(window);
        tracker.Hide(window);
        Assert.Equal(1, nativeHides);
        Assert.False(tracker.IsVisible(window));
        tracker.MarkVisible(window);
        tracker.Hide(window);
        Assert.Equal(2, nativeHides);
    }

    [Fact]
    public void Resolves_native_Hwnd_and_rejects_Uno_ID_only_handles()
    {
        Assert.Equal(new IntPtr(123), UnoWin32WindowHandle.Resolve(new NativePeer { Hwnd = new IntPtr(123) }));
        Assert.Throws<InvalidOperationException>(() => UnoWin32WindowHandle.Resolve(null));
        Assert.Throws<InvalidOperationException>(() => UnoWin32WindowHandle.Resolve(new { Id = 123 }));
        Assert.Throws<InvalidOperationException>(() => UnoWin32WindowHandle.Resolve(new NativePeer()));
    }

    private sealed class NativePeer
    {
        public IntPtr Hwnd { get; init; }
    }
}
