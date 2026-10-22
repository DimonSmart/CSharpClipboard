namespace CSharpClipboard.App;

// Uno shows a native window only on its first activation. After SW_HIDE we must
// explicitly restore native visibility before calling Window.Activate() again.
internal sealed class WindowVisibilityTracker<T>(
    Action<T> activate,
    Action<T> showAfterHide,
    Action<T> hide) where T : class
{
    private readonly HashSet<T> _visible = [];
    private readonly HashSet<T> _shownOnce = [];
    private readonly HashSet<T> _showing = [];
    private readonly HashSet<T> _hiding = [];

    internal bool IsVisible(T? window) => window is not null && _visible.Contains(window);

    internal void Show(T window)
    {
        if (!_showing.Add(window)) return;
        try
        {
            // A normal first activation allows Uno to initialize/render the window.
            // Only reactivation after a native hide needs a second native ShowWindow.
            if (_shownOnce.Contains(window) && !_visible.Contains(window))
                showAfterHide(window);
            activate(window);
            _shownOnce.Add(window);
            _visible.Add(window);
        }
        finally { _showing.Remove(window); }
    }

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
        _shownOnce.Clear();
        _showing.Clear();
        _hiding.Clear();
    }
}
