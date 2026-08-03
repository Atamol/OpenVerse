using OpenVerse.Common;

namespace OpenVerse.Tests;

// the client tests every received message against its own board and writes ConductError when it refuses one, then ships
// that log to add_client_log. it is the only desync verdict that is not ours, so it is the one worth counting
public class ConductErrorTests
{
    // shapes lifted from a real session's add_client_log body
    const string Sample =
        "ID:837123942 {\\n&&1/16:34:2:382:[bid 47681 vid 837123942]CardID112821010Idx40 IsOperateReceive notHandCard" +
        "&&1/16:34:2:383:[bid 47681 vid 837123942]ConductError" +
        "&&1/16:34:20:072:[bid 47681 vid 837123942]IsOperateReceive actionCard null" +
        "&&1/16:34:20:073:[bid 47681 vid 837123942]ConductError" +
        "&&1/16:34:58:616:[bid 47681 vid 837123942]IsOperateReceive playCard null" +
        "&&1/16:34:58:616:[bid 47681 vid 837123942]ConductError \\n}";

    [Fact]
    public void ReadsEveryRefusalWithItsReason()
    {
        var found = ClientLog.Read(Sample);

        Assert.Equal(3, found.Count);
        Assert.Equal(["IsOperateReceive notHandCard", "IsOperateReceive actionCard null", "IsOperateReceive playCard null"],
                     found.Select(c => c.Reason));
        Assert.All(found, c => Assert.Equal("47681", c.BattleId));
        Assert.All(found, c => Assert.Equal("837123942", c.ViewerId));
    }

    // the card id is what ties a refusal back to the play the relay could not price
    [Fact]
    public void NamesTheCardWhenTheClientDid()
    {
        var first = ClientLog.Read(Sample)[0];

        Assert.Equal(112821010, first.CardId);
        Assert.Equal(40, first.Idx);
        Assert.Contains("112821010", first.ToString());
    }

    [Fact]
    public void CarriesNoCardWhenTheClientNamedNone()
    {
        var second = ClientLog.Read(Sample)[1];

        Assert.Equal(0, second.CardId);
        Assert.DoesNotContain("card", second.ToString());
    }

    // a skill-selection refusal logs its own check first, so it must not be reported as a bare ConductError
    [Fact]
    public void ReadsASkillSelectionRefusal()
    {
        var log = "&&1/16:57:48:585:[bid 98731 vid 648872311]IsOperateReceive IsSelectSkillCheck" +
                  "&&1/16:57:48:586:[bid 98731 vid 648872311]ConductError";

        var found = ClientLog.Read(log);

        Assert.Single(found);
        Assert.Equal("IsOperateReceive IsSelectSkillCheck", found[0].Reason);
        Assert.Equal("98731", found[0].BattleId);
    }

    [Fact]
    public void FindsNothingInAnOrdinaryLog()
    {
        var log = "&&1/16:7:41:273:[bid 47681 vid 1001]OnReceived URI:InitNetwork&&SetStatus:RoomReady";

        Assert.Empty(ClientLog.Read(log));
    }

    // a battle that never desynced must not produce a phantom, and an entry with no bracket must not throw
    [Fact]
    public void IgnoresMalformedEntries()
    {
        Assert.Empty(ClientLog.Read(""));
        Assert.Empty(ClientLog.Read("ConductError with no bracket at all"));
    }
}
