using System.Text.Json;
using CSharpClipboard.Core;

namespace CSharpClipboard.Storage;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return default;
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Settings are invalid. The existing file has been preserved.");
            return values.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))! : [];
            values[key] = JsonSerializer.SerializeToElement(value);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, values, cancellationToken: cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
