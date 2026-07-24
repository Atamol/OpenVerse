using System;
using System.Collections.Generic;
using UnityEngine;
using Wizard.Battle.Phase;
using Wizard.Battle.UI;
using Wizard.Battle.View.Vfx;
using Wizard.BattleMgr;

// the battle ctor leaves every UI reference null and the scene fills them later, so headlessly they only need to exist
// and do nothing
public class HeadlessTurnPanelControl : ITurnPanelControl
{
    public GameObject GameObject => null;
    public void Initialize(bool isEvoEnableP = true, bool isEvoEnableE = true) { }
    public void StartUI(int turn, int evo, bool isP) { }
    public VfxBase LoadResource() => NullVfx.GetInstance();
}

public class HeadlessDetailPanelControl : IDetailPanelControl
{
    public bool IsShow => false;
    public BattleCardBase _card => null;
    public bool forceEvolutionConfirm { get; set; }
    public UIButton EvolveButton => null;
    public GameObject EvoTargetPanelColliderGameObject => null;
    public DetailPanelControl.ShowRequest CurrentShowRequest => default;
    public EvolutionConfirmation _evolutionConfirmation => null;
    public event Action OnHideOneTime;
    public void UpdateCardDescriptionOnEvent() { }
    public void UpdateCardDescriptionOnEvolutionEvent() { }
    public void Show(BattleManagerBase b, OperateMgr o, BattleCardBase c, DetailPanelControl.ShowRequest r) { }
    public void ShowList(BattleManagerBase b, OperateMgr o, List<BattleCardBase> cards, DetailPanelControl.ShowRequest r, BuffInfo buff, BattleLogItem.CardTextureOption t = BattleLogItem.CardTextureOption.Null, string divergenceId = "", int logTextureId = 0) { }
    public void Hide() { }
    public void SetSize(float percent) { }
    public void UpdateBuffInfo(BattleCardBase targetCard, List<BattlePlayerBase.MyRotationBonusCondition> l) { }
    public void UpdateLogItemBuffInfo(BattleCardBase targetCard) { }
    public void SetScreenPosition(bool right) { }
    public VfxBase ShowEvolutionButton(BattleCardBase card) => NullVfx.GetInstance();
    public void CreateNextPanel() { }
    public void SetKeyBtnActive(List<bool> hasKeyword) { }
    public void ShowKeySubPanel(int page) { }
    public void HideKeySubPanel() { }
    public bool IsDisplayedRight() => false;
    public List<BuffInfo> GetDistinctBuffList(List<BuffInfo> l) => l;
    public List<NetworkBattleReceiver.ReplayBuffInfoLabel> GetBuffDetailLabel(BattleCardBase c) => new List<NetworkBattleReceiver.ReplayBuffInfoLabel>();
}

// the opening phase only reaches for the scene (a battle log window, the 3D field intro), so its Update, which hands
// over to the mulligan phase, is the part that matters
public class HeadlessOpeningPhase : OpeningPhase
{
    public HeadlessOpeningPhase(BattleManagerBase battleMgr) : base(battleMgr) { }
    public override VfxBase Setup() => NullVfx.GetInstance();
}

// UIButton.isEnabled toggles a UnityEngine.Collider, and Collider.set_enabled is an InternalCall in the real
// PhysicsModule. the JIT refuses to compile any method resolving such a call, so the whole property throws
// SecurityException before a single branch runs (a Collider or not makes no difference), and overriding it is the only
// way past. nothing headless reads the value back
public class HeadlessMenuButton : Wizard.WizardUIButton
{
    public override bool isEnabled { get => true; set { } }
}

// Teardown runs when the battle hands over to the result screen, and every line of it before FinishBattle is a scene
// reference (menu button, turn-end button, log window, class info UI, status panels, battery). Setup already works, so
// it is left alone
public class HeadlessMainPhase : MainPhase
{
    public HeadlessMainPhase(BattleManagerBase m, Wizard.Battle.UI.BattleLogManager l) : base(m, l) { }

    public override VfxBase Teardown()
    {
        // FinishBattle is shutdown bookkeeping (clear the quest's special-battle info, stop the AI coroutine), neither
        // of which exists headlessly, and a throw here would block the verdict that follows
        try { _battleManager.FinishBattle(); } catch { }
        return NullVfx.GetInstance();
    }
}

// the real result phase is the result screen itself (win/lose banner, camera, BGM); the only thing on it the engine's
// bookkeeping cares about is OnSetupEnd, which BattleManagerBase hooks to fire OnWin
public class HeadlessResultPhase : IResultPhase
{
    public event Action OnSetupEnd;
    public VfxBase Setup() => InstantVfx.Create(() => OnSetupEnd?.Invoke());
    public VfxWith<IPhase> Update(float dt) => new VfxWith<IPhase>(NullVfx.GetInstance(), null);
    public VfxBase Teardown() => NullVfx.GetInstance();
    public void Pause() { }
}

public class HeadlessPhaseCreator : SingleBattlePhaseCreator
{
    public HeadlessPhaseCreator(BattleManagerBase battleMgr) : base(battleMgr) { }
    public override IPhase CreateOpeningPhase() => new HeadlessOpeningPhase(_battleMgr);
    public override IPhase CreateMainPhase() => new HeadlessMainPhase(_battleMgr, Wizard.Battle.UI.BattleLogManager.GetInstance());
    public override IResultPhase CreateResultPhase(bool winnerIsPlayer) => new HeadlessResultPhase();
}

public class HeadlessContentsCreator : StandardBattleMgrContentsCreator
{
    // StandardBattleMgrContentsCreator seeds itself with new Random().Next(), which BattleManagerBase hands to
    // _stableRandom. a server must be able to replay a battle and a harness to report a number twice, so pin the seed
    public HeadlessContentsCreator(int seed) : base(new Wizard.Battle.Recovery.NullRecoveryRecordManager(), new Wizard.Battle.Replay.NullReplayRecordManager())
    {
        typeof(StandardBattleMgrContentsCreator).GetProperty("RecoveryManager").SetValue(this, new HeadlessRecoveryManager());
        typeof(StandardBattleMgrContentsCreator).GetProperty("RandomSeed").SetValue(this, seed);
    }

    public override IPhaseCreator CreatePhaseCreator(BattleManagerBase battleMgr) => new HeadlessPhaseCreator(battleMgr);
}
