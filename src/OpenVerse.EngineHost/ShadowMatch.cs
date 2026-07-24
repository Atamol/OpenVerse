using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Wizard;
using Wizard.BattleMgr;

namespace OpenVerse.EngineHost
{
    // a passive observer of a live relayed match: the relay replays every battle message into a real headless engine so
    // its reading of the board can be compared with what the clients did. the relay must never notice it exists, so
    // every entry point swallows and reports rather than throwing (a wrong observation costs a log line, a thrown one
    // costs the players their game), and a match that goes wrong is closed, not retried. the surface is flat (handles,
    // primitives, Dictionary) because the net10 server calls it by reflection and cannot reference net48 types
    public static class ShadowMatch
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static string LastError { get; private set; }

        static readonly Dictionary<int, ShadowBattle> _live = new Dictionary<int, ShadowBattle>();
        static int _nextHandle = 1;

        public static bool Boot(string cardMasterCsv)
        {
            try
            {
                Headless.Boot(cardMasterCsv);
                Wire.Init();
                return true;
            }
            catch (Exception e) { LastError = Headless.Root(e); return false; }
        }

        public static int CardCount => Headless.CardCount;

        /// <returns>a handle, or -1 with LastError set</returns>
        public static int Create(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck, int[] playerHand, int[] enemyHand)
        {
            try
            {
                // the engine keeps the current battle on GameMgr, so a second one would silently steal the first's
                // manager. one observed match at a time; the rest of the server is unaffected either way
                if (_live.Count > 0) { LastError = "a shadow match is already running"; return -1; }
                var b = ShadowBattle.Start(seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand);
                int h = _nextHandle++;
                _live[h] = b;
                return h;
            }
            catch (Exception e) { LastError = Headless.Root(e); return -1; }
        }

        /// <returns>"" when the engine applied it, otherwise why it did not</returns>
        public static string Ingest(int handle, string uri, Dictionary<string, object> body, bool isPlayer)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return "no such match";
            try { return b.Ingest(uri, body, isPlayer); }
            catch (Exception e) { return "threw: " + Headless.Root(e); }
        }

        /// <returns>the engine's RESULT_CODE, or 0 (NotFinish) when it cannot be read</returns>
        public static int Verdict(int handle)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return 0;
            try { return (int)b.Mgr.JudgeCurrentFinishStatus(); }
            catch (Exception e) { LastError = Headless.Root(e); return 0; }
        }

        // live cost of a hand card, or -1. the one number the relay cannot derive: a spellboost discount never rides
        // the wire (NetworkSkill_cost_change.IsSend is false while the card is face-down in hand)
        public static int CostOf(int handle, bool isSelfPlayer, int idx)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return -1;
            try { return b.CostOf(isSelfPlayer, idx); }
            catch (Exception e) { LastError = Headless.Root(e); return -1; }
        }

        // which card sits at an index, or 0. the actor leaves cardId 0 for anything leaving a zone the peer can't see
        // (the real server named it); one query over every zone replaces a per-route reconstruction (deck summon,
        // reanimate, and so on)
        public static int CardIdOf(int handle, bool isSelfPlayer, int idx)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return 0;
            try { return b.CardIdOf(isSelfPlayer, idx); }
            catch (Exception e) { LastError = Headless.Root(e); return 0; }
        }

        // answers the skill-condition queries the actor puts on the wire but never answers, one row per answerable spec
        // (receive-side wire keys). must run PRE-play: the evaluation lifts the played card out of hand to match the
        // state the actor evaluated in, which only holds before the play is ingested
        public static List<object> AnswerConditions(int handle, bool isSelfPlayer, int cardIdx, List<object> specs)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return new List<object>();
            try { return global::Answer.AnswerConditions(b.Mgr, isSelfPlayer, cardIdx, specs); }
            catch (Exception e) { LastError = Headless.Root(e); return new List<object>(); }
        }

        /// <summary>one line of board state, for comparing against what the clients report</summary>
        public static string State(int handle)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return "";
            try { return b.State(); }
            catch (Exception e) { return "unreadable: " + Headless.Root(e); }
        }

        public static void Close(int handle)
        {
            _live.Remove(handle);
        }

        public static int LiveCount => _live.Count;
    }

    sealed class ShadowBattle
    {
        public ShadowMgr Mgr;

        public static ShadowBattle Start(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck,
                                         int[] playerHand, int[] enemyHand)
        {
            UnityEngine.Random.InitState(seed);
            var mgr = new ShadowMgr(new HeadlessContentsCreator(seed));

            Headless.T("GameMgr").GetField("_battleMgr", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(Headless.GameMgrIns, mgr);
            typeof(BattlePlayer).GetProperty("PlayerBattleView").SetValue(mgr.BattlePlayer, new Wizard.Battle.View.NullPlayerView());
            typeof(BattleEnemy).GetProperty("BattleEnemyView").SetValue(mgr.BattleEnemy, new Wizard.Battle.View.NullBattlePlayerView());
            mgr.TurnPanelControl = new HeadlessTurnPanelControl();
            mgr.BattleUIContainer = new BattleUIContainer();
            Headless.SeedInstance(mgr.BattleUIContainer, 2);
            mgr.BtlUIContainer = new UnityEngine.GameObject("BtlUI");
            mgr.BtlContainer = new UnityEngine.GameObject("Btl");
            mgr.DetailMgr.DetailPanelControl = new HeadlessDetailPanelControl();
            HeadlessFix.Apply(mgr);

            // decks are filled after this, not before: SetupBattlePlayersEvent is what subscribes OnEvolveEvent, and
            // filling first makes it subscribe twice, which spends two EP per evolution and lets EP go negative
            mgr.SetupBattlePlayersEvent();
            Fill(mgr, mgr.BattlePlayer, true, playerDeck);
            Fill(mgr, mgr.BattleEnemy, false, enemyDeck);
            mgr.SetupInitialGameState(playerFirst, true, 20, 20);
            mgr.StartOpening(playerFirst ? 0 : 1);
            Pump(mgr);

            // The opening hand is dealt to the real clients by the relay's Deal/Swap, which never reach the shadow, and
            // the engine's own OnReceiveDeal hook is null, so without this the shadow's hand stays empty, every play
            // finds its card still in the deck, and the whole match is a silent no-op. Draw the exact post-mulligan
            // indices the relay computed so the board matches what the clients hold.
            DealOpeningHand(mgr, mgr.BattlePlayer, playerHand);
            DealOpeningHand(mgr, mgr.BattleEnemy, enemyHand);
            Pump(mgr);

            return new ShadowBattle { Mgr = mgr };
        }

        static void Fill(BattleManagerBase mgr, BattlePlayerBase p, bool isPlayer, int[] ids)
        {
            var master = CardMaster.GetInstanceForBattle();
            p.cardTotalNum = 1;
            foreach (var id in ids)
            {
                var card = mgr.CreateBattleCard(id, isPlayer, null, master.GetCardParameterFromId(id), p, p.cardTotalNum);
                p.cardTotalNum++;
                p.AddToDeck(card);
            }
            p.BattleStartDeckCardList = new List<BattleCardBase>(p.DeckCardList);
        }

        // draw the opening-hand cards the relay named, by their stable deck Index, through the engine's own DrawCards so
        // IsInHand and the deck list stay consistent. a later play references them by index, so they must be in hand first
        static void DealOpeningHand(BattleManagerBase mgr, BattlePlayerBase p, int[] handIdx)
        {
            if (handIdx == null || handIdx.Length == 0) return;
            var want = handIdx.ToHashSet();
            var draw = p.DeckCardList.Where(c => c != null && want.Contains(c.Index)).ToList();
            if (draw.Count == 0) return;
            mgr.VfxMgr.RegisterSequentialVfx(p.DrawCards(draw, new SkillProcessor(), isOpen: false, isMulligan: true).Vfx);
        }

        public string Ingest(string uri, Dictionary<string, object> body, bool isPlayer)
        {
            NetworkBattleDefine.NetworkBattleURI parsed;
            if (!Enum.TryParse(uri, out parsed)) return "unknown uri " + uri;

            var recv = (NetworkBattleReceiver)typeof(NetworkBattleManagerBase)
                .GetField("networkReceiver", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(Mgr);

            NormalizeSelfTargetList(body, isPlayer);
            // the shadow is never told which card either side drew, so every drawn card is still in its deck; put the
            // cards this message needs where the wire says they are before the engine reads it
            int type = body.TryGetValue("type", out var t) && t is not null ? Convert.ToInt32(t) : -1;
            int playIdx = body.TryGetValue("playIdx", out var p) && p is not null ? Convert.ToInt32(p) : -1;
            ShadowReconciler.Repair(Mgr, uri, body, isPlayer, type, playIdx);

            if (!recv.ReceivedMessage(parsed, true, body, isPlayer, Wire.Handler))
                return "receiver rejected it";
            // state does not move until the queue drains, so a caller that reads State() straight after would see the
            // board as it was before the message
            Pump(Mgr);
            // the engine drew its own deck top wherever the play drew a card, so hoist the index the wire actually
            // named while this play is still the last thing that happened
            ShadowReconciler.RepairAfter(Mgr, uri, body, isPlayer);
            Pump(Mgr);
            return "";
        }

        // Some vfx wait on an asset load that never completes headless; Release cuts those loose and the queue is
        // pumped again. A queue that still will not drain is left alone rather than spun on.
        static void Pump(BattleManagerBase mgr, int maxFrames = 600)
        {
            for (int round = 0; round < 8; round++)
            {
                for (int i = 0; i < maxFrames; i++)
                {
                    bool end;
                    try { end = mgr.VfxMgr.IsEnd; } catch { return; }
                    if (end) return;
                    try { mgr.VfxMgr.Update(1f / 60f); } catch { return; }
                }
                int freed;
                try { freed = VfxUnstick.Release(mgr.VfxMgr); } catch { return; }
                if (freed == 0) return;
            }
        }

        // every zone, not just the hand: the whole point is that the caller should not have to know where the card
        // came from
        public int CardIdOf(bool isSelfPlayer, int idx)
        {
            BattlePlayerBase side = isSelfPlayer ? (BattlePlayerBase)Mgr.BattlePlayer : Mgr.BattleEnemy;
            foreach (var zone in new IEnumerable<BattleCardBase>[]
                     { side.HandCardList, side.DeckCardList, side.CemeteryList, side.BanishList, side.InPlayCards })
                if (zone != null)
                    foreach (var c in zone)
                        if (c != null && c.Index == idx) return c.CardId;
            return 0;
        }

        // The real client always puts targets in oppoTargetList (-> OpponentTargetDataList), but
        // WatchOperationCollection reads PlayerTargetDataList for a self-authored action (isPlayer=true), so a
        // self-side attack/select finds an empty bucket and InPlayCardReflection.Attack indexes [0] on it. Rewrite
        // oppoTargetList -> targetList for self-side and re-express the sender-relative isSelf as a vid, so the
        // isWatch parse yields the absolute TargetData.IsSelf the resolver expects. Enemy-side (isPlayer=false) is
        // already correct: that sender IS BattleEnemy, so its oppoTargetList lands where the collection reads.
        static void NormalizeSelfTargetList(Dictionary<string, object> body, bool isPlayer)
        {
            if (!isPlayer) return;
            if (!body.TryGetValue("oppoTargetList", out var v) || v is not System.Collections.IList oppo || oppo.Count == 0) return;

            var targetList = new List<object>();
            foreach (var item in oppo)
            {
                if (item is not IDictionary<string, object> o) { targetList.Add(item); continue; }
                var e = new Dictionary<string, object>();
                foreach (var kv in o) if (kv.Key != "isSelf") e[kv.Key] = kv.Value;
                int rel = o.TryGetValue("isSelf", out var s) && s is not null ? Convert.ToInt32(s) : 0;
                e["vid"] = rel == 1 ? Wire.SelfVid : Wire.OppoVid;
                targetList.Add(e);
            }
            body.Remove("oppoTargetList");
            body["targetList"] = targetList;
        }

        public int CostOf(bool isSelfPlayer, int idx)
        {
            BattlePlayerBase side = isSelfPlayer ? (BattlePlayerBase)Mgr.BattlePlayer : Mgr.BattleEnemy;
            foreach (var c in side.HandCardList)
                if (c.Index == idx) return c.Cost;
            return -1;
        }

        public string State()
        {
            var p = Mgr.BattlePlayer;
            var e = Mgr.BattleEnemy;
            return Side("P", p) + " | " + Side("E", e);
        }

        static string Side(string tag, BattlePlayerBase s)
            => tag + " life=" + s.Class.Life + " pp=" + s.Pp + " ep=" + s.CurrentEpCount
             + " hand=" + s.HandCardList.Count + " deck=" + s.DeckCardList.Count + " board=" + s.InPlayCards.Count();
    }
}
