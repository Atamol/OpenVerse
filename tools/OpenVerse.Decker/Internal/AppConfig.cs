using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVerse.Decker.Internal;

public sealed class AppConfig : INotifyPropertyChanged
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "app_config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Instance { get; private set; } = null!;

    static AppConfig() => LoadOrCreate();

    [JsonPropertyName("card_name_text_path")]
    public string CardNameTextPath
    {
        get => _cardNameTextPath;
        set { _cardNameTextPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _cardNameTextPath = Path.Combine(AppContext.BaseDirectory, "data", "master_cardnametext.json");

    [JsonPropertyName("skill_desc_text_path")]
    public string SkillDescTextPath
    {
        get => _skillDescTextPath;
        set { _skillDescTextPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _skillDescTextPath = Path.Combine(AppContext.BaseDirectory, "data", "master_skilldesctext.json");

    [JsonPropertyName("textlangs_path")]
    public string TextlangsPath
    {
        get => _textlangsPath;
        set { _textlangsPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _textlangsPath = Path.Combine(AppContext.BaseDirectory, "data", "textlangs.json");

    [JsonPropertyName("card_master_csv_path")]
    public string CardMasterCsvPath
    {
        get => _cardMasterCsvPath;
        set { _cardMasterCsvPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _cardMasterCsvPath = Path.Combine(AppContext.BaseDirectory, "data", "card_master_full.csv.gz");

    /// <summary>
    /// where the client caches card_*.unity3d. Missing or wrong just means the tiles keep their
    /// rarity colour instead of art.
    /// </summary>
    [JsonPropertyName("card_bundle_dir_path")]
    public string CardBundleDirPath
    {
        get => _cardBundleDirPath;
        set { _cardBundleDirPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _cardBundleDirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Cygames", "Shadowverse", "a");

    [JsonPropertyName("openverse_db_path")]
    public string OpenVerseDbPath
    {
        get => _openVerseDbPath;
        set { _openVerseDbPath = value; Save(); RaisePropertyChanged(); }
    }
    private string _openVerseDbPath = DeckRepository.DefaultDbPath;

    [JsonPropertyName("def_lang")]
    public int DefLang
    {
        get => _defLang;
        set { _defLang = value; Save(); RaisePropertyChanged(); }
    }
    private int _defLang;

    public static void LoadOrCreate()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!File.Exists(ConfigPath))
        {
            Instance = new AppConfig();
            Instance.Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            Instance = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            Instance = new AppConfig();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
