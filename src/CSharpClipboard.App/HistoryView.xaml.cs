using Microsoft.UI.Xaml.Controls;

namespace CSharpClipboard.App;

public sealed partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    internal TextBox SearchBox => SearchBoxInner;
    internal ListView HistoryList => HistoryListInner;
    internal TextBlock EmptyMessage => EmptyMessageInner;
    internal Button ClearButton => ClearButtonInner;
    internal Button SettingsButton => SettingsButtonInner;
    internal TextBlock KeyboardHints => KeyboardHintsInner;
    internal TextBlock StatusLabel => StatusLabelInner;
    internal TextBlock FeedbackLabel => FeedbackLabelInner;
}
