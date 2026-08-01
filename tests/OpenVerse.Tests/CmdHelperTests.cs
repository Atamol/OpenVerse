using OpenVerse.Common;

namespace OpenVerse.Tests;

public class CmdHelperTests
{
    // CmdHelper's registry is static and process-wide (it mirrors how a real tool registers once at startup),
    // so every fact below uses its own enum member and its own command string - none are shared across facts,
    // even within this file - to avoid tripping RegisterArg's duplicate-flag check against another test.
    enum Key
    {
        ReadArgRoundTrip, MissingFlagIsNull, TrailingFlagWithNoValueIsNull, LastTokenOverallIsNull,
        AliasPrimary, DuplicateFirst, DuplicateSecond, HasFlagPresent, HasFlagAbsent,
        HasFlagIgnoresTakesValue, ManTakesValue, ManSwitch, NeverRegistered,
    }

    [Fact]
    public void ReadArgReturnsTheTokenAfterTheFlag()
    {
        CmdHelper.RegisterArg(Key.ReadArgRoundTrip, new CommandExplanation("d", "readargroundtrip"));
        Assert.Equal("bar", CmdHelper.ReadArg(["--readargroundtrip", "bar"], Key.ReadArgRoundTrip));
    }

    [Fact]
    public void MissingFlagReturnsNull()
    {
        CmdHelper.RegisterArg(Key.MissingFlagIsNull, new CommandExplanation("d", "missingflagisnull"));
        Assert.Null(CmdHelper.ReadArg(["--something-else", "bar"], Key.MissingFlagIsNull));
    }

    // the case this file was specifically asked to cover: "--foo bar" is the registered shape, but the caller
    // only passed "--foo" with nothing after it - ReadArg must return null, not throw and not read past the end
    [Fact]
    public void FlagPresentWithNoTrailingValueReturnsNull()
    {
        CmdHelper.RegisterArg(Key.TrailingFlagWithNoValueIsNull, new CommandExplanation("d", "trailingflagwithnovalueisnull"));
        Assert.Null(CmdHelper.ReadArg(["--trailingflagwithnovalueisnull"], Key.TrailingFlagWithNoValueIsNull));
    }

    // same case, but the flag is not the only token - it is simply the last one, so there is still no "next" token
    [Fact]
    public void FlagAsTheLastOfSeveralTokensStillReturnsNull()
    {
        CmdHelper.RegisterArg(Key.LastTokenOverallIsNull, new CommandExplanation("d", "lasttokenoverallisnull"));
        Assert.Null(CmdHelper.ReadArg(["--unrelated", "x", "--lasttokenoverallisnull"], Key.LastTokenOverallIsNull));
    }

    [Fact]
    public void ReadArgOnAnUnregisteredKeyThrows()
    {
        Assert.Throws<InvalidOperationException>(() => { CmdHelper.ReadArg(["--anything"], Key.NeverRegistered); });
    }

    [Fact]
    public void HasFlagOnAnUnregisteredKeyThrows()
    {
        Assert.Throws<InvalidOperationException>(() => CmdHelper.HasFlag(["--anything"], Key.NeverRegistered));
    }

    [Fact]
    public void AliasesAllResolveToTheSameArg()
    {
        CmdHelper.RegisterArg(Key.AliasPrimary, new CommandExplanation("d", "aliasprimary", "-ap"));
        Assert.Equal("v1", CmdHelper.ReadArg(["-ap", "v1"], Key.AliasPrimary));
        Assert.Equal("v2", CmdHelper.ReadArg(["--aliasprimary", "v2"], Key.AliasPrimary));
    }

    [Fact]
    public void DuplicateCommandAcrossTwoKeysThrows()
    {
        CmdHelper.RegisterArg(Key.DuplicateFirst, new CommandExplanation("d", "duplicatecommand"));
        Assert.Throws<InvalidOperationException>(() =>
            CmdHelper.RegisterArg(Key.DuplicateSecond, new CommandExplanation("d", "duplicatecommand")));
    }

    [Fact]
    public void CommandExplanationWithNoCommandsThrows()
    {
        Assert.Throws<ArgumentException>(() => new CommandExplanation("no commands given"));
    }

    // a boolean switch (TakesValue = false, ex: --help) has no value to read - HasFlag checks presence only
    [Fact]
    public void HasFlagTrueWhenPresentWithNoValue()
    {
        CmdHelper.RegisterArg(Key.HasFlagPresent, new CommandExplanation("d", "hasflagpresent") { TakeValue = false });
        Assert.True(CmdHelper.HasFlag(["--hasflagpresent"], Key.HasFlagPresent));
    }

    [Fact]
    public void HasFlagFalseWhenAbsent()
    {
        CmdHelper.RegisterArg(Key.HasFlagAbsent, new CommandExplanation("d", "hasflagabsent") { TakeValue = false });
        Assert.False(CmdHelper.HasFlag(["--something-else"], Key.HasFlagAbsent));
    }

    // HasFlag is not limited to TakesValue = false args - it only ever checks presence, regardless of the flag
    [Fact]
    public void HasFlagWorksRegardlessOfTakesValue()
    {
        CmdHelper.RegisterArg(Key.HasFlagIgnoresTakesValue, new CommandExplanation("d", "hasflagignorestakesvalue"));
        Assert.True(CmdHelper.HasFlag(["--hasflagignorestakesvalue", "some-value"], Key.HasFlagIgnoresTakesValue));
    }

    [Fact]
    public void GenerateManShowsValuePlaceholderOnlyWhenTakesValue()
    {
        CmdHelper.RegisterArg(Key.ManTakesValue, new CommandExplanation("takes a value", "mantakesvalue"));
        CmdHelper.RegisterArg(Key.ManSwitch, new CommandExplanation("is a switch", "manswitch") { TakeValue = false });
        var man = CmdHelper.GenerateMan();
        Assert.Contains("--mantakesvalue  <value>", man);
        Assert.Contains("is a switch", man);
        Assert.DoesNotContain("--manswitch  <value>", man);
    }
}
