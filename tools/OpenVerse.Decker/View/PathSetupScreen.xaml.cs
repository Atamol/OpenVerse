using System.IO;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

public partial class PathSetupScreen : UserControl
{
    private readonly CoreWindow _core;

    private string _cardNameTextPath = AppConfig.Instance.CardNameTextPath;
    private string _skillDescTextPath = AppConfig.Instance.SkillDescTextPath;
    private string _textlangsPath = AppConfig.Instance.TextlangsPath;
    private string _cardMasterCsvPath = AppConfig.Instance.CardMasterCsvPath;
    private string _openVerseDbPath = AppConfig.Instance.OpenVerseDbPath;

    public PathSetupScreen(CoreWindow core)
    {
        InitializeComponent();
        _core = core;
        RefreshPathTexts();
    }

    // static check used by CoreWindow to decide whether this screen needs to be shown at all
    public static bool PathsAreValid() => PathsAreValid(AppConfig.Instance);

    public static bool PathsAreValid(AppConfig config) =>
        File.Exists(config.CardNameTextPath) &&
        File.Exists(config.SkillDescTextPath) &&
        File.Exists(config.TextlangsPath) &&
        File.Exists(config.CardMasterCsvPath) &&
        File.Exists(config.OpenVerseDbPath);

    private void RefreshPathTexts()
    {
        CardNameTextPathText.Text = _cardNameTextPath;
        SkillDescTextPathText.Text = _skillDescTextPath;
        TextlangsPathText.Text = _textlangsPath;
        CardMasterCsvPathText.Text = _cardMasterCsvPath;
        OpenVerseDbPathText.Text = _openVerseDbPath;
    }

    private static string? BrowseForFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void BrowseCardNameText_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BrowseForFile("JSON files (*.json)|*.json|All files (*.*)|*.*") is { } path)
        {
            _cardNameTextPath = path;
            RefreshPathTexts();
        }
    }

    private void BrowseSkillDescText_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BrowseForFile("JSON files (*.json)|*.json|All files (*.*)|*.*") is { } path)
        {
            _skillDescTextPath = path;
            RefreshPathTexts();
        }
    }

    private void BrowseTextlangs_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BrowseForFile("JSON files (*.json)|*.json|All files (*.*)|*.*") is { } path)
        {
            _textlangsPath = path;
            RefreshPathTexts();
        }
    }

    private void BrowseCardMasterCsv_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BrowseForFile("card_master_full.csv.gz|card_master_full.csv.gz|Gzip files (*.gz)|*.gz|All files (*.*)|*.*") is { } path)
        {
            _cardMasterCsvPath = path;
            RefreshPathTexts();
        }
    }

    private void BrowseOpenVerseDb_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BrowseForFile("openverse.db|openverse.db|SQLite database (*.db)|*.db|All files (*.*)|*.*") is { } path)
        {
            _openVerseDbPath = path;
            RefreshPathTexts();
        }
    }

    private void RunButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var missing = new List<string>();
        if (!File.Exists(_cardNameTextPath))
        {
            missing.Add("master_cardnametext.json");
        }
        if (!File.Exists(_skillDescTextPath))
        {
            missing.Add("master_skilldesctext.json");
        }
        if (!File.Exists(_textlangsPath))
        {
            missing.Add("textlangs.json");
        }
        if (!File.Exists(_cardMasterCsvPath))
        {
            missing.Add("card_master_full.csv.gz");
        }
        if (!File.Exists(_openVerseDbPath))
        {
            missing.Add("openverse.db");
        }

        if (missing.Count > 0)
        {
            ErrorText.Text = I18n.Format("PathSetupMissingFilesError", string.Join(", ", missing));
            return;
        }

        ErrorText.Text = string.Empty;
        AppConfig.Instance.CardNameTextPath = _cardNameTextPath;
        AppConfig.Instance.SkillDescTextPath = _skillDescTextPath;
        AppConfig.Instance.TextlangsPath = _textlangsPath;
        AppConfig.Instance.CardMasterCsvPath = _cardMasterCsvPath;
        AppConfig.Instance.OpenVerseDbPath = _openVerseDbPath;

        _core.ShowScreen(new InitialScreen(_core));
    }
}
