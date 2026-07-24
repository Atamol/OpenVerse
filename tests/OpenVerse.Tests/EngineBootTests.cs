using System.IO.Compression;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// the engine assemblies are built per-host and stay out of the repo, so these skip rather than fail when absent
[Collection("Engine")]
public class EngineBootTests
{
    static string? Csv()
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data", "card_master_full.csv.gz");
        if (!File.Exists(gz)) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"))) return null;
        using var fs = File.OpenRead(gz);
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

    [Fact]
    public void BootsAndLoadsTheRealCardMaster()
    {
        if (Csv() is not { } csv) return;
        Assert.True(EngineBoot.Boot(csv), EngineBoot.Failure);
        Assert.True(EngineBoot.CardCount > 10000, $"only {EngineBoot.CardCount} cards");
    }

    [Fact]
    public void BootIsIdempotent()
    {
        if (Csv() is not { } csv) return;
        Assert.True(EngineBoot.Boot(csv), EngineBoot.Failure);
        Assert.True(EngineBoot.Boot(csv));
    }

    // the card whose ability broke the relay: if the engine can describe it, it can adjudicate it
    [Fact]
    public void ResolvesARealCardsDefinition()
    {
        if (Csv() is not { } csv) return;
        Assert.True(EngineBoot.Boot(csv), EngineBoot.Failure);

        var cm = EngineBoot.T("CardMaster")!;
        var master = cm.GetMethod("GetInstanceForBattle")!.Invoke(null, null);
        var prm = cm.GetMethod("GetCardParameterFromId")!.Invoke(master, [113131030])!;
        var skill = prm.GetType().GetProperty("Skill")!.GetValue(prm) as string;

        Assert.Contains("summon_card", skill);
    }
}
