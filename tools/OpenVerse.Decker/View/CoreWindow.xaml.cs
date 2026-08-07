using System.Windows;
using System.Windows.Controls;

namespace OpenVerse.Decker.View;

/// <summary>
/// deck editor without any restrictions like numbers of a card, clan, resurgent, etc.<br/>
/// this application is necessary to accomplish such a freedom.
///
/// core windows shows these user control in order.
/// 1. PathSetupScreen: if required paths to some files are not valid, set them up first
/// 2. InitialScreen: set up some basic settings like language
/// 3. DeckListScreen: list up all the decks in openverse.db. you can add new decks too.
/// 4. DeckEditScreen: edit a deck without any restrictions. you can go back to DeckListScreen with saving the deck or canceling it.
/// </summary>
public partial class CoreWindow : Window
{
    public CoreWindow()
    {
        InitializeComponent();
        NavigateToStartScreen();
    }

    private void NavigateToStartScreen()
    {
        if (!PathSetupScreen.PathsAreValid())
        {
            ShowScreen(new PathSetupScreen(this));
        }
        else
        {
            ShowScreen(new InitialScreen(this));
        }
    }

    public void ShowScreen(UserControl screen) => ScreenHost.Content = screen;

    public void ShowFocused(UserControl content, bool canDismiss = true)
    {
        if (content is UserSelectDialog dialog)
        {
            dialog.DismissRequested += HideFocused;
            canDismiss = false;
        }
        Focused.Show(content, canDismiss);
    }

    public void HideFocused() => Focused.Hide();
}
