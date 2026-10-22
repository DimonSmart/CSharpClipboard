using System.IO.Pipes;

namespace CSharpClipboard.App;

internal sealed class SingleInstanceLease : IDisposable
{
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private readonly object _activationGate = new();
    private Action? _activated;
    private bool _pendingActivation;
    public event Action? Activated
    {
        add
        {
            bool pending;
            lock (_activationGate) { _activated += value; pending = _pendingActivation; _pendingActivation = false; }
            if (pending) value?.Invoke();
        }
        remove { lock (_activationGate) _activated -= value; }
    }
    private SingleInstanceLease(FileStream file, string pipeName)
    {
        _lock = file;
        _pipeName = pipeName;
        _ = ListenAsync();
    }
    public static async Task<SingleInstanceLease?> AcquireAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..24];
        var pipe = "CSharpClipboard." + hash;
        try { return new SingleInstanceLease(new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), pipe); }
        catch (IOException)
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            try { await client.ConnectAsync(1500); await client.WriteAsync(new byte[] { 1 }); } catch (Exception error) when (error is IOException or TimeoutException) { }
            return null;
        }
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, _stop.Token) == 1)
                {
                    Action? activated;
                    lock (_activationGate) { activated = _activated; if (activated is null) _pendingActivation = true; }
                    activated?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { _stop.Cancel(); _lock.Dispose(); }
}
