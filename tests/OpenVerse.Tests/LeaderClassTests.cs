using Microsoft.Data.Sqlite;
using OpenVerse.Api;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// The leader the battle shows comes from the battle_deck row the room writes. Counting it off the card ids is only a
// guess, and on a deck that mixes classes the guess picks the majority and hands the player a leader they never chose
[Collection("Sqlite")]
public class LeaderClassTests : IDisposable
{
    const string Owner = "udid-owner";

    readonly string _dbPath;
    readonly DeckStore _decks;
    readonly BattleDeckStore _battleDecks;
    readonly RoomStore _rooms;
    readonly RoomHandler _handler;

    public LeaderClassTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ov-leader-{Guid.NewGuid():N}.db");
        _decks = new DeckStore(_dbPath);
        _battleDecks = new BattleDeckStore(_dbPath);
        _rooms = new RoomStore();
        _handler = new RoomHandler(_rooms, new UserStore(), _decks, _battleDecks);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    // class digit is the 100,000s place, so 100711010 is class 7 and 100811010 is class 8
    static int[] MixedDeck() =>
        [.. Enumerable.Repeat(100711010, 30), .. Enumerable.Repeat(100811010, 10)];

    string StartBattle(int storedClass, int[] cards)
    {
        _decks.Save(new Deck { UserKey = Owner, DeckNo = 1, Format = 2, ClassId = storedClass, CardIdArray = cards });
        var room = _rooms.Create(Owner, 1, 1, 2, 0, false, false);
        _handler.Handle("/shadowverse/open_room/set_deck", """{"deck_no":1}""", Owner);
        _handler.Handle("/shadowverse/open_room_battle/do_matching", "{}", Owner);
        return room.RoomId;
    }

    [Fact]
    public void TheLeaderIsTheClassTheDeckRecorded()
    {
        var roomId = StartBattle(storedClass: 8, MixedDeck());
        var written = _battleDecks.Get(roomId, isOwner: true);
        Assert.NotNull(written);
        Assert.Equal(8, written!.ClassId);
        Assert.Equal(8, written.CharaId);   // GetClassPrm throws on anything outside 1..8
    }

    [Fact]
    public void TheCardMajorityDoesNotOverrideIt()
    {
        // 30 of the 40 cards are class 7, so a card count would return that
        var roomId = StartBattle(storedClass: 8, MixedDeck());
        Assert.NotEqual(7, _battleDecks.Get(roomId, isOwner: true)!.ClassId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void EverySingleClassDeckKeepsItsOwnLeader(int cls)
    {
        var roomId = StartBattle(cls, [.. Enumerable.Repeat(100011010 + cls * 100000, 40)]);
        Assert.Equal(cls, _battleDecks.Get(roomId, isOwner: true)!.ClassId);
    }

    // a deck that never recorded one still has to produce a leader the client can resolve
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void AnUnrecordedClassFallsBackToTheCards(int stored)
    {
        var roomId = StartBattle(stored, MixedDeck());
        Assert.Equal(7, _battleDecks.Get(roomId, isOwner: true)!.ClassId);
    }
}
