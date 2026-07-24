using System.Text.Json.Nodes;
using OpenVerse.Battle;

namespace OpenVerse.Tests;

// a Deck->Field uList entry is a direct summon; without a cardId the peer leaves the slot a placeholder and the summon
// never happens. Deck->Hand is a draw and must stay hidden.
public class SummonRevealTests
{
    static JsonObject Reveal(string json, Dictionary<int, int> ledger)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        BattleHub.InjectSummonedCardIds(body, ledger);
        return body;
    }

    // the exact uList captured from the match where the direct summon did not appear on the peer
    const string Captured = """
        {"uList":[{"idxList":[2],"from":0,"to":20,"isSelf":1,"skill":"2|55|0"},
                  {"idxList":[28],"from":0,"to":20,"isSelf":1,"skill":"28|59|0"}]}
        """;

    static readonly Dictionary<int, int> Ledger = new() { [2] = 113131030, [28] = 113131030, [9] = 720314020 };

    [Fact]
    public void DirectSummonEntriesGetTheirCardId()
    {
        var ul = Reveal(Captured, Ledger)["uList"]!.AsArray();
        Assert.Equal(113131030, ul[0]!["cardId"]!.GetValue<int>());
        Assert.Equal(113131030, ul[1]!["cardId"]!.GetValue<int>());
    }

    [Fact]
    public void OtherFieldsAreUntouched()
    {
        var e = Reveal(Captured, Ledger)["uList"]!.AsArray()[1]!.AsObject();
        Assert.Equal("[28]", e["idxList"]!.ToJsonString());
        Assert.Equal(1, e["isSelf"]!.GetValue<int>());
        Assert.Equal("28|59|0", e["skill"]!.GetValue<string>());
    }

    // a draw must never be revealed: that is the opponent's hidden hand
    [Fact]
    public void DeckToHandIsNeverRevealed()
    {
        var body = Reveal("""{"uList":[{"idxList":[9],"from":0,"to":10,"isSelf":1}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }

    // isSelf=0 is the peer's own card: stamping the ledger id there would corrupt the wrong index space
    [Fact]
    public void PeerOwnedEntriesAreSkipped()
    {
        var body = Reveal("""{"uList":[{"idxList":[2],"from":0,"to":20,"isSelf":0}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }

    [Fact]
    public void AClientSuppliedCardIdWins()
    {
        var body = Reveal("""{"uList":[{"idxList":[2],"from":0,"to":20,"isSelf":1,"cardId":999}]}""", Ledger);
        Assert.Equal(999, body["uList"]!.AsArray()[0]!["cardId"]!.GetValue<int>());
    }

    // the sender merges records whose fields all match, and its gate compares cardId - which is 0 on all of these - so
    // two different cards collapse into one entry. splitting is the inverse of that merge.
    // this is the exact entry captured live where two followers came out of the deck and only one could be named
    [Fact]
    public void MixedIdEntryIsSplitOnePerCard()
    {
        var ledger = new Dictionary<int, int> { [14] = 130141030, [2] = 121134010 };
        var ul = Reveal("""{"uList":[{"idxList":[14,2],"from":0,"to":20,"isSelf":1,"skill":"9|129|0"}]}""", ledger)["uList"]!.AsArray();
        Assert.Equal(2, ul.Count);
        Assert.Equal("[14]", ul[0]!["idxList"]!.ToJsonString());
        Assert.Equal(130141030, ul[0]!["cardId"]!.GetValue<int>());
        Assert.Equal("[2]", ul[1]!["idxList"]!.ToJsonString());
        Assert.Equal(121134010, ul[1]!["cardId"]!.GetValue<int>());
    }

    // every non-positional field is broadcast to each index by the receiver, so the split must carry them verbatim
    [Fact]
    public void SplitEntriesKeepTheirOtherFields()
    {
        var ledger = new Dictionary<int, int> { [14] = 130141030, [2] = 121134010 };
        var ul = Reveal("""{"uList":[{"idxList":[14,2],"from":0,"to":20,"isSelf":1,"skill":"9|129|0"}]}""", ledger)["uList"]!.AsArray();
        foreach (var e in ul)
        {
            Assert.Equal("9|129|0", e!["skill"]!.GetValue<string>());
            Assert.Equal(0, e["from"]!.GetValue<int>());
            Assert.Equal(20, e["to"]!.GetValue<int>());
            Assert.Equal(1, e["isSelf"]!.GetValue<int>());
        }
    }

    // randomTargetIdx is read positionally against idxList, so slicing it is not safe: decline instead
    [Fact]
    public void MixedIdWithRandomTargetIdxIsDeclined()
    {
        var ledger = new Dictionary<int, int> { [14] = 130141030, [2] = 121134010 };
        var ul = Reveal("""{"uList":[{"idxList":[14,2],"from":0,"to":20,"isSelf":1,"randomTargetIdx":[3,4]}]}""", ledger)["uList"]!.AsArray();
        Assert.Single(ul);
        Assert.Null(ul[0]!["cardId"]);
    }

    // an unresolved index (the -99 shortage sentinel included) must not be half-revealed
    [Fact]
    public void MixedIdWithAnUnknownIndexIsDeclined()
    {
        var ledger = new Dictionary<int, int> { [14] = 130141030 };
        var ul = Reveal("""{"uList":[{"idxList":[14,-99],"from":0,"to":20,"isSelf":1}]}""", ledger)["uList"]!.AsArray();
        Assert.Single(ul);
        Assert.Null(ul[0]!["cardId"]);
    }

    // unrelated entries must keep their position, since the first one's cardId is latched for accelerate
    [Fact]
    public void OtherEntriesKeepTheirOrderAroundASplit()
    {
        var ledger = new Dictionary<int, int> { [14] = 130141030, [2] = 121134010 };
        var ul = Reveal("""
            {"uList":[{"idxList":[5],"from":10,"to":30,"isSelf":1,"cardId":111},
                      {"idxList":[14,2],"from":0,"to":20,"isSelf":1},
                      {"idxList":[6],"from":10,"to":30,"isSelf":1,"cardId":222}]}
            """, ledger)["uList"]!.AsArray();
        Assert.Equal(4, ul.Count);
        Assert.Equal(111, ul[0]!["cardId"]!.GetValue<int>());
        Assert.Equal(130141030, ul[1]!["cardId"]!.GetValue<int>());
        Assert.Equal(121134010, ul[2]!["cardId"]!.GetValue<int>());
        Assert.Equal(222, ul[3]!["cardId"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownIndexIsDeclined()
    {
        var body = Reveal("""{"uList":[{"idxList":[77],"from":0,"to":20,"isSelf":1}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }

    [Fact]
    public void NoUListIsANoOp()
    {
        Assert.Null(Reveal("""{"playIdx":3}""", Ledger)["uList"]);
    }

    // arriving on the field is what makes a card public, so the source zone is irrelevant: reanimate (cemetery),
    // return-from-banish and reservation all hit the same unnamed-card problem the deck summon did
    [Theory]
    [InlineData(30)]  // Cemetery -> reanimate
    [InlineData(40)]  // Banish
    [InlineData(80)]  // Reservation
    public void AnyHiddenZoneArrivingOnTheFieldIsRevealed(int from)
    {
        var body = Reveal($$"""{"uList":[{"idxList":[2],"from":{{from}},"to":20,"isSelf":1}]}""", Ledger);
        Assert.Equal(113131030, body["uList"]!.AsArray()[0]!["cardId"]!.GetValue<int>());
    }

    // a play out of hand already carries its identity in the knownList InjectKnownCard synthesizes, so the busiest
    // path stays untouched
    [Fact]
    public void HandToFieldIsLeftToTheNormalPlayPath()
    {
        var body = Reveal("""{"uList":[{"idxList":[2],"from":10,"to":20,"isSelf":1}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }

    // the destination decides privacy: a card going back to hand from anywhere is hidden again
    [Theory]
    [InlineData(30)]  // Cemetery -> Hand
    [InlineData(40)]  // Banish -> Hand
    public void ArrivingInHandIsNeverRevealed(int from)
    {
        var body = Reveal($$"""{"uList":[{"idxList":[2],"from":{{from}},"to":10,"isSelf":1}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }

    // an entry with no from at all is not proof of a public source, so it keeps the safe decline
    [Fact]
    public void MissingFromIsDeclined()
    {
        var body = Reveal("""{"uList":[{"idxList":[2],"to":20,"isSelf":1}]}""", Ledger);
        Assert.Null(body["uList"]!.AsArray()[0]!["cardId"]);
    }
}
