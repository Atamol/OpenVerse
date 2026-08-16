using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
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

    // file name -> the path field it fills, so detection and the missing-file report share one list
    private static readonly string[] SetupGeneratedFiles =
    [
        "master_cardnametext.json",
        "master_skilldesctext.json",
        "textlangs.json",
        "card_master_full.csv.gz",
    ];

    private string? _setupExePath;
    private bool _promptShown;

    public PathSetupScreen(CoreWindow core)
    {
        InitializeComponent();
        _core = core;
        AutoDetectMissingPaths();
        RefreshPathTexts();

        // the dialog needs the window up, and this screen is built inside CoreWindow's constructor
        Loaded += (_, _) => PromptToRunSetup();
    }

    /// <summary>
    /// Proposes a path for anything that is missing. Only the text boxes change
    /// </summary>
    private void AutoDetectMissingPaths()
    {
        if (PathsAreValid(AppConfig.Instance))
        {
            return;
        }

        var directories = SetupLocator.SearchDirectories();
        _setupExePath = SetupLocator.FindFile(SetupLocator.SetupExeName, directories);

        _cardNameTextPath = Detected(_cardNameTextPath, "master_cardnametext.json", directories);
        _skillDescTextPath = Detected(_skillDescTextPath, "master_skilldesctext.json", directories);
        _textlangsPath = Detected(_textlangsPath, "textlangs.json", directories);
        _cardMasterCsvPath = Detected(_cardMasterCsvPath, "card_master_full.csv.gz", directories);
        // openverse.db is deliberately left alone: it lives in the game's own AppData folder, well
        // outside this search, so a hit here would be some other install's copy
    }

    // an existing path is never second-guessed, and a failed search leaves the default in place
    private static string Detected(string current, string fileName, IReadOnlyList<string> directories) =>
        File.Exists(current) ? current : SetupLocator.FindFile(fileName, directories) ?? current;

    /// <summary>
    /// Setup writes the four data files, so if they are still missing after the search it has not
    /// been run here yet - offer to run it rather than leaving the user to find the exe.
    /// </summary>
    private void PromptToRunSetup()
    {
        var stillMissing = SetupGeneratedFiles.Any(name => !File.Exists(PathFor(name)));
        if (_promptShown || _setupExePath is null || !stillMissing)
        {
            return;
        }
        _promptShown = true;

        var dialog = new ConfirmDialog(
            I18n.Text("PathSetupRunSetupMessage"),
            I18n.Text("PathSetupRunSetupButton"),
            I18n.Text("PathSetupCloseAppButton"));
        dialog.Confirmed += () =>
        {
            _core.HideFocused();
            RunSetup();
        };
        dialog.Cancelled += () => Application.Current.Shutdown();
        _core.ShowFocused(dialog, canDismiss: false);
    }

    private string PathFor(string fileName) => fileName switch
    {
        "master_cardnametext.json" => _cardNameTextPath,
        "master_skilldesctext.json" => _skillDescTextPath,
        "textlangs.json" => _textlangsPath,
        _ => _cardMasterCsvPath,
    };

    private void RunSetup()
    {
        try
        {
            // shell execute so its console shows: it takes a while and prints what it extracted
            Process.Start(new ProcessStartInfo(_setupExePath!)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_setupExePath!) ?? string.Empty,
            });
            ErrorText.Text = I18n.Text("PathSetupSetupRunning");
        }
        catch (Exception e)
        {
            ErrorText.Text = I18n.Format("PathSetupSetupFailed", e.Message);
        }
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
