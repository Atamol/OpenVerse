using OpenVerse.Battle;

namespace OpenVerse.Tests;

// RESULT_CODE is self-relative: it names the RECIPIENT's own outcome, so a *Win code means the holder won. The
// earlier reading here was the opposite, which credited every decided match to the loser.
public class WinCountTests
{
    [Theory]
    [InlineData(101)]  // LifeWin
    [InlineData(103)]  // DeckoutWin
    [InlineData(105)]  // RetireWin
    [InlineData(201)]  // DisconnectWin
    public void WinCodesMeanTheRecipientWon(int code) => Assert.True(BattleHub.WonBy(code));

    [Theory]
    [InlineData(102)]  // LifeLose
    [InlineData(104)]  // DeckoutLose
    [InlineData(106)]  // RetireLose
    [InlineData(202)]  // DisconnectLose
    public void LoseCodesMeanTheRecipientLost(int code) => Assert.False(BattleHub.WonBy(code));

    // the pairs the relay actually hands out must credit the side the code says won
    [Theory]
    [InlineData(101, 102)]  // natural lethal: reporter won
    [InlineData(102, 101)]  // natural lethal: reporter lost
    [InlineData(105, 106)]  // retire: the reporter received the retire, so it won
    public void RelayCodePairsAgreeOnOneWinner(int reporterCode, int peerCode)
    {
        Assert.NotEqual(BattleHub.WonBy(reporterCode), BattleHub.WonBy(peerCode));
    }

    // a no-contest must not move the score either way
    [Theory]
    [InlineData(1)]    // NoContest
    [InlineData(2)]    // Invalid
    [InlineData(0)]
    [InlineData(999)]  // Error
    public void UndecidedCodesDoNotScore(int code) => Assert.Null(BattleHub.WonBy(code));
}
