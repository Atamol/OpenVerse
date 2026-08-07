using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenVerse.Decker.View;

public partial class FocusedScreen : UserControl
{
    private bool _canDismiss;

    public FocusedScreen()
    {
        InitializeComponent();
    }

    public void Show(UserControl content, bool canDismiss = true)
    {
        _canDismiss = canDismiss;
        ContentHost.Content = content;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        ContentHost.Content = null;
        _canDismiss = true;
    }

    private void Shade_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Shade_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_canDismiss)
        {
            Hide();
        }
    }

    private void ContentBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void ContentBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
