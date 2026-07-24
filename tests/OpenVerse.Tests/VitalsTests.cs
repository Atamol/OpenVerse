using System.Text.Json.Nodes;
using OpenVerse.Battle;

namespace OpenVerse.Tests;

// playerParam entries taken from real captured traffic. These are the values that make vengeance/overflow decidable,
// and the relay had been dropping them.
public class VitalsTests
{
    static BattleHub.Vitals Apply(params string[] entries)
    {
        var v = new BattleHub.Vitals();
        foreach (var e in entries)
        {
            var o = JsonNode.Parse(e)!.AsObject();
            foreach (var (k, node) in o)
            {
                if (k == "isSelf" || node is null) continue;
                var n = node.GetValue<int>();
                switch (k)
                {
                    case "damage": v.Life -= n; break;
                    case "heal": v.Life += n; break;
                    case "set": v.Life = n; break;
                    case "addPP": v.Pp += n; break;
                    case "usePP": v.Pp -= n; break;
                    case "maxPP": v.MaxPp = n; break;
                    case "cemetery": v.Cemetery = n; break;
                }
            }
        }
        return v;
    }

    // magnitudes: the sender takes Math.Abs, so the key is what carries the sign
    [Fact]
    public void DamageAndHealMoveLifeOppositeWays()
    {
        Assert.Equal(18, Apply("""{"isSelf":0,"damage":2}""").Life);
        Assert.Equal(21, Apply("""{"isSelf":1,"heal":1}""").Life);
        Assert.Equal(15, Apply("""{"isSelf":0,"damage":3}""", """{"isSelf":0,"damage":2}""").Life);
    }

    // maxPP is absolute, not a delta - this is the ramp value overflow reads
    [Fact]
    public void MaxPpIsAbsolute()
    {
        Assert.Equal(7, Apply("""{"isSelf":1,"maxPP":1}""", """{"isSelf":1,"maxPP":7}""").MaxPp);
    }

    [Fact]
    public void PpAccumulatesAndSpends()
    {
        var v = Apply("""{"isSelf":1,"addPP":2}""", """{"isSelf":1,"addPP":1}""", """{"isSelf":1,"usePP":2}""");
        Assert.Equal(1, v.Pp);
    }

    // set anchors life absolutely, so a later damage is relative to it and not to the starting 20
    [Fact]
    public void SetAnchorsLifeAbsolutely()
    {
        Assert.Equal(4, Apply("""{"isSelf":1,"set":6}""", """{"isSelf":1,"damage":2}""").Life);
    }

    [Fact]
    public void CemeteryIsAbsolute()
    {
        Assert.Equal(6, Apply("""{"isSelf":1,"cemetery":3}""", """{"isSelf":1,"cemetery":6}""").Cemetery);
    }

    // the whole point: is this deck in vengeance range (life <= 10)?
    [Fact]
    public void TracksLifeIntoVengeanceRange()
    {
        var v = Apply("""{"isSelf":1,"damage":6}""", """{"isSelf":1,"damage":5}""");
        Assert.Equal(9, v.Life);
        Assert.True(v.Life <= 10);
    }
}
