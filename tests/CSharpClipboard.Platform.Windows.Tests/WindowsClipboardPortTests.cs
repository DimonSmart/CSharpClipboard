using CSharpClipboard.Core;
using CSharpClipboard.Platform.Windows;
using System.ComponentModel;

namespace CSharpClipboard.Platform.Windows.Tests;

public sealed class WindowsClipboardPortTests
{
    [Theory]
    [InlineData(0u, "0")]
    [InlineData(42u, "42")]
    [InlineData(uint.MaxValue, "4294967295")]
    public void Revision_uses_the_actual_unsigned_sequence_number(uint sequenceNumber, string expected)
    {
        Assert.Equal(expected, WindowsClipboardPort.CreateRevision(sequenceNumber).Value);
    }

    [Fact]
    public void Read_result_preserves_the_expected_revision_and_exact_text()
    {
        const string text = "first\r\n\0last  ";
        var revision = new ClipboardRevision("42");

        var result = WindowsClipboardPort.CreateReadResult(revision, text, false, false);

        Assert.Equal(revision, result.Revision);
        Assert.Equal(text, result.PlainText);
        Assert.Equal(ClipboardContentFlags.None, result.ContentFlags);
        Assert.Null(result.Source);
    }

    [Theory]
    [InlineData(true, false, ClipboardContentFlags.ContainsFiles)]
    [InlineData(false, true, ClipboardContentFlags.ProtectedOrExcluded)]
    [InlineData(true, true, ClipboardContentFlags.ContainsFiles | ClipboardContentFlags.ProtectedOrExcluded)]
    public void File_drops_and_monitor_exclusions_are_not_capture_eligible(
        bool containsFileDrop,
        bool monitorExcluded,
        ClipboardContentFlags expectedFlags)
    {
        var result = WindowsClipboardPort.CreateReadResult(
            new ClipboardRevision("7"),
            "text",
            containsFileDrop,
            monitorExcluded);

        Assert.Equal(expectedFlags, result.ContentFlags);
        Assert.False(ClipboardCaptureEligibility.TryCreate(result, out _));
    }

    [Fact]
    public void Unsupported_data_has_no_plain_text()
    {
        var result = WindowsClipboardPort.CreateReadResult(new ClipboardRevision("8"), null, false, false);

        Assert.Null(result.PlainText);
        Assert.False(ClipboardCaptureEligibility.TryCreate(result, out _));
    }

    [Fact]
    public async Task Listener_can_be_created_and_disposed_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var port = new WindowsClipboardPort();
        try
        {
            var revision = await port.GetRevisionAsync(CancellationToken.None);
            ClipboardReadResult? result = null;

            try
            {
                result = await port.ReadAsync(revision, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // An external copy may legitimately race this real-OS integration check.
            }
            catch (Win32Exception)
            {
                // Another process may temporarily own the real clipboard.
            }

            Assert.False(string.IsNullOrEmpty(revision.Value));
            Assert.True(result is null || result.Revision == revision);
        }
        finally
        {
            port.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            port.GetRevisionAsync(CancellationToken.None).AsTask());
    }
}
