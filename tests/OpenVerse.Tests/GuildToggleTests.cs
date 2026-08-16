using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenVerse.Api;
using OpenVerse.Common;

namespace OpenVerse.Tests;

[Collection("Sqlite")]
public class GuildToggleTests : IDisposable
{
    const string Udid = "udid-a";
    const string Base = "/shadowverse/guild/";

    readonly string _dbPath;
    readonly UnlockStore _unlocks;
    readonly GuildHandler _handler;

    public GuildToggleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ov-guild-{Guid.NewGuid():N}.db");
        _unlocks = new UnlockStore(_dbPath);
        _handler = new GuildHandler(_unlocks);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    (int Code, string Data) Call(string name, string udid = Udid) => _handler.Handle(Base + name, udid);

    // opening the screen from home, which is the only thing that counts as pressing the button
    (int Code, string Data) Press(string udid = Udid)
    {
        _handler.ArmFromHome(udid);
        return Call("info", udid);
    }

    string GuildName(string udid = Udid)
    {
        var json = Call("search_guild", udid).Data;
        return JsonDocument.Parse(json).RootElement.GetProperty("list")[0].GetProperty("guild_name").GetString()!;
    }

    [Theory]
    [InlineData("/shadowverse/guild/info", true)]
    [InlineData("/shadowverse/guild_chat/messages", true)]
    [InlineData("/shadowverse/deck/update", false)]
    [InlineData("/shadowverse/load/index", false)]
    public void CanHandleMatchesOnlyGuildEndpoints(string path, bool expected)
        => Assert.Equal(expected, GuildHandler.CanHandle(path));

    [Fact]
    public void OpeningTheScreenFlipsTheSwitch()
    {
        Assert.False(_unlocks.IsOn(Udid));
        Press();
        Assert.True(_unlocks.IsOn(Udid));
        Press();
        Assert.False(_unlocks.IsOn(Udid));
    }

    // the search list is the only text on that screen the server fills in, so it is where the state is reported
    [Fact]
    public void TheSearchRowReportsTheCurrentState()
    {
        Assert.Contains("OFF", GuildName());
        Press();
        Assert.Contains("ON", GuildName());
    }

    // a flip only reaches the client through login, so saying ON while the running session is still OFF is a lie
    [Fact]
    public void TheSearchRowSaysWhenTheFlipHasNotReachedTheClient()
    {
        _unlocks.MarkServed(Udid);
        Assert.DoesNotContain("未反映", GuildName());
        Press();
        Assert.Contains("未反映", GuildName());
        _unlocks.MarkServed(Udid);
        Assert.DoesNotContain("未反映", GuildName());
    }

    [Fact]
    public void ListingGuildsDoesNotFlipAnything()
    {
        Press();
        Call("search_guild");
        Call("search_guild");
        Assert.True(_unlocks.IsOn(Udid));
    }

    // GuildApply.OpenCategory runs guild/info a second time and NOT_JOINING keeps that tab in the menu, so flipping
    // on every one of them undoes what the user just did
    [Fact]
    public void MovingBetweenTabsDoesNotFlipItBack()
    {
        Press();
        Assert.True(_unlocks.IsOn(Udid));
        Call("info");   // 申請中 tab
        Call("info");
        Assert.True(_unlocks.IsOn(Udid));
    }

    [Fact]
    public void ComingBackFromHomeArmsItAgain()
    {
        Press();
        Call("info");
        Press();
        Assert.False(_unlocks.IsOn(Udid));
    }

    [Fact]
    public void TheSwitchIsPerUser()
    {
        Press();
        Assert.True(_unlocks.IsOn(Udid));
        Assert.False(_unlocks.IsOn("udid-b"));
    }

    [Fact]
    public void TheSwitchSurvivesARestart()
    {
        Press();
        Assert.True(new UnlockStore(_dbPath).IsOn(Udid));
    }

    // GuildInfo reads all four without checking they are there, so a missing one takes the screen down
    [Fact]
    public void InfoCarriesEveryFieldTheClientReadsUnguarded()
    {
        var root = JsonDocument.Parse(Press().Data).RootElement;
        foreach (var key in new[] { "max_member_num", "max_sub_leader_num", "guild_status", "usable_stamp_list" })
            Assert.True(root.TryGetProperty(key, out _), $"{key} is missing");
        Assert.Equal(0, root.GetProperty("guild_status").GetInt32()); // NOT_JOINING -> the search category
    }

    [Fact]
    public void TheSearchRowCarriesEveryFieldGuildDetailInfoReads()
    {
        var row = JsonDocument.Parse(Call("search_guild").Data).RootElement.GetProperty("list")[0];
        foreach (var key in new[]
                 {
                     "guild_id", "guild_name", "guild_emblem_id", "description",
                     "join_condition", "activity", "member_num", "leader_name", "leader_viewer_id",
                 })
            Assert.True(row.TryGetProperty(key, out _), $"{key} is missing");
    }

    // NetworkTask sends 2000-2999 to the each-function maintenance popup, which closes and does nothing else
    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("invite")]
    [InlineData("leave")]
    [InlineData("breakup")]
    [InlineData("remove")]
    [InlineData("change_role")]
    [InlineData("update_description")]
    public void EveryActionDeclinesAsMaintenance(string name)
    {
        Assert.Equal(GuildHandler.GuildMaintenance, Call(name).Code);
        Assert.InRange(GuildHandler.GuildMaintenance, 2000, 2999);
    }

    [Fact]
    public void ChatIsDeclinedTheSameWay()
        => Assert.Equal(GuildHandler.GuildMaintenance, _handler.Handle("/shadowverse/guild_chat/post", Udid).ResultCode);

    // moving between the category tabs fires these, and an error there would pop a dialog at someone just looking
    [Theory]
    [InlineData("invited_guild_list", "list")]
    [InlineData("join_request_list", "list")]
    [InlineData("invite_user_list", "list")]
    [InlineData("emblem_list", "guild_emblem_list")]
    public void BrowsingReadsComeBackEmptyRatherThanFailing(string name, string arrayKey)
    {
        var (code, data) = Call(name);
        Assert.Equal(1, code);
        Assert.Empty(JsonDocument.Parse(data).RootElement.GetProperty(arrayKey).EnumerateArray());
    }

    // GuildFriendListTask walks data itself, not data.list
    [Fact]
    public void TheFriendListIsABareArray()
    {
        var (code, data) = Call("friend_list");
        Assert.Equal(1, code);
        Assert.Empty(JsonDocument.Parse(data).RootElement.EnumerateArray());
    }

    [Fact]
    public void DecliningAnActionDoesNotFlipTheSwitch()
    {
        Press();
        Call("create");
        Call("join");
        Assert.True(_unlocks.IsOn(Udid));
    }
}
