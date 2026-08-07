using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Wizard;
using Wizard.BattleMgr;

namespace OpenVerse.EngineHost
{
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

        public static int Create(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck, int[] playerHand, int[] enemyHand)
        {
            try
            {
                // The engine keeps the current battle on GameMgr, so a second one would silently steal the first's
                // manager. One observed match at a time. The rest of the server is unaffected either way
                if (_live.Count > 0) { LastError = "a shadow match is already running"; return -1; }
                var b = ShadowBattle.Start(seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand);
                int h = _nextHandle++;
                _live[h] = b;
                return h;
            }
            catch (Exception e) { LastError = Headless.Root(e); return -1; }
        }

        // A card put back into a deck gets a fresh slot, drawn from a XorShift the client seeds off idxChangeSeed in the
        // Deal it receives. The shadow is dealt its opening hand directly and never sees that Deal, so without this it
        // skips the reshuffle both clients perform. enrolment is also gated on IsMulliganEnd, which only the mulligan
        // flow sets
        public static bool SetDeckMirror(int handle, int selfIdxSeed, int oppoIdxSeed)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return false;
            try
            {
                b.Mgr.CreateXorShift(selfIdxSeed, oppoIdxSeed);
                b.Mgr.IsMulliganEnd = true;
                return true;
            }
            catch (Exception e) { LastError = Headless.Root(e); return false; }
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

        // Live cost of a hand card, or -1. The one number the relay cannot derive: a spellboost discount never rides
        // the wire (NetworkSkill_cost_change.IsSend is false while the card is face-down in hand)
        public static int CostOf(int handle, bool isSelfPlayer, int idx)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return -1;
            try { return b.CostOf(isSelfPlayer, idx); }
            catch (Exception e) { LastError = Headless.Root(e); return -1; }
        }

        // which card sits at an index, or 0. The actor leaves cardId 0 for anything leaving a zone the peer can't see
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

        // The fields the wire cannot carry, read off the card object. Empty when this board cannot answer
        public static Dictionary<string, object> Project(int handle, bool isSelfPlayer, int idx)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return new Dictionary<string, object>();
            try { return b.Project(isSelfPlayer, idx); }
            catch (Exception e) { LastError = Headless.Root(e); return new Dictionary<string, object>(); }
        }

        // A client never receives its own action, and the two paths do not draw from StableRandom the same way, so a
        // board fed its own play as a received message is a spectator of its client rather than a copy of it.
        // anything other than "" means the caller should fall back to the receive path
        public static string PlayByIntent(int handle, int idx, List<object> selectIdxs, List<object> choiceIds)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return "no such match";
            try { return b.PlayByIntent(idx, selectIdxs, choiceIds); }
            catch (Exception e) { var why = Headless.Root(e); LastError = why; return why; }
        }

        // the stock client's own verdict on the last message this board took: OperateReceiveChecker.IsOperateReceive,
        // the test a real client runs before applying anything. "" when there is nothing to report
        public static string LastVerdict(int handle)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return "";
            try
            {
                var v = b.Mgr.CheckerVerdicts;
                return v.Count == 0 ? "" : (b.Mgr.LastCheckerPassed ? "pass " : "FAIL ") + v[v.Count - 1];
            }
            catch (Exception e) { LastError = Headless.Root(e); return ""; }
        }

        // The shared StableRandom cursor, or -1. spin for a receiver is the actor's cursor delta minus its own
        public static int RandomCursor(int handle)
        {
            ShadowBattle b;
            if (!_live.TryGetValue(handle, out b)) return -1;
            try { return b.RandomCursor(); }
            catch (Exception e) { LastError = Headless.Root(e); return -1; }
        }

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
            ShadowReconciler.Reset();
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
            // finds its card still in the deck, and the whole match is a silent no-op. draw the exact post-mulligan
            // indices the relay computed so the board matches what the clients hold
            DealOpeningHand(mgr, mgr.BattlePlayer, playerHand);
            DealOpeningHand(mgr, mgr.BattleEnemy, enemyHand);
            Pump(mgr);

            return new ShadowBattle { Mgr = mgr };
        }

        const string ShadowKnown = "shadowKnown";

        const int HandZone = 10, FieldZone = 20;

        static bool Holds(BattlePlayerBase p, int idx)
        {
            foreach (var zone in new IEnumerable<BattleCardBase>[]
                     { p.HandCardList, p.DeckCardList, p.CemeteryList, p.BanishList, p.ClassAndInPlayCardList })
                if (zone != null)
                    foreach (var c in zone.ToList())
                        try { if (c != null && c.Index == idx) return true; } catch { }
            return false;
        }

        // A card created mid-match exists on no board until the wire names it, so the receiver throws in OperateMgr.
        // CreateBattleCard is the engine's own token path and needs no view
        static void Materialize(BattleManagerBase mgr, Dictionary<string, object> body, bool isPlayer, int type, int playIdx)
        {
            object raw;
            if (!body.TryGetValue(ShadowKnown, out raw)) return;
            body.Remove(ShadowKnown);
            var entries = raw as List<object>;
            if (entries == null) return;

            // whoever authored the message owns the cards it names
            BattlePlayerBase side = isPlayer ? (BattlePlayerBase)mgr.BattlePlayer : mgr.BattleEnemy;
            var master = CardMaster.GetInstanceForBattle();
            var from = new Dictionary<int, int>();
            foreach (var m in ShadowReconciler.Moves(body)) if (!from.ContainsKey(m.idx)) from[m.idx] = m.from;

            foreach (var e in entries.OfType<Dictionary<string, object>>())
            {
                object idxO, idO;
                if (!e.TryGetValue("idx", out idxO) || !e.TryGetValue("cardId", out idO)) continue;
                if (idxO == null || idO == null) continue;
                int idx = Convert.ToInt32(idxO), cardId = Convert.ToInt32(idO);
                if (idx <= 0 || cardId <= 0) continue;
                if (Holds(mgr.BattlePlayer, idx) || Holds(mgr.BattleEnemy, idx)) continue;

                CardParameter param;
                try { param = master.GetCardParameterFromId(cardId); } catch { continue; }
                if (param == null) continue;

                BattleCardBase card;
                try { card = mgr.CreateBattleCard(cardId, side.IsPlayer, null, param, side, idx); } catch { continue; }
                if (card == null) continue;

                // an attack names a card already in play, everything else comes from hand, and the engine moves it on itself
                int zone = from.TryGetValue(idx, out var f) && f >= 0 ? f
                         : idx == playIdx && (type == 10 || type == 20 || type == 21) ? FieldZone
                         : HandZone;
                try
                {
                    if (zone == FieldZone) side.ClassAndInPlayCardList.Add(card);
                    else side.HandCardList.Add(card);
                    Built++;
                }
                catch { }
            }
        }

        public static int Built;

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

        static void DealOpeningHand(BattleManagerBase mgr, BattlePlayerBase p, int[] handIdx)
        {
            if (handIdx == null || handIdx.Length == 0) return;
            var want = handIdx.ToHashSet();
            var draw = p.DeckCardList.Where(c => c != null && want.Contains(c.Index)).ToList();
            if (draw.Count == 0) return;
            mgr.VfxMgr.RegisterSequentialVfx(p.DrawCards(draw, new SkillProcessor(), isOpen: false, isMulligan: true).Vfx);
        }

        bool _sawTurnStart;

        public string Ingest(string uri, Dictionary<string, object> body, bool isPlayer)
        {
            if (uri == "TurnStart") _sawTurnStart = true;

            NetworkBattleDefine.NetworkBattleURI parsed;
            if (!Enum.TryParse(uri, out parsed)) return "unknown uri " + uri;

            var recv = (NetworkBattleReceiver)typeof(NetworkBattleManagerBase)
                .GetField("networkReceiver", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(Mgr);

            NormalizeSelfTargetList(body, isPlayer);
            // The shadow is never told which card either side drew, so every drawn card is still in its deck. Put the
            // cards this message needs where the wire says they are before the engine reads it
            int type = body.TryGetValue("type", out var t) && t is not null ? Convert.ToInt32(t) : -1;
            int playIdx = body.TryGetValue("playIdx", out var p) && p is not null ? Convert.ToInt32(p) : -1;
            Materialize(Mgr, body, isPlayer, type, playIdx);
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

        // some vfx wait on an asset load that never completes headless, and Release cuts those loose and the queue is
        // pumped again. A queue that still will not drain is left alone rather than spun on
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

        public int CardIdOf(bool isSelfPlayer, int idx)
        {
            if (!Trusted) return 0;
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
        // isWatch parse yields the absolute TargetData.IsSelf the resolver expects. enemy-side (isPlayer=false) is
        // already correct: that sender IS BattleEnemy, so its oppoTargetList lands where the collection reads
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

        // without the turn messages the shadow is not playing the same match at all, so it has nothing to say. its Pp is
        // deliberately not part of this: a hand card's cost comes from its own modifiers, and a shadow that overspent
        // still knows what its cards cost. The caller clamps anything it takes to the master base cost
        bool Trusted => _sawTurnStart;

        public int CostOf(bool isSelfPlayer, int idx)
        {
            if (!Trusted) return -1;
            BattlePlayerBase side = isSelfPlayer ? (BattlePlayerBase)Mgr.BattlePlayer : Mgr.BattleEnemy;
            foreach (var c in side.HandCardList)
                if (c.Index == idx) return c.Cost;
            return -1;
        }

        // UNTESTED lead on the 13-of-16 charge undercount: this path skips ShadowReconciler.Repair, and
        // MirrorPair.Observe skips the actor's Observe once an intent play succeeds, so a card the wire put in hand can
        // still sit in the shadow's deck where no charge reaches it. Wire the body through and call Repair to settle it
        public string PlayByIntent(int idx, List<object> selectIdxs, List<object> choiceIds)
        {
            if (!Trusted) return "not trusted yet";
            var hand = Mgr.BattlePlayer.HandCardList;
            BattleCardBase card = null;
            foreach (var c in hand) if (c != null && c.Index == idx) { card = c; break; }
            if (card == null) return "idx " + idx + " is not in this board's hand";

            var selected = new List<BattleCardBase>();
            if (selectIdxs != null)
                foreach (var o in selectIdxs)
                {
                    var want = Convert.ToInt32(o);
                    var found = FindAnywhere(want);
                    if (found == null) return "target idx " + want + " is not on this board";
                    selected.Add(found);
                }

            List<int> choices = null;
            if (choiceIds != null && choiceIds.Count > 0)
            {
                choices = new List<int>();
                foreach (var o in choiceIds) choices.Add(Convert.ToInt32(o));
            }

            Mgr.OperateMgr.InitSetCard(card, true);
            Mgr.OperateMgr.PlayCard(card, true, selected, false, choices);
            return "";
        }

        BattleCardBase FindAnywhere(int idx)
        {
            foreach (BattlePlayerBase side in new BattlePlayerBase[] { Mgr.BattlePlayer, Mgr.BattleEnemy })
                foreach (var zone in new IEnumerable<BattleCardBase>[]
                         { side.InPlayCards, side.HandCardList, side.DeckCardList, side.CemeteryList, side.BanishList })
                {
                    if (zone == null) continue;
                    foreach (var c in zone) if (c != null && c.Index == idx) return c;
                }
            return null;
        }

        public int RandomCursor()
        {
            var f = typeof(BattleManagerBase).GetField("stableRandomCount",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return f == null ? -1 : Convert.ToInt32(f.GetValue(Mgr));
        }

        public Dictionary<string, object> Project(bool isSelfPlayer, int idx)
        {
            var r = new Dictionary<string, object>();
            if (!Trusted) return r;
            BattlePlayerBase side = isSelfPlayer ? (BattlePlayerBase)Mgr.BattlePlayer : Mgr.BattleEnemy;
            foreach (var zone in new IEnumerable<BattleCardBase>[]
                     { side.HandCardList, side.DeckCardList, side.CemeteryList, side.BanishList, side.InPlayCards })
            {
                if (zone == null) continue;
                foreach (var c in zone)
                {
                    if (c == null || c.Index != idx) continue;
                    r["idx"] = c.Index;
                    r["cardId"] = c.CardId;
                    // The engine's own fold over every CostAdd/CostSet/CostHalf modifier, which is the number the peer
                    // has no way to compute. NOT the fixed-use or accelerate price: the peer recomputes those itself
                    r["cost"] = c.Cost;
                    r["atk"] = c.Atk;
                    r["life"] = c.Life;
                    Add(r, c, "spellboost", "SpellChargeCount");
                    Add(r, c, "chant", "AddChantCount");
                    Add(r, c, "tribe", "Tribe");
                    Add(r, c, "clan", "Clan");
                    return r;
                }
            }
            return r;
        }

        // the properties differ between builds, so a missing one is a key the caller does not get rather than a throw
        static void Add(Dictionary<string, object> into, BattleCardBase c, string key, string prop)
        {
            var p = c.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p == null) return;
            try
            {
                var v = p.GetValue(c);
                if (v != null) into[key] = Convert.ToInt32(v);
            }
            catch { }
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
