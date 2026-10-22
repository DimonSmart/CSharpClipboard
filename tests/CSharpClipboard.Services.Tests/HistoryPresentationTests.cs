using CSharpClipboard.Core;
using CSharpClipboard.Services;

namespace CSharpClipboard.Services.Tests;

public sealed class HistoryPresentationTests
{
    [Fact]
    public void Open_snapshot_excludes_new_entries_until_reopening_and_reflects_explicit_changes()
    {
        HistoryEntry Entry(string text) => new(Guid.NewGuid(), text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
        var first = Entry("Ёж Café"); var second = Entry("other"); var third = Entry("new");
        var presentation = new HistoryPresentation();
        presentation.Open([first, second]);
        presentation.Refresh([second with { PinnedAt = DateTimeOffset.UtcNow }, third, first]);
        Assert.Equal(new[] { first.Id, second.Id }, presentation.Filter("").Select(e => e.Id));
        Assert.Empty(presentation.Filter("new"));
        Assert.NotNull(presentation.Filter("")[1].PinnedAt);
        Assert.Single(presentation.Filter("ЁЖ")); Assert.Single(presentation.Filter("CAFÉ"));
        presentation.Open([second, first, third]);
        Assert.Equal(second.Id, presentation.Filter("")[0].Id);
        Assert.Single(presentation.Filter("new"));
        presentation.Refresh([third, first]);
        Assert.Equal(new[] { first.Id, third.Id }, presentation.Filter("").Select(e => e.Id));
    }
}
