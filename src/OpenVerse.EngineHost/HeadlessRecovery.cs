using System;
using System.Collections;
using UnityEngine;
using Wizard.Battle.Recovery;
using Wizard.Battle.View.Vfx;

// same as NullRecoveryManager except BackGroundId picks an id no case in CreateManager's switch matches, so the battle
// constructs without a 3D field at all
public class HeadlessRecoveryManager : IRecoveryManager
{
    public DataMgr.BattleType BattleType => DataMgr.BattleType.None;
    public bool? DidPlayerGoFirst => null;
    public int RandomSeed => 0;
    public bool HasMulliganInfo => false;
    public int BackGroundId => 0;
    public string BgmId => "NONE";
    public long RecordTime => 0L;
    public int IdxChangeSeed => -1;

    public event Action OnStartRecovery;
    public event Action OnEndDataRecovery;
    public event Action OnEndRecovery;

    public void Setup()
    {
        OnStartRecovery?.Invoke();
        OnEndDataRecovery?.Invoke();
        OnEndRecovery?.Invoke();
    }

    public VfxBase Recovery(BattlePlayer battlePlayer, BattleEnemy battleEnemy, Func<IEnumerator, Coroutine> startCoroutine) => NullVfx.GetInstance();
    public VfxBase UpdateRecovery() => NullVfx.GetInstance();
    public void RecoveryBeforeMulligan() { }
    public VfxBase RecoveryMulligan(BattlePlayer battlePlayer, BattleEnemy battleEnemy) => NullVfx.GetInstance();
    public string RecoveryPopSkillTargetCardName() => string.Empty;
}
