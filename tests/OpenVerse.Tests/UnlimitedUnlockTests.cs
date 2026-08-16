using System.IO.Compression;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class UnlimitedUnlockTests
{
    const int ResurgentCol = 76;
    const int BaseCardIdCol = 63;
    const int ClanCol = 7;

    // 79 columns, with a quoted voice field holding commas at the position the real master puts one
    static string Row(int cardId, int baseCardId, string resurgent, string clan = "0")
    {
        var f = Enumerable.Repeat("0", CardMasterCodec.Columns.Length).ToArray();
        f[0] = cardId.ToString();
        f[ClanCol] = clan;
        f[55] = "\"VO_A,VO_B,VO_C\"";
        f[BaseCardIdCol] = baseCardId.ToString();
        f[ResurgentCol] = resurgent;
        return string.Join(',', f);
    }

    static string? RealMaster()
    {
        if (Fixtures.DataDir() is not { } dir) return null;
        using var fs = File.OpenRead(Path.Combine(dir, "card_master_full.csv.gz"));
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

    static string Field(string line, int index)
    {
        var (s, e) = UnlimitedUnlock.FieldRange(line, index);
        return s < 0 ? "" : line[s..e];
    }

    [Fact]
    public void FieldRangeSkipsCommasInsideQuotes()
    {
        var line = Row(100011010, 100011010, "1");
        Assert.Equal("\"VO_A,VO_B,VO_C\"", Field(line, 55));
        Assert.Equal("100011010", Field(line, 0));
        Assert.Equal("1", Field(line, ResurgentCol));
    }

    [Fact]
    public void ClearResurgentZeroesTheFlagAndLeavesEverythingElse()
    {
        var before = Row(100011010, 100011010, "1");
        var after = UnlimitedUnlock.ClearResurgent(before);
        Assert.Equal("0", Field(after, ResurgentCol));
        Assert.Equal("\"VO_A,VO_B,VO_C\"", Field(after, 55));
        Assert.Equal(before.Split(',').Length, after.Split(',').Length);
    }

    [Fact]
    public void ClearResurgentKeepsLineCountAndRowsThatWereAlreadyOff()
    {
        var csv = string.Join('\n', Row(1, 1, "1"), Row(2, 2, "0"), Row(3, 3, "1"));
        var after = UnlimitedUnlock.ClearResurgent(csv);
        var lines = after.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal("0", Field(l, ResurgentCol)));
    }

    // ClanType.ALL is 0, the value neutral cards carry, and the editor's mask always has that bit set
    [Fact]
    public void ClearClanFlattensEveryCardToNeutral()
    {
        var csv = string.Join('\n', Row(10, 10, "0", clan: "4"), Row(20, 20, "0", clan: "7"));
        var after = UnlimitedUnlock.ClearClan(csv);
        Assert.All(after.Split('\n'), l => Assert.Equal("0", Field(l, ClanCol)));
    }

    [Fact]
    public void ClearClanLeavesTheResurgentFlagAlone()
    {
        var after = UnlimitedUnlock.ClearClan(Row(10, 10, "1", clan: "4"));
        Assert.Equal("1", Field(after, ResurgentCol));
        Assert.Equal("\"VO_A,VO_B,VO_C\"", Field(after, 55));
    }

    [Fact]
    public void TheRealMasterKeepsEveryRowWhenTheClanIsFlattened()
    {
        if (RealMaster() is not { } csv) return;
        var before = csv.Split('\n');
        var after = UnlimitedUnlock.ClearClan(csv).Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.True(before.Count(l => Field(l, ClanCol) is not ("" or "0")) > 1000, "no clans to flatten");
        Assert.All(after, l => Assert.True(Field(l, ClanCol) is "" or "0"));
        for (var i = 0; i < before.Length; i++)
            Assert.Equal(Field(before[i], 0), Field(after[i], 0));
    }

    [Fact]
    public void RestrictedListNamesEveryBaseCardOnceAtTheRaisedCap()
    {
        // two ids sharing a base card (a card and its foil) must collapse to one entry
        var csv = string.Join('\n', Row(10, 10, "0"), Row(11, 10, "0"), Row(20, 20, "0"));
        var json = UnlimitedUnlock.RestrictedListJson(csv);
        using var doc = JsonDocument.Parse(json);
        var map = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        Assert.Equal(2, map.Count);
        Assert.Equal(UnlimitedUnlock.MaxCopies, map["10"]);
        Assert.Equal(UnlimitedUnlock.MaxCopies, map["20"]);
    }

    // CardParameter overwrites its built-in 3 with whatever this says, so the cap has to be the deck size or the
    // editor still stops short
    [Fact]
    public void TheRaisedCapIsTheWholeDeck()
        => Assert.Equal(40, UnlimitedUnlock.MaxCopies);

    [Fact]
    public void TheRealMasterLosesEveryResurgentFlagAndKeepsEveryRow()
    {
        if (RealMaster() is not { } csv) return;
        var before = csv.Split('\n');
        var after = UnlimitedUnlock.ClearResurgent(csv).Split('\n');
        Assert.Equal(before.Length, after.Length);

        var wasSet = before.Count(l => Field(l, ResurgentCol) == "1");
        Assert.True(wasSet > 0, "the real master has no resurgent cards, so this test proves nothing");
        Assert.Equal(0, after.Count(l => Field(l, ResurgentCol) == "1"));

        // the rewrite splices one field, so every other column has to read back identical
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Equal(Field(before[i], 0), Field(after[i], 0));
            Assert.Equal(Field(before[i], 7), Field(after[i], 7));
            Assert.Equal(Field(before[i], 55), Field(after[i], 55));
            Assert.Equal(Field(before[i], BaseCardIdCol), Field(after[i], BaseCardIdCol));
            Assert.Equal(Field(before[i], 78), Field(after[i], 78));
        }
    }

    [Fact]
    public void TheRealMasterYieldsAParsableRestrictedList()
    {
        if (RealMaster() is not { } csv) return;
        var json = UnlimitedUnlock.RestrictedListJson(csv);
        using var doc = JsonDocument.Parse(json);
        var count = doc.RootElement.EnumerateObject().Count();
        Assert.True(count > 1000, $"only {count} base cards, the column index is probably wrong");
        Assert.All(doc.RootElement.EnumerateObject(), p =>
        {
            Assert.True(int.TryParse(p.Name, out var id) && id > 0);
            Assert.Equal(UnlimitedUnlock.MaxCopies, p.Value.GetInt32());
        });
    }

    // the client keeps the master it already downloaded when the hash matches, so a flip has to change the hash
    [Fact]
    public void TheUnlockedMasterDiffersFromTheLockedOne()
    {
        if (RealMaster() is not { } csv) return;
        Assert.NotEqual(csv, UnlimitedUnlock.ClearResurgent(csv));
    }
}
