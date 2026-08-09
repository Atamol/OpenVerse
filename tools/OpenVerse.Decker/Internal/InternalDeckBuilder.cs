using OpenVerse.Common;
using OpenVerse.Decker.Data;

namespace OpenVerse.Decker.Internal;

/// <summary>
/// implement loose coupling between deck building ui and database operations.
/// </summary>
public sealed class InternalDeckBuilder
{
    private readonly DeckStore _store;
    private readonly string _userKey;

    public static readonly IReadOnlyList<int> ValidClanIds = [1, 2, 3, 4, 5, 6, 7, 8];

    public TextLoader Text { get; }
    public StatsLoader Stats { get; }

    public CardFilterLoader Filters { get; } = new();

    public FilterEngine FilterEngine { get; }

    /// <summary>Shared so its cache and its single decode thread are shared too.</summary>
    public CardArtworkLoader Artwork { get; } = new(AppConfig.Instance.CardBundleDirPath);

    public static string[] ExtractUserKeys()
    {
        var repo = new DeckRepository(AppConfig.Instance.OpenVerseDbPath);
        var keys = repo.Exists() ? repo.ListUserKeys() : [];

        return keys.ToArray();
    }

    public InternalDeckBuilder(TextLoader text, StatsLoader stats, string key)
    {
        Text = text;
        Stats = stats;
        FilterEngine = CardFilterCatalog.Build(text, stats, Filters);
        _store = new DeckStore(AppConfig.Instance.OpenVerseDbPath);

        var repo = new DeckRepository(AppConfig.Instance.OpenVerseDbPath);
        var keys = repo.Exists() ? repo.ListUserKeys() : [];
        if (!keys.Contains(key))
        {
            throw new Exception($"The database doesn't contain the specified key: {key}");
        }
        _userKey = key;
    }

    public bool HasUser => !string.IsNullOrEmpty(_userKey);

    public List<Deck> ListDecks() => HasUser ? _store.List(_userKey) : [];

    public Deck NewDeck(int classId) => new()
    {
        ClassId = classId,
        Format = 2, // 1 = rotation, 2 = unlimited
    };

    public void Save(Deck deck)
    {
        if (!HasUser)
        {
            throw new InvalidOperationException(
                "openverse.db has no user_key yet - launch the client and let it sync at least once first");
        }

        deck.UserKey = _userKey;
        if (deck.DeckNo == 0)
        {
            deck.DeckNo = _store.NextDeckNo(_userKey, deck.Format);
        }

        _store.Save(deck);
    }

    public void Delete(Deck deck)
    {
        if (!HasUser)
        {
            throw new InvalidOperationException(
                "openverse.db has no user_key yet - launch the client and let it sync at least once first");
        }

        _store.Delete(_userKey, deck.DeckNo);
    }
}
