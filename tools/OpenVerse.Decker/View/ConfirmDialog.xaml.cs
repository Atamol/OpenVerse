using System.Windows;
using System.Windows.Controls;

namespace OpenVerse.Decker.View;

public partial class ConfirmDialog : UserControl
{
    public event Action? Confirmed;
    public event Action? Cancelled;

    public ConfirmDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirmed?.Invoke();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}
