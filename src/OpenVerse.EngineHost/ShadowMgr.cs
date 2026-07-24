using System;
using System.Collections.Generic;
using Wizard;
using Wizard.Battle;
using Wizard.Battle.View.Vfx;
using Wizard.BattleMgr;

public class ShadowBattlePlayer : BattlePlayer
{
    public ShadowBattlePlayer(BattleManagerBase m, BattleCamera c, BackGroundBase bg, IInnerOptionsBuilder b)
        : base(m, c, bg, b) { }

    public override BattleCardBase CreateCard(int cardId, int cardIndex, bool isChoiceBrave = false)
    {
        var prm = CardMaster.GetInstanceForBattle().GetCardParameterFromId(cardId);
        return BattleMgr.CreateBattleCard(cardId, IsPlayer, null, prm, this, cardIndex);
    }
}

public class ShadowBattleEnemy : BattleEnemy
{
    public ShadowBattleEnemy(BattleManagerBase m, BattleCamera c, BackGroundBase bg, IInnerOptionsBuilder b)
        : base(m, c, bg, b) { }

    public override BattleCardBase CreateCard(int cardId, int cardIndex, bool isChoiceBrave = false)
    {
        var prm = CardMaster.GetInstanceForBattle().GetCardParameterFromId(cardId);
        return BattleMgr.CreateBattleCard(cardId, IsPlayer, null, prm, this, cardIndex);
    }
}

// InPlayCardReflection.Attack registers the attacker/target pair with the player's AttackSelectControl only so the
// arrow UI can draw, and there is no view here
public class ShadowInPlayCardReflection : InPlayCardReflection
{
    public ShadowInPlayCardReflection(BattleManagerBase m, OperateMgr o) : base(m, o) { }
    protected override void RegisterPairToAttackSelectControl(BattleCardBase a, BattleCardBase t) { }
}

public class ShadowOperateReceive : OperateReceive
{
    public ShadowOperateReceive(NetworkBattleManagerBase m, RegisterActionManager r, OperateMgr o, NetworkBattleData d)
        : base(m, r, o, d) { }
    protected override InPlayCardReflection CreateNetworkInPlayAction() => new ShadowInPlayCardReflection(_battleMgr, _operateMgr);
}

// WatchOperationCollection is the only collection that honours _isPlayer on both sides; NetworkOperationCollection
// hardcodes BattleEnemy. its watch-only ctor leaves _watchBattleMgr null, so every member that reaches through it
// (all presentation) is stubbed here
public class ShadowOperationCollection : WatchOperationCollection
{
    public ShadowOperationCollection(NetworkBattleManagerBase m, OperateMgr o, NetworkBattleReceiver.ReceiveData d, NetworkBattleData n, bool isPlayer)
        : base(m, o, d, n, isPlayer) { }

    protected override void PlayCancelSlide() { }
    public override void TouchOperation() { }
    public override void SlideObject() { }
    public override void TurnEndReady() { }
    public override void SelectObjectOperation() { }

    // same dispatch as the stock body minus SlideObjectReceiveCtrl and BattleView.ClearSelectSkillActCard, both of
    // which go through the null _watchBattleMgr
    public override void SelectSkillOperation(PlayHandCardReflection play, InPlayCardReflection inPlay)
    {
        var act = _receivedData._isEvolveTargetSelect
            ? (ReceivePlayActionsReflectionBase)inPlay
            : play;
        switch (_receivedData._selectSkillOperation)
        {
            case NetworkBattleSender.SELECT_SKILL_OPERATION.StartSelect:
                CheckStateAndCancel(play, inPlay, _receivedData.isSelf);
                act.CurrentState = ReceivePlayActionsReflectionBase.SelectChoiceState.SELECT;
                act.StartSelect(_receivedData.idx, _receivedData.isSelf);
                break;
            case NetworkBattleSender.SELECT_SKILL_OPERATION.SelectCard:
                act.SelectCard(_receivedData._selectedCardIndex, TargetIsPlayerSide(), _receivedData._isEvolveTargetSelect,
                               _receivedData.isSelf, _receivedData._isBurialRiteSelect, false, false);
                break;
            case NetworkBattleSender.SELECT_SKILL_OPERATION.CompleteSelect:
                act.CurrentState = ReceivePlayActionsReflectionBase.SelectChoiceState.NONE;
                act.CompleteSelectCard(_receivedData._selectedCardIndex, TargetIsPlayerSide(), _receivedData._isEvolveTargetSelect,
                                       _receivedData.isSelf, _receivedData._isBurialRiteSelect, _receivedData.IsChoiceBraveSelect);
                break;
            case NetworkBattleSender.SELECT_SKILL_OPERATION.CancelSelect:
                act.CurrentState = ReceivePlayActionsReflectionBase.SelectChoiceState.NONE;
                act.CancelSelect(_receivedData.isSelf);
                break;
            default:
                UnityEngine.Debug.LogError("select skill op not wired in the shadow host: " + _receivedData._selectSkillOperation);
                break;
        }
    }

    // transcription of the private WatchOperationCollection.IsTargetSelf
    bool TargetIsPlayerSide() => _receivedData.isSelf == _receivedData._isPlayerCard;
    public override void TurnEndWithSkillActivationOperation(PlayHandCardReflection p, InPlayCardReflection i) { }
    public override void MaintenanceOperation() { }
    public override void ChatStampOperation() { }

    // the stock body calls JudgeResultReceive then BattleFinishReceiveAfterFinishBattleSend, which emits on the wire
    public static NetworkBattleReceiver.RESULT_CODE LastFinishCode = NetworkBattleReceiver.RESULT_CODE.NotFinish;
    public override void BattleFinishOperation() { LastFinishCode = _receivedData.result; }
    public override void JudgeResultOperation() { LastFinishCode = _receivedData.result; }
}

// every checker this drives is a socket-liveness watchdog (disconnect, missing turn start) on a coroutine, and
// BattleCoroutine needs a scene prefab. none of it is rules
public class NullReceiveIntervalTrigger : ReceiveIntervalTrigger
{
    public override void ReceiveDataCheck(NetworkBattleManagerBase m, NetworkBattleData d, bool isPlayer, bool isExTurn) { }
}

public class ShadowMgr : NetworkBattleManagerBase
{
    public ShadowMgr(IBattleMgrContentsCreator c) : base(c)
    {
        OperateReceive = new ShadowOperateReceive(this, RegisterActionManager, OperateMgr, networkBattleData);
        receiveIntervalTrigger = new NullReceiveIntervalTrigger();
        // NetworkSkill_token_draw goes to CreateBattleCardWithGameObject directly, so the players' CreateCard override
        // never sees it and the token creation lands on a card prefab. this is the client's own view-less card builder,
        // the one recovery installs, a different seam from IsRecovery that sets no other flag
        SetupCreateBattleCardFunc(true);
        // the base NetworkBattleReceiver never parses the SelectSkill payload: _selectSkillOperation, _isPlayerCard and
        // _isEvolveTargetSelect are only ever assigned in NetworkWatchBattleReceiver. a node that ingests both sides
        // needs the watch receiver, and its ctor takes the plain manager
        typeof(NetworkBattleManagerBase)
            .GetField("networkReceiver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .SetValue(this, new NetworkWatchBattleReceiver(this));
    }

    public override bool IsVirtualBattle => true;

    protected override BattlePlayer CreateBattlePlayer()
        => new ShadowBattlePlayer(this, _battleCamera, _backGround, CreatePlayerInnerOptionsBuilder());

    protected override BattleEnemy CreateBattleEnemy()
        => new ShadowBattleEnemy(this, _battleCamera, _backGround, CreateEnemyInnerOptionsBuilder());

    // records what the stock client would have done with each message without changing the routing, so a silent drop
    // shows up as a checker verdict rather than as nothing at all
    public readonly List<string> CheckerVerdicts = new List<string>();
    public bool LastCheckerPassed = true;

    protected override NetworkOperationCollectionBase CreateNetworkOperationCollection(NetworkBattleReceiver.ReceiveData d, bool isPlayer)
    {
        LastCheckerPassed = IsOperateReceiveCheck();
        CheckerVerdicts.Add(d.dataUri + "/" + d.actionType + "/isPlayer=" + isPlayer + " -> " + (LastCheckerPassed ? "pass" : "FAIL"));
        return new ShadowOperationCollection(this, OperateMgr, d, networkBattleData, isPlayer);
    }

    // base reaches SBattleLoad.m_TurnEndBtnUI; everything above that line is BattleManagerBase.StartOpening, replayed
    // here because a grandparent call is not expressible
    public override void StartOpening(int firstAttack)
    {
        SetupInitialGameState(firstAttack == 0, true, 20, 20);
        VfxMgr.RegisterSequentialVfx(ChangePhase(PhaseCreator.CreateOpeningPhase()));
        VfxMgr.RegisterSequentialVfx(BattlePlayer.StartSkillWhenBattleStart(new SkillProcessor()));
    }

    // the networked override is the finish handshake: it hangs AckEmitBattleFinish off RealTimeNetworkAgent.OnAck
    // (null here) and emits TurnEndFinal. the judgement it gates on is kept, the handshake is not
    public NetworkBattleReceiver.RESULT_CODE TerminalCode = NetworkBattleReceiver.RESULT_CODE.NotFinish;

    public override VfxBase JudgeBattleResult()
    {
        var c = JudgeCurrentFinishStatus();
        if (c != NetworkBattleReceiver.RESULT_CODE.NotFinish) TerminalCode = c;
        return NullVfx.GetInstance();
    }

    // both of these are wire emissions with no rules behind them
    protected override void SetupNetworkEvent(bool isRecovery) { }
    public override void SendEcho(int playIndex, NetworkBattleDefine.PlayActionType t, bool isNotActiveSeq = false, bool isTurnStart = false) { }
}
