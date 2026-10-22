using System.Globalization;
using CSharpClipboard.Core;

namespace CSharpClipboard.Services;

public sealed class HistoryPresentation
{
    private volatile List<HistoryEntry> _entries = [];
    public void Open(IReadOnlyList<HistoryEntry> entries) => _entries = [.. entries];
    public void Refresh(IReadOnlyList<HistoryEntry> entries)
    {
        var byId = entries.ToDictionary(entry => entry.Id);
        _entries = [.. _entries.Where(entry => byId.ContainsKey(entry.Id)).Select(entry => byId[entry.Id])];
    }
    public IReadOnlyList<HistoryEntry> Filter(string search) => _entries.Where(entry => string.IsNullOrEmpty(search) ||
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(entry.Text, search, CompareOptions.IgnoreCase) >= 0).ToArray();
}
