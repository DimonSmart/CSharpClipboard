namespace CSharpClipboard.App.Tests;

public sealed class HistoryListNavigationTests
{
    [Theory]
    [InlineData(0, -1, 1, -1)]
    [InlineData(0, 0, -1, -1)]
    [InlineData(1, -1, 1, 0)]
    [InlineData(1, 0, -1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(5, -1, -1, 0)]
    [InlineData(5, -1, 1, 0)]
    [InlineData(5, 0, 1, 1)]
    [InlineData(5, 3, -1, 2)]
    [InlineData(5, 4, 1, 4)]
    [InlineData(5, 0, -1, 0)]
    public void Arrow_navigation_selects_adjacent_item_and_clamps_at_edges(
        int count, int currentIndex, int delta, int expected)
    {
        Assert.Equal(expected, HistoryListNavigation.NextIndex(count, currentIndex, delta));
    }
}
