namespace CSharpClipboard.App.Tests;

public sealed class WindowVisibilityTests
{
    [Fact]
    public void Reopening_a_hidden_window_shows_it_natively_before_reactivation()
    {
        var window = new object();
        var events = new List<string>();
        var tracker = new WindowVisibilityTracker<object>(
            activate: _ => events.Add("activate"),
            showAfterHide: _ => events.Add("native-show"),
            hide: _ => events.Add("native-hide"));

        tracker.Show(window);
        Assert.True(tracker.IsVisible(window));
        tracker.Hide(window);
        Assert.False(tracker.IsVisible(window));
        tracker.Show(window);
        Assert.True(tracker.IsVisible(window));
        tracker.Show(window);
        tracker.Hide(window);
        tracker.Show(window);

        Assert.Equal(
            new[] { "activate", "native-hide", "native-show", "activate",
                    "activate", "native-hide", "native-show", "activate" },
            events);
    }

    [Fact]
    public void Failed_native_show_keeps_window_hidden_and_allows_retry()
    {
        var window = new object();
        var nativeShows = 0;
        var tracker = new WindowVisibilityTracker<object>(
            activate: _ => { },
            showAfterHide: _ =>
            {
                nativeShows++;
                if (nativeShows == 1) throw new InvalidOperationException("SW_SHOW failed.");
            },
            hide: _ => { });

        tracker.Show(window);
        tracker.Hide(window);
        Assert.Throws<InvalidOperationException>(() => tracker.Show(window));
        Assert.False(tracker.IsVisible(window));
        tracker.Show(window);
        Assert.True(tracker.IsVisible(window));
        Assert.Equal(2, nativeShows);
    }

    [Fact]
    public void Hiding_removes_visibility_only_after_native_hide_succeeds()
    {
        var window = new object();
        var attempts = 0;
        var tracker = new WindowVisibilityTracker<object>(
            activate: _ => { },
            showAfterHide: _ => { },
            hide: _ =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("Native hide failed.");
            });

        tracker.Show(window);
        Assert.Throws<InvalidOperationException>(() => tracker.Hide(window));
        Assert.True(tracker.IsVisible(window));

        tracker.Hide(window);
        Assert.False(tracker.IsVisible(window));
        tracker.Hide(window);
        Assert.Equal(2, attempts);

        tracker.Show(window);
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
        tracker = new WindowVisibilityTracker<object>(
            activate: _ => { },
            showAfterHide: _ => { },
            hide: w =>
            {
                nativeHides++;
                Assert.True(tracker!.IsVisible(w));
                tracker.Hide(w); // Deactivation during ShowWindow(SW_HIDE).
            });
        tracker.Show(window);
        tracker.Hide(window);
        Assert.Equal(1, nativeHides);
        Assert.False(tracker.IsVisible(window));
        tracker.Show(window);
        tracker.Hide(window);
        Assert.Equal(2, nativeHides);
    }

    [Fact]
    public void Reentrant_activation_does_not_duplicate_first_or_native_show()
    {
        var window = new object();
        var activations = 0;
        var nativeShows = 0;
        WindowVisibilityTracker<object>? tracker = null;
        tracker = new WindowVisibilityTracker<object>(
            activate: w => { activations++; tracker!.Show(w); },
            showAfterHide: _ => nativeShows++,
            hide: _ => { });
        tracker.Show(window);
        tracker.Hide(window);
        tracker.Show(window);
        Assert.Equal(2, activations);
        Assert.Equal(1, nativeShows);
        Assert.True(tracker.IsVisible(window));
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
