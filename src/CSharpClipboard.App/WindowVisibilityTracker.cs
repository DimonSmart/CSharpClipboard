namespace CSharpClipboard.App;

// Keeps the logical visibility state until the native hide has actually succeeded.
// A hide can synchronously raise deactivation, so recursive requests must be ignored.
internal sealed class WindowVisibilityTracker<T>(Action<T> hide) where T : class
{
    private readonly HashSet<T> _visible = [];
    private readonly HashSet<T> _hiding = [];

    internal void MarkVisible(T window) => _visible.Add(window);
    internal bool IsVisible(T? window) => window is not null && _visible.Contains(window);

    internal void Hide(T window)
    {
        if (!_visible.Contains(window) || !_hiding.Add(window)) return;
        try
        {
            hide(window);
            _visible.Remove(window);
        }
        finally { _hiding.Remove(window); }
    }

    internal void Clear()
    {
        _visible.Clear();
        _hiding.Clear();
    }
}
