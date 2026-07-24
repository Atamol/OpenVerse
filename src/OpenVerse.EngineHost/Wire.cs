using System;
using System.Collections.Generic;
using System.Reflection;
using Wizard.RoomMatch;

// builds the client's own PlayActions dictionary. keys are the NetworkParameter enum names verbatim:
// ConvertReceiveDataToMakeData filters on Enum.IsDefined(typeof(NetworkParameter), key) and NetworkParameterNames is
// nothing but enum -> Enum.GetName, so there is no separate short wire alphabet
public static class Wire
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public const int SelfVid = 1000;    // whoever is BattlePlayer on this node
    public const int OppoVid = 2000;

    public static WatchDataHandler Handler;

    // targetList is parsed with isWatch:true, so per-target side comes from vid through WatchDataHandler.isOwner, not
    // from an isSelf field. isOwner compares against GameMgr's own viewer id and touches no instance state, so an
    // uninitialized handler is enough, and without one the vid branch NREs and the whole message is dropped
    public static void Init()
    {
        var gm = GameMgr.GetIns();
        var infoT = typeof(NetworkUserInfoData);
        var info = gm.GetNetworkUserInfoData();
        if (info == null)
        {
            info = (NetworkUserInfoData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(infoT);
            var slot = typeof(GameMgr).GetField("_networkUserInfoData", Any);
            slot.SetValue(gm, info);
        }
        infoT.GetField("_selfInfo", Any).SetValue(info, new Dictionary<string, object> { { "viewerId", SelfVid } });

        Handler = (WatchDataHandler)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(WatchDataHandler));

        if (info.GetSelfViewerId() != SelfVid) throw new Exception("viewer id seeding failed");
        if (Handler.isOwner(SelfVid.ToString()) != true) throw new Exception("isOwner(self) should be true");
        if (Handler.isOwner(OppoVid.ToString()) != false) throw new Exception("isOwner(oppo) should be false");
    }

    // TargetData.IsSelf is absolute, not relative to the actor: both GetOpposingCardObjTarget and
    // LookForActionDataToTargetCard read true as "this card lives on BattleEnemy"
    public static Dictionary<string, object> Target(int idx, bool onEnemySide)
        => new Dictionary<string, object>
        {
            { "targetIdx", idx },
            { "vid", onEnemySide ? OppoVid : SelfVid },
        };

    public static Dictionary<string, object> PlayActions(
        NetworkBattleDefine.PlayActionType type, int playIdx, bool isSelf,
        List<Dictionary<string, object>> targets, int cardId, bool withKnownList)
    {
        var d = new Dictionary<string, object>
        {
            { "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            { "type", (int)type },
            { "playIdx", playIdx },
            { "isSelf", isSelf ? 1 : 0 },
        };
        // knownList is what NetworkBattleData.GetPlayCard resolves against, so the stock OperateReceiveChecker needs
        // it. not free: BeforeSettingReceiveData runs every entry through ReplaceReceivedCard against BattleEnemy,
        // which swaps the live instance for a freshly built one. off by default
        if (withKnownList)
            d["knownList"] = new List<object> { new Dictionary<string, object> { { "idx", playIdx }, { "cardId", cardId } } };
        if (targets != null && targets.Count > 0)
        {
            var l = new List<object>();
            foreach (var t in targets) l.Add(t);
            d["targetList"] = l;
        }
        return d;
    }

    public static Dictionary<string, object> Simple() => new Dictionary<string, object>
    {
        { "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
    };

    // NetworkWatchBattleReceiver.ParseSelectSkillData reads "value" positionally: [0] operation, [1]
    // isEvolveTargetSelect, [2] isBurialRite, [3] payload. the payload for Start* is a bare index; for Select/Complete
    // it is one flag char plus a zero-padded 3-digit index (EmitHandUtility.ConvertToThreeDigitCardIndex)
    public static Dictionary<string, object> SelectSkill(
        NetworkBattleSender.SELECT_SKILL_OPERATION op, bool isEvolveSelect, bool isBurialRite,
        string payload, bool actorIsPlayer, bool withEmptyUList = false)
    {
        var d = new Dictionary<string, object>
        {
            { "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            { "vid", actorIsPlayer ? SelfVid : OppoVid },
        };
        // ParseSelectSkillData hands _lastUnapprovedList straight to receiveData.unapprovedList on Select/Complete,
        // and BeforeSettingReceiveData then LINQs over it. without a preceding uList that field is null and the whole
        // message dies inside ConductReceiveData
        if (withEmptyUList) d["uList"] = new List<object>();
        d["value"] = new List<object> { (int)op, isEvolveSelect, isBurialRite, payload };
        return d;
    }

    public static string SelectPayload(bool isPlayerCardFlag, int index)
        => (isPlayerCardFlag ? "1" : "0") + index.ToString().PadLeft(3, '0');
}
