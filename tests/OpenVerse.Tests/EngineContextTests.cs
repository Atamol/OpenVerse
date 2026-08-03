using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// the gate for the mirror pair. two instances that silently share one engine still print two plausible boards, because
// ShadowBattle.Mgr is an instance reference while only the static BattleManagerBase.main gets stolen, so nothing about
// board output can catch it. assert on identity instead
[Collection("Engine")]
public class EngineContextTests
{
    const BindingFlags PubS = BindingFlags.Public | BindingFlags.Static;
    const BindingFlags NonPubS = BindingFlags.NonPublic | BindingFlags.Static;

    static string? Csv()
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data", "card_master_full.csv.gz");
        if (!File.Exists(gz)) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"))) return null;
        using var fs = File.OpenRead(gz);
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

    static int[] Deck() =>
        Enumerable.Range(0, 40).Select(i => new[] { 100011010, 900011080, 100011020, 100011030 }[i % 4]).ToArray();

    static (EngineContext Alc, Type Host) Instance(string name, string csv)
    {
        var alc = new EngineContext(name);
        var host = alc.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))
                      .GetType("OpenVerse.EngineHost.ShadowMatch")!;
        Assert.True((bool)host.GetMethod("Boot", PubS)!.Invoke(null, [csv])!,
                    host.GetProperty("LastError", PubS)!.GetValue(null) as string);
        return (alc, host);
    }

    static Assembly EngineOf(EngineContext alc) => alc.Assemblies.Single(a => a.GetName().Name == "Assembly-CSharp");
    static object? Live(Type bmb) => bmb.GetField("main", NonPubS)!.GetValue(null);

    [Fact]
    public void TwoInstancesDoNotShareTheEnginesBattleManager()
    {
        if (Csv() is not { } csv) return;

        var (alcA, hostA) = Instance("ENGINE-A", csv);
        var (alcB, hostB) = Instance("ENGINE-B", csv);

        var csA = EngineOf(alcA);
        var csB = EngineOf(alcB);
        Assert.NotSame(csA, csB);
        Assert.Same(alcA, AssemblyLoadContext.GetLoadContext(csA));
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(csA));

        // distinct Types mean distinct static storage. AssemblyQualifiedName is identical in both, so identity is the
        // only discriminator that works here
        var bmbA = csA.GetType("BattleManagerBase")!;
        var bmbB = csB.GetType("BattleManagerBase")!;
        Assert.NotSame(bmbA, bmbB);
        Assert.Null(Live(bmbA));
        Assert.Null(Live(bmbB));

        // BattleManagerBase's ctor does main = this on every battle it builds
        var hA = (int)hostA.GetMethod("Create", PubS)!.Invoke(null, [7, true, Deck(), Deck(), new[] { 1, 2, 3 }, new[] { 1, 2, 3 }])!;
        Assert.True(hA > 0);
        Assert.NotNull(Live(bmbA));
        Assert.Null(Live(bmbB));               // the assertion this whole stage exists for
        var a0 = Live(bmbA);

        var hB = (int)hostB.GetMethod("Create", PubS)!.Invoke(null, [99, false, Deck(), Deck(), new[] { 2, 3, 4 }, new[] { 2, 3, 4 }])!;
        Assert.True(hB > 0);
        Assert.Same(a0, Live(bmbA));           // B did not steal A
        Assert.NotSame(Live(bmbA), Live(bmbB));
        Assert.False(bmbB.IsInstanceOfType(Live(bmbA)));

        // the other singleton a battle hangs off
        Assert.NotSame(csA.GetType("GameMgr")!.GetField("_instance", NonPubS)!.GetValue(null),
                       csB.GetType("GameMgr")!.GetField("_instance", NonPubS)!.GetValue(null));

        hostA.GetMethod("Close", PubS)!.Invoke(null, [hA]);
        hostB.GetMethod("Close", PubS)!.Invoke(null, [hB]);
    }

    // measured: a null-returning override and one throwing FileNotFoundException both let the binder fall through to a
    // Default-context copy, with no exception and no log. the refusal has to be a shape the binder does not retry past
    [Fact]
    public void TheResolverRefusesRatherThanFallingBackToDefault()
    {
        var alc = new EngineContext("ENGINE-PROBE");
        var e = Assert.ThrowsAny<Exception>(() => alc.LoadFromAssemblyName(new AssemblyName("NotAnEngineAssembly")));
        Assert.IsNotType<FileNotFoundException>(e);
        for (var i = e; i is not null; i = i.InnerException)
            if ((i.Message ?? "").Contains("ENGINE-PROBE")) return;
        Assert.Fail($"the refusal did not name the context: {e}");
    }

    // the card whose ability broke the relay: if the engine can describe it out of a context, it can adjudicate it
    [Fact]
    public void ResolvesARealCardsDefinition()
    {
        if (Csv() is not { } csv) return;
        var (alc, _) = Instance("ENGINE-CARDS", csv);

        var cm = EngineOf(alc).GetType("Wizard.CardMaster")!;
        var master = cm.GetMethod("GetInstanceForBattle")!.Invoke(null, null);
        Assert.NotNull(master);
        var prm = cm.GetMethod("GetCardParameterFromId")!.Invoke(master, [113131030])!;
        var skill = prm.GetType().GetProperty("Skill")!.GetValue(prm) as string;

        Assert.Contains("summon_card", skill);
    }

    // over-tighten the allow-list and the flat Dictionary<string, object> surface starts crossing a type boundary
    [Fact]
    public void BclTypesStayUnifiedAcrossContexts()
    {
        var alc = new EngineContext("ENGINE-BCL");
        var rt = alc.LoadFromAssemblyName(new AssemblyName("System.Runtime"));
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(rt));
    }
}
