using System.Text;

namespace CSharpClipboard.Core;

public static class ClipboardCaptureEligibility
{
    public const int MaximumUtf8Bytes = 1_048_576;

    private const ClipboardContentFlags ExcludedFlags =
        ClipboardContentFlags.ContainsFiles |
        ClipboardContentFlags.Composite |
        ClipboardContentFlags.ProtectedOrExcluded |
        ClipboardContentFlags.Transient |
        ClipboardContentFlags.Generated;

    public static bool TryCreate(ClipboardReadResult sample, out HistoryCapture? capture)
    {
        capture = null;

        if (sample.ItemCount != 1 || sample.PlainText is null || sample.PlainText.Length == 0)
        {
            return false;
        }

        if ((sample.ContentFlags & ExcludedFlags) != ClipboardContentFlags.None)
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(sample.PlainText) > MaximumUtf8Bytes)
        {
            return false;
        }

        capture = new HistoryCapture(sample.PlainText, sample.Source);
        return true;
    }
}
