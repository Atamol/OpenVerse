using System.Windows;
using System.Windows.Controls;

namespace OpenVerse.Decker.View;

public partial class ConfirmDialog : UserControl
{
    public event Action? Confirmed;
    public event Action? Cancelled;

    /// <summary>
    /// Labels default to the shared OK/Cancel strings; pass them when the two choices are not a
    /// yes and a no, so the buttons can say what they actually do.
    /// </summary>
    public ConfirmDialog(string message, string? confirmLabel = null, string? cancelLabel = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        if (confirmLabel is not null)
        {
            OkButton.Content = confirmLabel;
        }
        if (cancelLabel is not null)
        {
            CancelButton.Content = cancelLabel;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirmed?.Invoke();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}
