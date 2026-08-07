using System.Reflection;
using System.Runtime.CompilerServices;
using OpenVerse.Battle;

namespace OpenVerse.Tests;

// A pulled cable leaves the socket half-open, so PeerClosed can be minutes late or never fire. The heartbeat used to
// answer peerOnline: true unconditionally, which left the survivor waiting on someone who was already gone
public class PeerSilenceTests
{
    static Session At(DateTimeOffset lastHeard)
    {
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        typeof(Session).GetProperty("LastHeard")!.SetValue(s, lastHeard);
        return s;
    }

    static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshPeerIsOnline() => Assert.True(BattleHub.PeerIsOnline(At(Now), Now));

    [Fact]
    public void ASilentPeerGoesOffline() =>
        Assert.False(BattleHub.PeerIsOnline(At(Now - BattleHub.PeerSilence - TimeSpan.FromSeconds(1)), Now));

    [Fact]
    public void JustInsideTheWindowIsStillOnline() =>
        Assert.True(BattleHub.PeerIsOnline(At(Now - BattleHub.PeerSilence + TimeSpan.FromSeconds(1)), Now));

    // a transient lookup miss must not read as a drop, or the client greys out mid-mulligan
    [Fact]
    public void AMissingPeerIsNotADrop() => Assert.True(BattleHub.PeerIsOnline(null, Now));

    // the client gives up at 95s, so the relay has to decide well before that or both sides just sit there
    [Fact]
    public void FiresWellInsideTheClientTimeout() =>
        Assert.True(BattleHub.PeerSilence < TimeSpan.FromSeconds(95));
}
