using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Wizard;
using Wizard.BattleMgr;

public static class ShadowReconciler
{
    // A draw names its index only in orderList, which the receive path drops (the engine re-simulates and takes its own
    // deck top). Index is not deck order either (the client reshuffles index assignment off idxChangeSeed on every card
    // entering the deck), so the two never line up on their own. So every card the actor drew is still in the shadow's
    // deck: the play is accepted and charged but nothing leaves the deck, and the later ATTACK/EVOLUTION find null in
    // ClassAndInPlayCardList. Each message says where its cards were (move from/to, plus what the action type implies),
    // so put them there before the engine reads it and its own simulation does the rest.

    const int Deck = 0, Hand = 10, Field = 20, Cemetery = 30, Banish = 40, Create = 50;

    // the turn draw rides on the turn messages, not on a play, and skipping them leaves every drawn card stuck in the
    // shadow's deck for the rest of the match
    static bool Carries(string uri) =>
        uri == "PlayActions" || uri == "TurnStart" || uri == "TurnEndActions" || uri == "TurnEnd";

    public static int Hoisted, Recovered, Unplaced;

    static readonly HashSet<int> SeenSelf = new HashSet<int>(), SeenOppo = new HashSet<int>();

    // the census is per match, and a room plays many. carrying it over makes Place refuse evictions the new match needs,
    // so every battle after the first ran against the previous one's indices
    public static void Reset()
    {
        SeenSelf.Clear();
        SeenOppo.Clear();
        Hoisted = Recovered = Unplaced = Evicted = 0;
    }

    public static void Repair(BattleManagerBase mgr, string uri, Dictionary<string, object> body, bool isPlayer, int type, int playIdx)
    {
        if (!Carries(uri)) return;

        var moves = Moves(body);

        var self = new Dictionary<int, int>();
        var oppo = new Dictionary<int, int>();
        foreach (var m in moves)
        {
            var d = m.isSelf ? self : oppo;
            if (!d.ContainsKey(m.idx) && m.from != Create) d[m.idx] = m.from;
        }
        // the acting card itself: hand for a play, board for an attack or an evolution
        if (playIdx > 0 && !self.ContainsKey(playIdx))
            self[playIdx] = (type == 10 || type == 20 || type == 21) ? Field : Hand;
        foreach (var t in Targets(body, isPlayer))
            if (t.idx > 0)
            {
                var d = t.onActorSide ? self : oppo;
                if (!d.ContainsKey(t.idx)) d[t.idx] = Field;
            }
        // an alter (spellboost / cost) can only land on a card in hand, so its idx list is a partial census of the
        // actor's hand, and this puts the select target of a spellboost-charging spell where the spell can find it
        foreach (var kv in Alters(body))
        {
            var d = kv.Value ? self : oppo;
            if (!d.ContainsKey(kv.Key)) d[kv.Key] = Hand;
        }

        foreach (var i in self.Keys) (isPlayer ? SeenSelf : SeenOppo).Add(i);
        foreach (var i in oppo.Keys) (isPlayer ? SeenOppo : SeenSelf).Add(i);
        if (playIdx > 0) (isPlayer ? SeenSelf : SeenOppo).Add(playIdx);

        Place(mgr, isPlayer ? (BattlePlayerBase)mgr.BattlePlayer : mgr.BattleEnemy, self, isPlayer ? SeenSelf : SeenOppo);
        Place(mgr, isPlayer ? (BattlePlayerBase)mgr.BattleEnemy : mgr.BattlePlayer, oppo, isPlayer ? SeenOppo : SeenSelf);
    }

    // after the engine re-simulated the play: it drew its own deck top wherever the play drew a card, so the card the
    // wire actually drew is still in the deck and the next message naming it would have to hoist it. draw the named
    // ones now, while the play that caused them is still the last thing that happened
    public static void RepairAfter(BattleManagerBase mgr, string uri, Dictionary<string, object> body, bool isPlayer)
    {
        if (!Carries(uri)) return;
        var self = new Dictionary<int, int>();
        var oppo = new Dictionary<int, int>();
        foreach (var m in Moves(body))
            if (m.to == Hand) (m.isSelf ? self : oppo)[m.idx] = Hand;
        Place(mgr, isPlayer ? (BattlePlayerBase)mgr.BattlePlayer : mgr.BattleEnemy, self, isPlayer ? SeenSelf : SeenOppo);
        Place(mgr, isPlayer ? (BattlePlayerBase)mgr.BattleEnemy : mgr.BattlePlayer, oppo, isPlayer ? SeenOppo : SeenSelf);
    }

    const int HandLimit = 9;

    static void Place(BattleManagerBase mgr, BattlePlayerBase p, Dictionary<int, int> want, HashSet<int> seen)
    {
        var draw = new List<BattleCardBase>();
        foreach (var kv in want)
        {
            if (kv.Value == Deck || kv.Value == Create) continue;
            var inDeck = Find(p.DeckCardList, kv.Key);
            if (inDeck != null) { draw.Add(inDeck); continue; }
            if (kv.Value == Field && Find(p.ClassAndInPlayCardList, kv.Key) == null
                                 && Find(p.HandCardList, kv.Key) == null) Unplaced++;
        }
        if (draw.Count == 0) return;

        // DrawCards discards straight to the cemetery past nine cards, and the hand is over-full precisely because the
        // shadow drew cards of its own choosing. give those back first: they stand in for the cards this call is about
        // to put where they belong
        int over = p.HandCardList.Count + draw.Count - HandLimit;
        for (int i = 0; over > 0 && i < p.HandCardList.Count; )
        {
            var c = p.HandCardList[i];
            if (c == null || seen.Contains(c.Index)) { i++; continue; }
            p.HandCardList.RemoveAt(i);
            p.AddToDeck(c);
            Evicted++;
            over--;
        }

        Hoisted += draw.Count;
        mgr.VfxMgr.RegisterSequentialVfx(p.DrawCards(draw, new SkillProcessor(), isOpen: false, isMulligan: true).Vfx);
    }

    public static int Evicted;

    static Dictionary<int, bool> Alters(Dictionary<string, object> body)
    {
        var d = new Dictionary<int, bool>();
        if (!body.TryGetValue("orderList", out var ol) || !(ol is List<object> orders)) return d;
        foreach (var o in orders.OfType<Dictionary<string, object>>())
        {
            if (!o.TryGetValue("alter", out var av) || !(av is Dictionary<string, object> a)) continue;
            if (!a.ContainsKey("spellboost") && !a.ContainsKey("cost")) continue;
            bool self = a.TryGetValue("isSelf", out var s) && Convert.ToInt32(s) == 1;
            if (a.TryGetValue("idx", out var idxO)) foreach (var i in Ints(idxO)) d[i] = self;
        }
        return d;
    }

    static BattleCardBase Find(IEnumerable<BattleCardBase> zone, int idx)
    {
        if (zone == null) return null;
        foreach (var c in zone.ToList())
        {
            try { if (c != null && c.Index == idx) return c; } catch { }
        }
        return null;
    }

    public struct Move { public int idx, from, to; public bool isSelf; }

    public static List<Move> Moves(Dictionary<string, object> body)
    {
        var list = new List<Move>();
        if (!body.TryGetValue("orderList", out var ol) || !(ol is List<object> orders)) return list;
        foreach (var o in orders.OfType<Dictionary<string, object>>())
        {
            if (!o.TryGetValue("move", out var mv) || !(mv is Dictionary<string, object> m)) continue;
            if (!m.TryGetValue("idx", out var idxO)) continue;
            bool self = m.TryGetValue("isSelf", out var s) && Convert.ToInt32(s) == 1;
            int from = m.TryGetValue("from", out var f) ? Convert.ToInt32(f) : -1;
            int to = m.TryGetValue("to", out var t) ? Convert.ToInt32(t) : -1;
            foreach (var i in Ints(idxO)) list.Add(new Move { idx = i, from = from, to = to, isSelf = self });
        }
        return list;
    }

    public struct Target { public int idx; public bool onActorSide; }

    // targetList after normalization (self-side) or oppoTargetList as sent (peer side); isSelf/vid is actor-relative
    static List<Target> Targets(Dictionary<string, object> body, bool isPlayer)
    {
        var list = new List<Target>();
        foreach (var key in new[] { "targetList", "oppoTargetList" })
        {
            if (!body.TryGetValue(key, out var v) || !(v is List<object> arr)) continue;
            foreach (var e in arr.OfType<Dictionary<string, object>>())
            {
                if (!e.TryGetValue("targetIdx", out var ti)) continue;
                bool actorSide;
                if (e.TryGetValue("vid", out var vid)) actorSide = Convert.ToInt32(vid) == Wire.SelfVid == isPlayer;
                else actorSide = e.TryGetValue("isSelf", out var s) && Convert.ToInt32(s) == 1;
                list.Add(new Target { idx = Convert.ToInt32(ti), onActorSide = actorSide });
            }
        }
        return list;
    }

    public static HashSet<int> ChargeIdxs(Dictionary<string, object> body)
    {
        var set = new HashSet<int>();
        if (!body.TryGetValue("orderList", out var ol) || !(ol is List<object> orders)) return set;
        foreach (var o in orders.OfType<Dictionary<string, object>>())
        {
            if (!o.TryGetValue("alter", out var av) || !(av is Dictionary<string, object> a)) continue;
            if (!a.ContainsKey("spellboost")) continue;
            if (!(a.TryGetValue("isSelf", out var s) && Convert.ToInt32(s) == 1)) continue;
            if (a.TryGetValue("idx", out var idxO)) foreach (var i in Ints(idxO)) set.Add(i);
        }
        return set;
    }

    static IEnumerable<int> Ints(object o)
    {
        if (o is List<object> l) { foreach (var x in l) yield return Convert.ToInt32(x); }
        else if (o != null) yield return Convert.ToInt32(o);
    }
}
