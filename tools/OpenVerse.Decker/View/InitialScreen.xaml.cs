using System.Windows.Controls;
using OpenVerse.Decker.Data;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

public partial class InitialScreen : UserControl
{
    private readonly CoreWindow _core;
    private string[] _langs = [];
    private string userKey = "";

    public InitialScreen(CoreWindow core)
    {
        InitializeComponent();

        _core = core;

        try
        {
            _langs = TextLoader.LoadAvailableLangs(AppConfig.Instance.TextlangsPath);
        }
        catch (Exception ex)
        {
            ErrorText.Text = I18n.Format("InitialLoadTextlangsError", ex.Message);
            return;
        }

        LangComboBox.ItemsSource = _langs;
        LangComboBox.SelectedIndex = _langs.Length > 0
            ? Math.Clamp(AppConfig.Instance.DefLang, 0, _langs.Length - 1)
            : -1;

        var userKeyCandidates = InternalDeckBuilder.ExtractUserKeys();
        Action<int, string> userKeySelectAction = (idx, str) => { this.userKey = str; };

        if (userKeyCandidates.Length == 1 && false)
        {
            userKeySelectAction(0, userKeyCandidates[0]);
        }
        else
        {
            _core.ShowFocused(
                new UserSelectDialog(
                    userKeyCandidates,
                    userKeySelectAction,
                    I18n.Text("CannotSelectUserKey")),
                false);
        }
    }

    private void RunButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (LangComboBox.SelectedIndex < 0)
        {
            ErrorText.Text = I18n.Text("InitialSelectLanguageError");
            return;
        }

        AppConfig.Instance.DefLang = LangComboBox.SelectedIndex;
        try
        {
            // TODO detect user key unselection safely
            _core.ShowScreen(new DeckListScreen(_core, _langs[LangComboBox.SelectedIndex], userKey));
        }
        catch (Exception ex)
        {
            ErrorText.Text = I18n.Format("InitialLoadDeckDataError", ex.Message);
        }
    }
}
