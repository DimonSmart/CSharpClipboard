namespace CSharpClipboard.App;

// Selection arithmetic stays independent of Uno focus and rendering.
internal static class HistoryListNavigation
{
    internal static int NextIndex(int count, int currentIndex, int delta)
    {
        if (count <= 0) return -1;
        if (currentIndex < 0) return 0;
        return Math.Clamp(currentIndex + delta, 0, count - 1);
    }
}
