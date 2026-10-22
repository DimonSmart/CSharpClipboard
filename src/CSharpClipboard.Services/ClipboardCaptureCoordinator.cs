using System.Threading.Channels;
using CSharpClipboard.Core;

namespace CSharpClipboard.Services;

public interface IClipboardCaptureCoordinator : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
}

public sealed class ClipboardCaptureCoordinator : IClipboardCaptureCoordinator, IClipboardWriteCoordinator
{
    private readonly IClipboardPort _clipboard;
    private readonly IHistoryRepository _history;
    private readonly Channel<ICommand> _commands = Channel.CreateUnbounded<ICommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _processor;
    private bool _isCapturing;
    private ClipboardRevision _baselineRevision = ClipboardRevision.None;
    public Func<string, bool> IsSourceExcluded { get; set; } = static _ => false;
    public event Action<string>? StatusChanged;

    public ClipboardCaptureCoordinator(IClipboardPort clipboard, IHistoryRepository history)
    {
        _clipboard = clipboard;
        _history = history;
        _clipboard.Changed += OnClipboardChanged;
        _processor = Task.Run(ProcessAsync);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(new SetBaselineCommand(cancellationToken));

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(new SetBaselineCommand(cancellationToken));

    public Task PauseAsync() => EnqueueAsync(new PauseCommand());

    public Task NotifyClipboardChangedAsync(ClipboardSignal signal) =>
        EnqueueAsync(new CaptureCommand(signal));

    public async Task<ClipboardRevision> WriteTextAsync(IClipboardWritePort writer, string text,
        CancellationToken cancellationToken = default)
    {
        var command = new WriteCommand(writer, text, cancellationToken);
        await EnqueueAsync(command).ConfigureAwait(false);
        return command.Revision;
    }

    public async ValueTask DisposeAsync()
    {
        _clipboard.Changed -= OnClipboardChanged;
        _commands.Writer.TryComplete();
        _shutdown.Cancel();
        await _processor.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void OnClipboardChanged(object? sender, ClipboardSignal signal)
    {
        _ = NotifyClipboardChangedAsync(signal);
    }

    private Task EnqueueAsync(ICommand command)
    {
        if (!_commands.Writer.TryWrite(command))
        {
            return Task.FromException(new ObjectDisposedException(nameof(ClipboardCaptureCoordinator)));
        }

        return command.Completion.Task;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await command.ExecuteAsync(this).ConfigureAwait(false);
                    command.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    command.Completion.TrySetCanceled(_shutdown.Token);
                }
                catch (Exception exception)
                {
                    StatusChanged?.Invoke($"Capture operation failed ({exception.GetType().Name}). History is preserved.");
                    command.Completion.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            while (_commands.Reader.TryRead(out var pending))
                pending.Completion.TrySetCanceled(_shutdown.Token);
        }
    }

    private async ValueTask SetBaselineAsync(CancellationToken cancellationToken)
    {
        _baselineRevision = await _clipboard.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
        _isCapturing = true;
    }

    private void Pause()
    {
        _isCapturing = false;
        _baselineRevision = ClipboardRevision.None;
    }

    private async ValueTask CaptureAsync(ClipboardSignal signal)
    {
        if (!_isCapturing)
        {
            return;
        }

        var beforeRead = await _clipboard.GetRevisionAsync(_shutdown.Token).ConfigureAwait(false);
        if (signal.Revision != beforeRead || beforeRead == _baselineRevision)
        {
            return;
        }

        ClipboardReadResult sample;
        try
        {
            sample = await _clipboard.ReadAsync(beforeRead, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        var afterRead = await _clipboard.GetRevisionAsync(_shutdown.Token).ConfigureAwait(false);
        if (!_isCapturing || beforeRead != afterRead || sample.Revision != beforeRead)
        {
            return;
        }

        if (ClipboardCaptureEligibility.TryCreate(sample, out var capture))
        {
            if (capture!.Source is { } source && IsSourceExcluded(source.Identifier)) return;
            await _history.UpsertCaptureAsync(capture!, _shutdown.Token).ConfigureAwait(false);
            _baselineRevision = beforeRead;
        }
    }

    private interface ICommand
    {
        TaskCompletionSource Completion { get; }

        ValueTask ExecuteAsync(ClipboardCaptureCoordinator coordinator);
    }

    private sealed class SetBaselineCommand(CancellationToken cancellationToken) : ICommand
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ExecuteAsync(ClipboardCaptureCoordinator coordinator) => coordinator.SetBaselineAsync(cancellationToken);
    }

    private sealed class PauseCommand : ICommand
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ExecuteAsync(ClipboardCaptureCoordinator coordinator)
        {
            coordinator.Pause();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CaptureCommand(ClipboardSignal signal) : ICommand
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ExecuteAsync(ClipboardCaptureCoordinator coordinator) => coordinator.CaptureAsync(signal);
    }

    private sealed class WriteCommand(IClipboardWritePort writer, string text, CancellationToken cancellationToken) : ICommand
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClipboardRevision Revision { get; private set; }

        public async ValueTask ExecuteAsync(ClipboardCaptureCoordinator coordinator)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, coordinator._shutdown.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Revision = await writer.WriteTextAsync(text, cancellation.Token).ConfigureAwait(false);
            coordinator._baselineRevision = Revision;
        }
    }
}
