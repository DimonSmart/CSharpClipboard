using CSharpClipboard.Core;
using CSharpClipboard.Services;

namespace CSharpClipboard.Services.Tests;

public sealed class PasteServiceTests
{
    [Fact]
    public async Task Snapshot_precedes_show_and_paste_uses_original_snapshot_in_order()
    {
        await using var fixture = new Fixture();
        var session = await fixture.Service.OpenHistoryAsync();
        fixture.Port.NextSnapshot = new Snapshot();
        Assert.Equal(HistorySelectionOutcome.ShortcutSent,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.Equal(new[] { "snapshot", "show", "restore", "hide", "write", "restore", "dispatch", "dispose" }, fixture.Port.Calls);
        Assert.Same(fixture.Port.Restored, fixture.Port.Dispatched);
        Assert.Equal(1, fixture.Port.Shortcuts);
        await session.DisposeAsync();
        Assert.Equal(1, fixture.Port.Dispatched!.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_hands_focus_back_before_hide_and_rechecks_after_clipboard_write(bool changeTargetDuringWrite)
    {
        await using var fixture = new Fixture();
        fixture.Port.LoseTargetOnHideWithoutRestore = true;
        if (changeTargetDuringWrite) fixture.Port.AfterCommit = () => fixture.Port.Refusal = "changed application";
        var session = await fixture.Service.OpenHistoryAsync();

        Assert.Equal(changeTargetDuringWrite ? HistorySelectionOutcome.CopiedOnly : HistorySelectionOutcome.ShortcutSent,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.Equal(changeTargetDuringWrite ? 0 : 1, fixture.Port.Shortcuts);
        Assert.Equal("chosen", fixture.Port.Text);
        Assert.Equal(1, fixture.Port.Writes);
    }

    [Theory]
    [InlineData(" \t\r\n\0😀e\u0301  \n")]
    [InlineData("\r\n\n\t ")]
    public async Task Copy_only_preserves_exact_text_and_never_restores_or_dispatches(string text)
    {
        await using var fixture = new Fixture();
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(session, text, HistorySelectionAction.CopyOnly));
        Assert.Equal(text, fixture.Port.Text);
        Assert.Equal(new[] { "snapshot", "show", "hide", "write", "dispose" }, fixture.Port.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("protected")]
    [InlineData("changed element")]
    [InlineData("changed window")]
    [InlineData("changed application")]
    [InlineData("exited")]
    [InlineData("paste permission denied")]
    [InlineData("untrusted desktop")]
    public async Task Adapter_restore_refusal_means_copy_only(string refusal)
    {
        await using var fixture = new Fixture();
        fixture.Port.Refusal = refusal;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.Equal(0, fixture.Port.Shortcuts);
        Assert.Null(fixture.Port.Dispatched);
        Assert.Equal(1, fixture.Port.Calls.Count(call => call == "restore"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_snapshot_still_opens_and_copies(bool throws)
    {
        await using var fixture = new Fixture();
        fixture.Port.NextSnapshot = null;
        fixture.Port.Failure = throws ? "snapshot" : null;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.DoesNotContain("restore", fixture.Port.Calls);
    }

    [Fact]
    public async Task Final_dispatch_must_recheck_target_after_successful_restore()
    {
        await using var fixture = new Fixture();
        fixture.Port.FinalTargetAvailable = false;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.Contains("dispatch", fixture.Port.Calls);
        Assert.Equal(0, fixture.Port.Shortcuts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clipboard_change_at_final_dispatch_or_copy_completion_is_not_reported_copied(bool copyOnly)
    {
        await using var fixture = new Fixture();
        fixture.Port.ExternalOnWrite = copyOnly;
        fixture.Port.ExternalAtDispatch = !copyOnly;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.ClipboardChanged,
            await fixture.Service.SelectAsync(session, "chosen", copyOnly ? HistorySelectionAction.CopyOnly : HistorySelectionAction.Paste));
        Assert.Equal("external", fixture.Port.Text);
        Assert.Equal(1, fixture.Port.Writes);
        Assert.Equal(0, fixture.Port.Shortcuts);
    }

    [Fact]
    public async Task Duplicate_pointer_and_keyboard_actions_are_rejected_before_waiting()
    {
        await using var fixture = new Fixture();
        fixture.Port.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await fixture.Service.OpenHistoryAsync();
        var pointer = fixture.Service.SelectAsync(session, "first", HistorySelectionAction.Paste);
        await fixture.Port.WriteStarted.Task;
        Assert.Equal(HistorySelectionOutcome.RejectedDuplicate,
            await fixture.Service.SelectAsync(session, "second", HistorySelectionAction.CopyOnly));
        fixture.Port.WriteGate.SetResult();
        Assert.Equal(HistorySelectionOutcome.ShortcutSent, await pointer);
        Assert.Equal(1, fixture.Port.Writes);
        Assert.Equal(1, fixture.Port.Shortcuts);
    }

    [Fact]
    public async Task Concurrent_invocations_serialize_actions_and_keep_their_snapshots()
    {
        await using var fixture = new Fixture();
        var firstSnapshot = fixture.Port.NextSnapshot;
        var first = await fixture.Service.OpenHistoryAsync();
        var secondSnapshot = fixture.Port.NextSnapshot = new Snapshot();
        var second = await fixture.Service.OpenHistoryAsync();
        fixture.Port.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAction = fixture.Service.SelectAsync(first, "first", HistorySelectionAction.Paste);
        await fixture.Port.WriteStarted.Task;
        var secondAction = fixture.Service.SelectAsync(second, "second", HistorySelectionAction.Paste);
        Assert.Equal(1, fixture.Port.Writes);
        fixture.Port.WriteGate.SetResult();
        Assert.Equal(HistorySelectionOutcome.ShortcutSent, await firstAction);
        Assert.Equal(HistorySelectionOutcome.ShortcutSent, await secondAction);
        Assert.Equal(new[] { firstSnapshot, secondSnapshot }, fixture.Port.DispatchSnapshots);
        Assert.Equal(1, fixture.Port.MaximumWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_own_callback_is_suppressed_and_immediate_external_copy_survives(bool externalBeforeWriteReturns)
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        fixture.Port.ExternalOnWrite = externalBeforeWriteReturns;
        var session = await fixture.Service.OpenHistoryAsync();
        await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.CopyOnly);
        if (!externalBeforeWriteReturns) fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Collection(fixture.History.Captures, capture => Assert.Equal("external", capture.Text));
    }

    [Fact]
    public async Task Own_callback_before_return_is_never_read_or_persisted()
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        var session = await fixture.Service.OpenHistoryAsync();
        await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.CopyOnly);
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Equal(0, fixture.Port.Reads);
        Assert.Empty(fixture.History.Captures);
    }

    [Fact]
    public async Task Pause_and_capture_permission_are_independent_of_paste_permission()
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        await fixture.Capture.PauseAsync();
        fixture.Port.ReadForbidden = true;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.ShortcutSent,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Equal(0, fixture.Port.Reads);
        Assert.Empty(fixture.History.Captures);
        fixture.Port.ReadForbidden = false;
        await fixture.Capture.ResumeAsync();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Empty(fixture.History.Captures);
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Single(fixture.History.Captures);
    }

    [Fact]
    public async Task Missing_paste_permission_does_not_disable_capture()
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        fixture.Port.Refusal = "paste permission denied";
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Single(fixture.History.Captures);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("write")]
    [InlineData("restore")]
    [InlineData("dispatch")]
    [InlineData("dispose")]
    public async Task Operational_failure_releases_snapshot_and_preserves_capture_and_saved_history(string stage)
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        fixture.History.Captures.Add(new("saved", null));
        var snapshot = fixture.Port.NextSnapshot!;
        var session = await fixture.Service.OpenHistoryAsync();
        fixture.Port.Failure = stage;
        Assert.Equal(HistorySelectionOutcome.Failed,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste));
        Assert.Equal(1, snapshot.Disposals);
        fixture.Port.Failure = null;
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Equal(new[] { "saved", "external" }, fixture.History.Captures.Select(c => c.Text));
        var next = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(next, "next", HistorySelectionAction.CopyOnly));
    }

    [Fact]
    public async Task Cancellation_during_write_releases_gate_and_snapshot_without_capture_loss()
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        fixture.Port.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = fixture.Port.NextSnapshot!;
        var session = await fixture.Service.OpenHistoryAsync();
        using var cancellation = new CancellationTokenSource();
        var action = fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste, cancellation.Token);
        await fixture.Port.WriteStarted.Task;
        cancellation.Cancel();
        Assert.Equal(HistorySelectionOutcome.Cancelled, await action);
        Assert.Equal(1, snapshot.Disposals);
        fixture.Port.WriteGate = null;
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Single(fixture.History.Captures);
        var next = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.CopiedOnly,
            await fixture.Service.SelectAsync(next, "next", HistorySelectionAction.CopyOnly));
    }

    [Fact]
    public async Task Closing_without_selection_releases_snapshot_and_rejects_late_actions()
    {
        await using var fixture = new Fixture();
        var snapshot = fixture.Port.NextSnapshot!;
        var session = await fixture.Service.OpenHistoryAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(1, snapshot.Disposals);
        Assert.Equal(HistorySelectionOutcome.RejectedDuplicate,
            await fixture.Service.SelectAsync(session, "late", HistorySelectionAction.Paste));
        Assert.Equal(0, fixture.Port.Writes);
    }

    [Fact]
    public async Task Show_failure_disposes_snapshot_and_allows_another_invocation()
    {
        await using var fixture = new Fixture();
        var snapshot = fixture.Port.NextSnapshot!;
        fixture.Port.Failure = "show";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.OpenHistoryAsync());
        Assert.Equal(1, snapshot.Disposals);
        fixture.Port.Failure = null;
        await using var next = await fixture.Service.OpenHistoryAsync();
    }

    [Fact]
    public async Task Cancellation_after_commit_does_not_lose_the_receipt_or_capture_external_copy()
    {
        await using var fixture = new Fixture();
        await fixture.Capture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Port.AfterCommit = cancellation.Cancel;
        var session = await fixture.Service.OpenHistoryAsync();
        Assert.Equal(HistorySelectionOutcome.Cancelled,
            await fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.Paste, cancellation.Token));
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Empty(fixture.History.Captures);
        Assert.Equal(0, fixture.Port.Reads);
        fixture.Port.CopyExternal();
        await fixture.Capture.NotifyClipboardChangedAsync(new(fixture.Port.Revision));
        Assert.Single(fixture.History.Captures);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_another_invocation_does_not_hide_or_write()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.OpenHistoryAsync();
        fixture.Port.NextSnapshot = new Snapshot();
        var second = await fixture.Service.OpenHistoryAsync();
        fixture.Port.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAction = fixture.Service.SelectAsync(first, "first", HistorySelectionAction.Paste);
        await fixture.Port.WriteStarted.Task;
        using var cancellation = new CancellationTokenSource();
        var secondAction = fixture.Service.SelectAsync(second, "second", HistorySelectionAction.Paste, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(HistorySelectionOutcome.Cancelled, await secondAction);
        Assert.Equal(1, fixture.Port.Writes);
        fixture.Port.WriteGate.SetResult();
        Assert.Equal(HistorySelectionOutcome.ShortcutSent, await firstAction);
        Assert.Equal(1, fixture.Port.Calls.Count(c => c == "hide"));
    }

    [Fact]
    public async Task Closing_accepted_session_waits_for_its_action_to_release_snapshot()
    {
        await using var fixture = new Fixture();
        fixture.Port.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = fixture.Port.NextSnapshot!;
        var session = await fixture.Service.OpenHistoryAsync();
        var action = fixture.Service.SelectAsync(session, "chosen", HistorySelectionAction.CopyOnly);
        await fixture.Port.WriteStarted.Task;
        var close = session.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted);
        Assert.Equal(0, snapshot.Disposals);
        fixture.Port.WriteGate.SetResult();
        await action;
        await close;
        Assert.Equal(1, snapshot.Disposals);
    }

    private sealed class Snapshot : IPasteTargetSnapshot
    {
        public int Disposals;
        public Action? Dispose;
        public ValueTask DisposeAsync() { Disposals++; Dispose?.Invoke(); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Port Port { get; } = new();
        public History History { get; } = new();
        public ClipboardCaptureCoordinator Capture { get; }
        public PasteService<Snapshot> Service { get; }
        public Fixture()
        {
            Capture = new(Port, History);
            Service = new(Port, Port, Port, Port, Capture);
        }
        public ValueTask DisposeAsync() => Capture.DisposeAsync();
    }

    private sealed class Port : IPasteTargetPort<Snapshot>, IHistoryPanelPort, IClipboardPort, IClipboardWritePort
    {
        private int _revision;
        private int _activeWrites;
        public event EventHandler<ClipboardSignal>? Changed;
        public ClipboardRevision Revision { get; private set; } = new("0");
        public string Text = "initial";
        public Snapshot? NextSnapshot = new();
        public Snapshot? Restored, Dispatched;
        public List<Snapshot> DispatchSnapshots = [];
        public List<string> Calls = [];
        public string? Failure, Refusal;
        public bool FinalTargetAvailable = true, ExternalOnWrite, ExternalAtDispatch, ReadForbidden;
        public bool LoseTargetOnHideWithoutRestore;
        public int Writes, Reads, Shortcuts, MaximumWrites;
        public TaskCompletionSource WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? WriteGate;
        public Action? AfterCommit;
        private void Call(string stage)
        {
            Calls.Add(stage);
            if (Failure == stage) throw new InvalidOperationException("sensitive native error must not escape");
        }
        public ValueTask<Snapshot?> GetActiveTargetAsync(CancellationToken cancellationToken)
        {
            Call("snapshot");
            if (NextSnapshot is { } snapshot) snapshot.Dispose = () => Call("dispose");
            return ValueTask.FromResult(NextSnapshot);
        }
        public ValueTask ShowAsync(Guid invocationId, CancellationToken cancellationToken) { Call("show"); return ValueTask.CompletedTask; }
        public ValueTask HideAsync(Guid invocationId, CancellationToken cancellationToken)
        {
            Call("hide");
            if (LoseTargetOnHideWithoutRestore && Restored is null) Refusal = "unrelated window activated by hide";
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> TryRestoreAsync(Snapshot snapshot, CancellationToken cancellationToken)
        { Call("restore"); Restored = snapshot; return ValueTask.FromResult(Refusal is null); }
        public ValueTask<PasteDispatchResult> TryDispatchAsync(Snapshot snapshot, ClipboardRevision expectedRevision, CancellationToken cancellationToken)
        {
            Call("dispatch"); Dispatched = snapshot; DispatchSnapshots.Add(snapshot);
            if (ExternalAtDispatch) CopyExternal();
            if (Revision != expectedRevision) return ValueTask.FromResult(PasteDispatchResult.ClipboardChanged);
            if (!FinalTargetAvailable) return ValueTask.FromResult(PasteDispatchResult.TargetUnavailable);
            Shortcuts++;
            return ValueTask.FromResult(PasteDispatchResult.ShortcutSent);
        }
        public ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Revision);
        public ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision expectedRevision, CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadForbidden) throw new InvalidOperationException();
            return ValueTask.FromResult(new ClipboardReadResult(expectedRevision, 1, Text, null, ClipboardContentFlags.None));
        }
        public async ValueTask<ClipboardRevision> WriteTextAsync(string text, CancellationToken cancellationToken)
        {
            Call("write"); Writes++;
            MaximumWrites = Math.Max(MaximumWrites, ++_activeWrites);
            try
            {
                WriteStarted.TrySetResult();
                if (WriteGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                Text = text;
                var committed = Revision = new((++_revision).ToString());
                Changed?.Invoke(this, new(Revision));
                AfterCommit?.Invoke();
                if (ExternalOnWrite) CopyExternal();
                return committed;
            }
            finally { _activeWrites--; }
        }
        public void CopyExternal()
        {
            Text = "external";
            Revision = new((++_revision).ToString());
            Changed?.Invoke(this, new(Revision));
        }
    }

    private sealed class History : IHistoryRepository
    {
        public List<HistoryCapture> Captures = [];
        public ValueTask UpsertCaptureAsync(HistoryCapture capture, CancellationToken cancellationToken) { Captures.Add(capture); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<HistoryEntry>> GetEntriesAsync(string? searchText, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HistoryEntry>>([]);
        public ValueTask<bool> PinAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> UnpinAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask ClearRecentAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ClearAllAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
