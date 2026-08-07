using System.Windows;
using System.Windows.Controls;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

public partial class UserSelectDialog : UserControl
{
    private readonly string[] _choices;
    private readonly Action<int, string> _onSelected;

    public event Action? DismissRequested;

    public UserSelectDialog(string[] choices, Action<int, string> onSelected, string messageOnEmpty)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(onSelected);

        InitializeComponent();
        _choices = choices;
        _onSelected = onSelected;

        if (_choices.Length == 0)
        {
            MessageText.Text = messageOnEmpty;
            MessageText.Visibility = Visibility.Visible;
            ChoiceList.Visibility = Visibility.Collapsed;
            GoButton.Visibility = Visibility.Collapsed;
            ExitButton.Visibility = Visibility.Visible;
            Title.Visibility = Visibility.Collapsed;
            return;
        }

        ChoiceList.ItemsSource = _choices;
        ChoiceList.SelectedIndex = 0;
        UpdateGoButtonText();
    }

    private void ChoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateGoButtonText();

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        var index = ChoiceList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _onSelected(index, _choices[index]);
        DismissRequested?.Invoke();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void UpdateGoButtonText()
    {
        var index = ChoiceList.SelectedIndex;
        GoButton.Content = index >= 0 ? $"{I18n.Format("SelectUserKeyButton", _choices[index])}" : $"{I18n.Format("SelectUserKeyButton", "???")}";
    }
}
