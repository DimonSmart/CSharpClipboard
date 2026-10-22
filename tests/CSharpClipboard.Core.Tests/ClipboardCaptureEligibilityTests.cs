using CSharpClipboard.Core;

namespace CSharpClipboard.Core.Tests;

public sealed class ClipboardCaptureEligibilityTests
{
    [Fact]
    public void Accepts_exact_text_without_transformation()
    {
        const string text = "first\r\n\0emoji 😀  \r\n";

        var accepted = ClipboardCaptureEligibility.TryCreate(Sample(text), out var capture);

        Assert.True(accepted);
        Assert.NotNull(capture);
        Assert.True(PreservesExactText(text, capture.Text));
    }

    [Fact]
    public void Accepts_whitespace_only_text()
    {
        Assert.True(ClipboardCaptureEligibility.TryCreate(Sample(" \t\r\n"), out var capture));
        Assert.True(PreservesExactText(" \t\r\n", capture!.Text));
    }

    [Theory]
    [InlineData(null, 1, ClipboardContentFlags.None)]
    [InlineData("", 1, ClipboardContentFlags.None)]
    [InlineData("text", 0, ClipboardContentFlags.None)]
    [InlineData("text", 2, ClipboardContentFlags.None)]
    [InlineData("text", 1, ClipboardContentFlags.ContainsFiles)]
    [InlineData("text", 1, ClipboardContentFlags.Composite)]
    [InlineData("text", 1, ClipboardContentFlags.ProtectedOrExcluded)]
    [InlineData("text", 1, ClipboardContentFlags.Transient)]
    [InlineData("text", 1, ClipboardContentFlags.Generated)]
    public void Rejects_excluded_content(string? text, int itemCount, ClipboardContentFlags flags)
    {
        Assert.False(ClipboardCaptureEligibility.TryCreate(Sample(text, itemCount, flags), out _));
    }

    [Fact]
    public void Accepts_exactly_one_mib_of_utf8()
    {
        Assert.True(ClipboardCaptureEligibility.TryCreate(Sample(new string('a', ClipboardCaptureEligibility.MaximumUtf8Bytes)), out _));
    }

    [Fact]
    public void Rejects_text_larger_than_one_mib_of_utf8()
    {
        Assert.False(ClipboardCaptureEligibility.TryCreate(Sample(new string('a', ClipboardCaptureEligibility.MaximumUtf8Bytes + 1)), out _));
    }

    private static ClipboardReadResult Sample(
        string? text,
        int itemCount = 1,
        ClipboardContentFlags flags = ClipboardContentFlags.None) =>
        new(new ClipboardRevision("1"), itemCount, text, null, flags);

    private static bool PreservesExactText(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal);
}
