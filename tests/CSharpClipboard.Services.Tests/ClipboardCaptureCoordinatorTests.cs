using CSharpClipboard.Core;
using CSharpClipboard.Services;

namespace CSharpClipboard.Services.Tests;

public sealed class ClipboardCaptureCoordinatorTests
{
    [Fact]
    public async Task Exclusions_apply_to_exact_known_sources_and_do_not_block_unknown_sources()
    {
        var clipboard = new FakeClipboard("baseline") { Source = new ClipboardSource("excluded.app") };
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history) { IsSourceExcluded = id => id == "excluded.app" };
        await coordinator.StartAsync();
        clipboard.Revision = new("one"); await coordinator.NotifyClipboardChangedAsync(new(clipboard.Revision));
        Assert.Empty(history.Captures);
        clipboard.Source = null; clipboard.Revision = new("two");
        await coordinator.NotifyClipboardChangedAsync(new(clipboard.Revision));
        Assert.Single(history.Captures);
    }
    [Fact]
    public async Task Start_and_resume_only_set_the_baseline()
    {
        var clipboard = new FakeClipboard("existing");
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);

        await coordinator.StartAsync();
        clipboard.Revision = new ClipboardRevision("later");
        await coordinator.ResumeAsync();

        Assert.Equal(0, clipboard.ReadCount);
        Assert.Empty(history.Captures);
    }

    [Fact]
    public async Task Pause_skips_signals_without_reading_or_writing()
    {
        var clipboard = new FakeClipboard("baseline");
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);
        await coordinator.StartAsync();
        await coordinator.PauseAsync();

        clipboard.Revision = new ClipboardRevision("copy");
        await coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(clipboard.Revision));

        Assert.Equal(0, clipboard.ReadCount);
        Assert.Empty(history.Captures);
    }

    [Fact]
    public async Task Capture_operations_are_serialized()
    {
        var clipboard = new FakeClipboard("baseline") { BlockReads = true };
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);
        await coordinator.StartAsync();
        clipboard.Revision = new ClipboardRevision("one");

        var first = coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(clipboard.Revision));
        await clipboard.ReadStarted.Task;
        var second = coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(clipboard.Revision));
        clipboard.AllowRead.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, clipboard.MaximumConcurrentReads);
    }

    [Fact]
    public async Task Revision_race_does_not_write_history()
    {
        var clipboard = new FakeClipboard("baseline");
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);
        await coordinator.StartAsync();
        clipboard.Revision = new ClipboardRevision("copy");
        clipboard.ChangeRevisionAfterRead = new ClipboardRevision("newer");

        await coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(new ClipboardRevision("copy")));

        Assert.Empty(history.Captures);
    }

    [Fact]
    public async Task Read_failure_does_not_write_history()
    {
        var clipboard = new FakeClipboard("baseline") { ThrowOnRead = true };
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);
        await coordinator.StartAsync();
        clipboard.Revision = new ClipboardRevision("copy");

        await coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(clipboard.Revision));

        Assert.Empty(history.Captures);
    }

    [Fact]
    public async Task Persists_exact_text()
    {
        const string text = "line 1\r\n\0😀  \r\n";
        var clipboard = new FakeClipboard("baseline") { Text = text };
        var history = new FakeHistoryRepository();
        await using var coordinator = new ClipboardCaptureCoordinator(clipboard, history);
        await coordinator.StartAsync();
        clipboard.Revision = new ClipboardRevision("copy");

        await coordinator.NotifyClipboardChangedAsync(new ClipboardSignal(clipboard.Revision));

        Assert.Collection(history.Captures, capture => Assert.True(PreservesExactText(text, capture.Text)));
    }

    private sealed class FakeClipboard(string revision) : IClipboardPort
    {
        private int _concurrentReads;

        public event EventHandler<ClipboardSignal>? Changed;

        public ClipboardRevision Revision { get; set; } = new(revision);

        public string Text { get; set; } = "text";
        public ClipboardSource? Source { get; set; }

        public int ReadCount { get; private set; }

        public int MaximumConcurrentReads { get; private set; }

        public bool BlockReads { get; init; }

        public bool ThrowOnRead { get; init; }

        public ClipboardRevision? ChangeRevisionAfterRead { get; set; }

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Revision);

        public async ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision expectedRevision, CancellationToken cancellationToken)
        {
            ReadCount++;
            var concurrentReads = Interlocked.Increment(ref _concurrentReads);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, concurrentReads);
            try
            {
                ReadStarted.TrySetResult();
                if (BlockReads)
                {
                    await AllowRead.Task.WaitAsync(cancellationToken);
                }

                if (ThrowOnRead)
                {
                    throw new InvalidOperationException();
                }

                return new ClipboardReadResult(expectedRevision, 1, Text, Source, ClipboardContentFlags.None);
            }
            finally
            {
                if (ChangeRevisionAfterRead is { } changed)
                {
                    Revision = changed;
                    ChangeRevisionAfterRead = null;
                }

                Interlocked.Decrement(ref _concurrentReads);
            }
        }

        public void RaiseChanged() => Changed?.Invoke(this, new ClipboardSignal(Revision));
    }

    private sealed class FakeHistoryRepository : IHistoryRepository
    {
        public List<HistoryCapture> Captures { get; } = [];

        public ValueTask UpsertCaptureAsync(HistoryCapture capture, CancellationToken cancellationToken)
        {
            Captures.Add(capture);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<HistoryEntry>> GetEntriesAsync(string? searchText, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<HistoryEntry>>([]);

        public ValueTask<bool> PinAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> UnpinAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask ClearRecentAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ClearAllAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static bool PreservesExactText(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal);
}
