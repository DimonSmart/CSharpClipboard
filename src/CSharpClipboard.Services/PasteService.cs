using CSharpClipboard.Core;

namespace CSharpClipboard.Services;

public enum HistorySelectionAction { CopyOnly, Paste }
public enum HistorySelectionOutcome { CopiedOnly, ShortcutSent, ClipboardChanged, RejectedDuplicate, Failed, Cancelled }

// The original snapshot is never replaced. Closing an unused session releases its native resources;
// closing an accepted session waits for that action to finish releasing them.
public sealed class HistoryInvocation<TSnapshot> : IAsyncDisposable where TSnapshot : class, IPasteTargetSnapshot
{
    private int _acceptedOrClosed;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal HistoryInvocation(object owner, TSnapshot? snapshot) { Owner = owner; Snapshot = snapshot; }
    public Guid Id { get; } = Guid.NewGuid();
    internal object Owner { get; }
    internal TSnapshot? Snapshot { get; }
    internal bool TryAccept() => Interlocked.CompareExchange(ref _acceptedOrClosed, 1, 0) == 0;

    internal async ValueTask ReleaseAsync()
    {
        try
        {
            if (Snapshot is not null) await Snapshot.DisposeAsync().ConfigureAwait(false);
        }
        finally { _released.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (TryAccept()) await ReleaseAsync().ConfigureAwait(false);
        else await _released.Task.ConfigureAwait(false);
    }
}

public sealed class PasteService<TSnapshot>(IPasteTargetPort<TSnapshot> targets, IHistoryPanelPort panel,
    IClipboardPort clipboard, IClipboardWritePort writer, IClipboardWriteCoordinator writes)
    where TSnapshot : class, IPasteTargetSnapshot
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    // UI entry points must call this API to open History: capture precedes any panel activation.
    public async Task<HistoryInvocation<TSnapshot>> OpenHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TSnapshot? snapshot;
            try { snapshot = await targets.GetActiveTargetAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { snapshot = null; }
            var invocation = new HistoryInvocation<TSnapshot>(this, snapshot);
            try { await panel.ShowAsync(invocation.Id, cancellationToken).ConfigureAwait(false); }
            catch { await invocation.DisposeAsync().ConfigureAwait(false); throw; }
            return invocation;
        }
        finally { _operations.Release(); }
    }

    public async Task<HistorySelectionOutcome> SelectAsync(HistoryInvocation<TSnapshot> invocation, string text,
        HistorySelectionAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (!ReferenceEquals(invocation.Owner, this)) throw new ArgumentException("Invocation belongs to another service.", nameof(invocation));
        if (!invocation.TryAccept()) return HistorySelectionOutcome.RejectedDuplicate;

        var entered = false;
        var outcome = HistorySelectionOutcome.Failed;
        try
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // Hand focus back while History still owns activation. Hiding the
            // foreground panel first lets Windows activate an unrelated window,
            // invalidating the original target before we can restore it.
            var restoredBeforeHide = action == HistorySelectionAction.Paste &&
                invocation.Snapshot is { } originalTarget &&
                await targets.TryRestoreAsync(originalTarget, cancellationToken).ConfigureAwait(false);
            await panel.HideAsync(invocation.Id, cancellationToken).ConfigureAwait(false);
            var revision = await writes.WriteTextAsync(writer, text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (restoredBeforeHide && invocation.Snapshot is { } snapshot &&
                await targets.TryRestoreAsync(snapshot, cancellationToken).ConfigureAwait(false))
            {
                outcome = await targets.TryDispatchAsync(snapshot, revision, cancellationToken).ConfigureAwait(false) switch
                {
                    PasteDispatchResult.ShortcutSent => HistorySelectionOutcome.ShortcutSent,
                    PasteDispatchResult.ClipboardChanged => HistorySelectionOutcome.ClipboardChanged,
                    _ => await CopiedOutcomeAsync(revision, cancellationToken).ConfigureAwait(false)
                };
            }
            else outcome = await CopiedOutcomeAsync(revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = HistorySelectionOutcome.Cancelled; }
        catch { outcome = HistorySelectionOutcome.Failed; }
        finally
        {
            try { await invocation.ReleaseAsync().ConfigureAwait(false); }
            catch { outcome = HistorySelectionOutcome.Failed; }
            if (entered) _operations.Release();
        }
        return outcome;
    }

    private async ValueTask<HistorySelectionOutcome> CopiedOutcomeAsync(ClipboardRevision expected, CancellationToken cancellationToken) =>
        await clipboard.GetRevisionAsync(cancellationToken).ConfigureAwait(false) == expected
            ? HistorySelectionOutcome.CopiedOnly : HistorySelectionOutcome.ClipboardChanged;
}
