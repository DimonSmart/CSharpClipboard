using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.Windows.Tests;

public sealed class WindowsFocusRestoreWaiterTests
{
    [Fact]
    public async Task Transient_unfocused_state_can_settle_to_exact_original_target()
    {
        var observations = 0;
        var restored = await WindowsFocusRestoreWaiter.WaitAsync(
            () => ++observations == 3 ? WindowsFocusRestoreState.Matched : WindowsFocusRestoreState.Pending,
            CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

        Assert.True(restored);
        Assert.Equal(3, observations);
    }

    [Fact]
    public async Task Different_focus_is_rejected_without_waiting_or_retrying()
    {
        var observations = 0;
        var restored = await WindowsFocusRestoreWaiter.WaitAsync(
            () => { observations++; return WindowsFocusRestoreState.Rejected; },
            CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

        Assert.False(restored);
        Assert.Equal(1, observations);
    }

    [Fact]
    public async Task Transient_foreground_never_settles_and_returns_copy_only()
    {
        var restored = await WindowsFocusRestoreWaiter.WaitAsync(
            () => WindowsFocusRestoreState.Pending,
            CancellationToken.None, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));

        Assert.False(restored);
    }

    [Fact]
    public async Task Cancellation_never_allows_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsFocusRestoreWaiter.WaitAsync(
                () => WindowsFocusRestoreState.Matched, cancellation.Token));
    }

    [Theory]
    [InlineData(0u, PasteDispatchResult.TargetUnavailable)]
    [InlineData(1u, PasteDispatchResult.TargetUnavailable)]
    [InlineData(2u, PasteDispatchResult.TargetUnavailable)]
    [InlineData(3u, PasteDispatchResult.TargetUnavailable)]
    [InlineData(4u, PasteDispatchResult.ShortcutSent)]
    public void Shortcut_is_reported_sent_only_if_all_four_key_events_were_sent(
        uint sent, PasteDispatchResult expected)
    {
        Assert.Equal(expected, WindowsPasteSendResult.FromSentCount(sent, 4));
    }
}
