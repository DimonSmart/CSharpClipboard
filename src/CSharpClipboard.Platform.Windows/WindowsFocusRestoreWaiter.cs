using System.Diagnostics;
using CSharpClipboard.Core;

namespace CSharpClipboard.Platform.Windows;

internal enum WindowsFocusRestoreState { Matched, Pending, Rejected }

// UIA/focus can lag a successful SetForegroundWindow. The bounded wait never
// approves a different window/control; the native port checks those identities
// on every probe and checks them again immediately before SendInput.
internal static class WindowsFocusRestoreWaiter
{
    internal static async Task<bool> WaitAsync(
        Func<WindowsFocusRestoreState> check,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var limit = timeout ?? TimeSpan.FromMilliseconds(450);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(30);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await Task.Run(check, cancellationToken).ConfigureAwait(false);
            if (state == WindowsFocusRestoreState.Matched) return true;
            if (state == WindowsFocusRestoreState.Rejected || elapsed.Elapsed >= limit) return false;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class WindowsPasteSendResult
{
    internal static PasteDispatchResult FromSentCount(uint sent, uint expected) =>
        sent == expected ? PasteDispatchResult.ShortcutSent : PasteDispatchResult.TargetUnavailable;
}
