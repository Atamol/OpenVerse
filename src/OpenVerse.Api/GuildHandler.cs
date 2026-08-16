using System.Collections.Concurrent;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

// There is no guild backend and no way to add a button to a client I do not touch, so the one on the home screen is
// reused as the switch for the Unlimited unlocks. Opening the screen flips it, and the search list is where the state
// is written back, since it is the only text on that screen the server gets to fill in
public sealed class GuildHandler
{
    // GuildInfo.eGUILD_STATUS.NOT_JOINING, which sends GuildManager to the search category. The chat category is the
    // other option and would need a whole chat backend to open
    const int NotJoining = 0;
    const int OnlyInvite = 3;  // JoinConditionType, the one that does not offer a join button
    const int Unlimited = 15;  // ActivityType

    // NetworkDefine.MAINTENANCE_TYPE.GUILD_MAINTENANCE. NetworkTask routes 2000-2999 to the each-function maintenance
    // popup, which closes without touching anything, so every guild action can decline with one dismissible dialog
    public const int GuildMaintenance = 2054;

    // Reads reached by moving around the screen, which must not throw a dialog at someone who only browsed. Each
    // returns the empty shape its task parses without checking
    static readonly Dictionary<string, string> EmptyReads = new()
    {
        ["invited_guild_list"] = """{"list":[]}""",
        ["join_request_list"] = """{"list":[]}""",
        ["invite_user_list"] = """{"list":[]}""",
        ["friend_list"] = "[]",
        ["emblem_list"] = """{"guild_emblem_list":[]}""",
    };

    readonly UnlockStore _unlocks;
    readonly ConcurrentDictionary<string, byte> _armed = new();

    public GuildHandler(UnlockStore unlocks) => _unlocks = unlocks;

    // guild/info is not once per visit: the 申請中 tab runs it again (GuildApply.OpenCategory -> UpdateGuildInfo ->
    // GuildManager.UpdateGuildInfo), and NOT_JOINING puts that tab in the menu. Flipping on every one of them undoes
    // the flip the user just made. Only arriving from home counts as a press, and that always passes through mypage
    public void ArmFromHome(string userKey) => _armed[userKey] = 0;

    public static bool CanHandle(string path) =>
        path.Contains("/guild/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/guild_chat/", StringComparison.OrdinalIgnoreCase);

    public (int ResultCode, string Data) Handle(string path, string userKey)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name == "info") return (1, Info(userKey));
        if (name == "search_guild") return (1, Serialize(new Dictionary<string, object> { ["list"] = new[] { Row(userKey) } }));
        if (name == "others_info")
            return (1, Serialize(new Dictionary<string, object>
            {
                ["guild"] = new Dictionary<string, object> { ["detail"] = Row(userKey) },
            }));
        if (EmptyReads.TryGetValue(name, out var empty)) return (1, empty);
        // everything left mutates something: create, join, invite, leave, breakup, remove, chat
        return (GuildMaintenance, "{}");
    }

    // GuildInfo parses max_member_num, max_sub_leader_num, guild_status and usable_stamp_list without checking that
    // they are there, so all four have to be present even with no guild
    string Info(string userKey)
    {
        if (_armed.TryRemove(userKey, out _))
            Console.WriteLine($"  [guild] unlimited unlock -> {(_unlocks.Toggle(userKey) ? "ON" : "OFF")} for {userKey}");
        return Serialize(new Dictionary<string, object>
        {
            ["max_member_num"] = 1,
            ["max_sub_leader_num"] = 0,
            ["guild_status"] = NotJoining,
            ["usable_stamp_list"] = Array.Empty<int>(),
        });
    }

    object Row(string userKey)
    {
        var on = _unlocks.IsOn(userKey);
        var pending = _unlocks.ReloadPending(userKey);
        var state = on ? "ON" : "OFF";
        return new Dictionary<string, object>
        {
            ["guild_id"] = 1,
            ["guild_name"] = pending ? $"アンリミテッド解放: {state} (未反映)" : $"アンリミテッド解放: {state}",
            ["guild_emblem_id"] = 1L,
            ["description"] = pending
                ? "ホームに戻ると確認が出ます．タイトルへ戻るを押すと入り直して反映されます．"
                : on
                    ? "リサージェントカードが使えます．同名は40枚まで入ります．戻すにはもう一度ギルドを開いてください．"
                    : "ギルドをもう一度開くとオンになります．オンの間はリサージェントカードが解禁され，同名カードを40枚まで入れられます．",
            ["join_condition"] = OnlyInvite,
            ["activity"] = Unlimited,
            ["member_num"] = 1,
            ["leader_name"] = "OpenVerse",
            ["leader_viewer_id"] = 0,
        };
    }

    static string Serialize(object o) => JsonSerializer.Serialize(o);
}
