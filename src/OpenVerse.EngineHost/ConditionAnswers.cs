using System;
using System.Collections.Generic;
using System.Reflection;
using Wizard;

// Answers the skill-condition questions the acting client puts on the wire but never answers itself: the lost Cygames
// server injected them into the same message's knownList, and without one CheckCondition drops its own reading and
// returns IsReceivedSkillConditionCheck, so the skill does not fire on the peer.
// Public surface is flat (Dictionary/List/string/int/bool) because the net10 caller reaches this net48 assembly by
// reflection with no shared types
public static class Answer
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static string LastError { get; private set; }

    public static List<string> LastTrace { get; private set; } = new List<string>();

    // specs are the actor's orderList[].skillConditionCheck objects flattened, and the rows come back keyed for the
    // receive side, ready to append to knownList. Not reentrant: the evaluation lifts the played card out of its hand
    // (see HideFromHand), so it must run on the shadow worker thread and never alongside an ingest
    public static List<object> AnswerConditions(NetworkBattleManagerBase mgr, bool isSelfPlayer, int cardIdx, List<object> specs)
    {
        LastTrace = new List<string>();
        var rows = new List<object>();
        if (specs == null || specs.Count == 0 || mgr == null) return rows;

        // Two specs for one skill are its two option slots (a powerup that buffs attack AND life registers twice), and
        // the receiving node tells them apart by their order, so the n-th spec for a skill answers its n-th slot
        var nth = new Dictionary<SkillBase, int>();

        foreach (var raw in specs)
        {
            var spec = raw as Dictionary<string, object>;
            if (spec == null) { LastTrace.Add("spec is not a dictionary -> declined"); continue; }
            Dictionary<string, object> row = null;
            try { row = One(mgr, isSelfPlayer, cardIdx, spec, nth); }
            catch (Exception e) { LastError = Root(e); LastTrace.Add("threw " + Root(e) + " -> declined"); row = null; }
            if (row != null) rows.Add(row);
        }
        return rows;
    }

    static Dictionary<string, object> One(NetworkBattleManagerBase mgr, bool isSelfPlayer, int cardIdx,
                                          Dictionary<string, object> spec, Dictionary<SkillBase, int> nth)
    {
        // Only these three keys are read. Type / target / condition / isPreprocess are the actor's own restatement of
        // a filter this side already owns. Trusting them would mean reimplementing the filter language
        int idx = Int(spec, "idx", cardIdx);
        int skillIdx = Int(spec, "skillIdx", -1);
        int skillCount = Int(spec, "skillCount", -1);

        var card = FindCard(mgr, isSelfPlayer, idx);
        if (card == null) { Note(idx, skillIdx, skillCount, "no card at that index -> declined"); return null; }

        var skill = ResolveSkill(card, skillIdx, skillCount);
        if (skill == null) { Note(idx, skillIdx, skillCount, "skill not resolvable on " + card.CardId + " -> declined"); return null; }

        // A when_play_other skill sits on a different card than the one being played, so its condition names the play
        // through target=played_card. Without this the filter resolves to nothing and every such condition reads false
        var played = idx == cardIdx ? card : FindCard(mgr, isSelfPlayer, cardIdx);

        int slot = nth.TryGetValue(skill, out var n) ? n : 0;
        nth[skill] = slot + 1;

        var kind = Classify(idx, skillCount, skill, slot);
        if (kind == RegisterSkillConditionCheck.SkillConditionType.NONE)
        { Note(idx, skillIdx, skillCount, "CreateList produced nothing -> declined"); return null; }

        switch (kind)
        {
            // Nothing on the receiving side reads an answer for this one
            case RegisterSkillConditionCheck.SkillConditionType.moved_to_hand_count:
                Note(idx, skillIdx, skillCount, "moved_to_hand_count -> no answer by design");
                return null;

            case RegisterSkillConditionCheck.SkillConditionType.count_check:
            case RegisterSkillConditionCheck.SkillConditionType.count_compare:
            case RegisterSkillConditionCheck.SkillConditionType.add_deck_count_check:
            case RegisterSkillConditionCheck.SkillConditionType.check_highlander:
            {
                bool ok = Evaluate(mgr, card, played, skill);
                Note(idx, skillIdx, skillCount, kind + " -> activate=" + (ok ? 1 : 0) + " [" + skill.GetType().Name + "]");
                return Row(idx, skillIdx, skillCount, "activate", ok ? 1 : 0);
            }

            case RegisterSkillConditionCheck.SkillConditionType.count:
            case RegisterSkillConditionCheck.SkillConditionType.param:
            case RegisterSkillConditionCheck.SkillConditionType.callCount:
            {
                int v;
                if (!Number(mgr, card, played, skill, slot, out v))
                { Note(idx, skillIdx, skillCount, kind + " -> value not readable, declined"); return null; }
                string key = kind == RegisterSkillConditionCheck.SkillConditionType.count ? "count"
                           : kind == RegisterSkillConditionCheck.SkillConditionType.param ? "param" : "callCount";
                // Count / param / callCount each set activate = 1 on the receiving side by themselves. Sending both
                // would be a second entry for the same skill and the second is unreachable
                Note(idx, skillIdx, skillCount, kind + " slot" + slot + " -> " + key + "=" + v + " [" + skill.GetType().Name + "]");
                return Row(idx, skillIdx, skillCount, key, v);
            }
        }
        Note(idx, skillIdx, skillCount, "unhandled " + kind + " -> declined");
        return null;
    }


    // ClassAndInPlayCardList, not InPlayCards: the latter yields around every ClassBattleCardBase, so the leader is
    // unreachable through it and every condition an attached ability asks at idx 0 gets declined for want of a card
    static BattleCardBase FindCard(NetworkBattleManagerBase mgr, bool isSelfPlayer, int idx)
    {
        BattlePlayerBase side = mgr.GetBattlePlayer(isSelfPlayer);
        foreach (var zone in new IEnumerable<BattleCardBase>[]
                 { side.HandCardList, side.ClassAndInPlayCardList, side.DeckCardList, side.CemeteryList, side.BanishList })
        {
            if (zone == null) continue;
            foreach (var c in zone) if (c != null && c.Index == idx) return c;
        }
        return null;
    }

    // MakeSendData writes skillIdx as Skills.IndexOf(skill), falling back to NormalSkills.IndexOf. skillCount is the
    // publish count, which is only assigned once the skill has fired, so it is the weaker key of the two here
    static SkillBase ResolveSkill(BattleCardBase card, int skillIdx, int skillCount)
    {
        if (skillIdx >= 0)
        {
            var byIdx = At(card.Skills, skillIdx) ?? At(card.NormalSkills, skillIdx) ?? At(card.EvolutionSkills, skillIdx);
            if (byIdx != null) return byIdx;
        }
        if (skillCount >= 0)
        {
            foreach (var s in All(card))
            {
                int pub = -1;
                try { pub = s.PublishedActiveSkillCount; } catch { }
                if (pub == skillCount) return s;
            }
        }
        return null;
    }

    static IEnumerable<SkillBase> All(BattleCardBase c)
    {
        var l = new List<SkillBase>();
        try { if (c.Skills != null) l.AddRange(c.Skills); } catch { }
        try { if (c.EvolutionSkills != null) l.AddRange(c.EvolutionSkills); } catch { }
        return l;
    }

    static SkillBase At(SkillCollectionBase col, int i)
    {
        try
        {
            if (col == null) return null;
            int k = 0;
            foreach (var s in col) { if (k++ == i) return s; }
        }
        catch { }
        return null;
    }

    static RegisterSkillConditionCheck.SkillConditionType Classify(int idx, int skillCount, SkillBase skill, int slot)
    {
        try
        {
            var list = RegisterSkillConditionCheck.CreateList(idx, skillCount, skill, null,
                                                              new List<SkillBase>(), new List<RegisterActionBase>());
            if (list == null || list.Count == 0) return RegisterSkillConditionCheck.SkillConditionType.NONE;
            return list[Math.Min(slot, list.Count - 1)].ConditionType;
        }
        catch { return RegisterSkillConditionCheck.SkillConditionType.NONE; }
    }


    // ExecutionInfoCreatorBase.CheckCondition is what a CPU battle runs. On this side every skill carries a
    // NetworkExecutionInfoCreator, whose CheckCondition would answer with the injected value instead of its own
    // reading, so use CheckScanCondition, which is that class's own call straight through to the base
    // Mirrors ActionProcessor's play path, which fills both with the same card. A field left null here comes back as
    // an empty filter result, so a gap against the actor reads as a silent false rather than an error
    static SkillConditionCheckerOption OptionForPlay(BattleCardBase played) =>
        new SkillConditionCheckerOption { PlayedCard = played, SummonedCard = played };

    static bool Evaluate(NetworkBattleManagerBase mgr, BattleCardBase card, BattleCardBase played, SkillBase skill)
    {
        var pair = mgr.GetBattlePlayerInfoPair(card.IsPlayer);
        var option = OptionForPlay(played);
        BattlePlayerBase owner;
        int at = HideFromHand(played, out owner);
        try
        {
            var nx = skill._executionInfoCreator as NetworkExecutionInfoCreator;
            if (nx != null) return nx.CheckScanCondition(pair, option, false);
            return skill._executionInfoCreator.CheckCondition(pair, option, false);
        }
        finally { Restore(played, owner, at); }
    }

    // The number is not a condition: it is the value of a variable inside the skill's own option text
    // (add_life={me.hand_other_self.tribe=levin.count}). GetReplaceOption is the client's own answer to "which option
    // slot does the injected number land in", and SetupOptionValue + GetInt is the engine evaluating that slot
    static bool Number(NetworkBattleManagerBase mgr, BattleCardBase card, BattleCardBase played, SkillBase skill, int slot, out int value)
    {
        value = 0;
        var pair = mgr.GetBattlePlayerInfoPair(card.IsPlayer);
        var option = OptionForPlay(played);
        BattlePlayerBase owner;
        int at = HideFromHand(played, out owner);
        int full;
        string text;
        try
        {
            // GetReplaceOption reads the option slots itself, and a variable slot only reads back once the filter
            // variable is live, so the setup has to come first or every variable option looks absent
            SkillCollectionBase.SetupOptionValue(skill.OptionValue, pair, card, skill, option, false);
            var keyword = ReplaceOption(mgr, skill, slot);
            if (keyword == SkillFilterCreator.ContentKeyword.none) return false;
            text = skill.OptionValue.GetOption(keyword);
            if (string.IsNullOrEmpty(text)) return false;
            full = skill.OptionValue.GetInt(keyword, null, isRemoveReplaceData: false);
        }
        finally { Restore(played, owner, at); }

        // ReplaceParameter scales whatever it is handed by the option's own leading "N*" and trailing "/N" before it
        // installs it, so the wire value has to be pre-divided or the scaling lands twice
        int mul = 1, div = 1;
        int i = text.IndexOfAny("*".ToCharArray());
        if (i >= 1 && int.TryParse(text.Substring(0, i), out var m) && m != 0) mul = m;
        int j = text.IndexOfAny("/".ToCharArray());
        if (j >= 1 && int.TryParse(text.Substring(j + 1), out var d) && d != 0) div = d;
        value = (mul == 1 && div == 1) ? full : full * div / mul;
        return true;
    }

    static SkillFilterCreator.ContentKeyword ReplaceOption(NetworkBattleManagerBase mgr, SkillBase skill, int slot)
    {
        try
        {
            var ev = mgr._networkBattleSetupCardEventBase;
            var m = typeof(NetworkBattleSetupCardEvent).GetMethod("GetReplaceOption", Any);
            return (SkillFilterCreator.ContentKeyword)m.Invoke(ev, new object[] { skill, slot });
        }
        catch { return SkillFilterCreator.ContentKeyword.none; }
    }

    // The shadow is asked before it has ingested the play, so the played card is still in its owner's hand while the
    // acting engine evaluated with it already out. hand_other_self excludes it either way, but a bare me.hand does not.
    // hand_other_oldest reads hand order, so Restore puts the card back where it was rather than on the end
    static int HideFromHand(BattleCardBase card, out BattlePlayerBase owner)
    {
        owner = null;
        try
        {
            var p = card.SelfBattlePlayer;
            if (p == null || p.HandCardList == null) return -1;
            int at = p.HandCardList.IndexOf(card);
            if (at < 0) return -1;
            p.HandCardList.RemoveAt(at);
            owner = p;
            return at;
        }
        catch { return -1; }
    }

    static void Restore(BattleCardBase card, BattlePlayerBase owner, int at)
    {
        try { if (owner != null && at >= 0 && !owner.HandCardList.Contains(card)) owner.HandCardList.Insert(Math.Min(at, owner.HandCardList.Count), card); }
        catch { }
    }


    // The receiving node matches the boolean reader on skillCount OR skillIdx and the numeric readers on skillCount
    // alone, so both keys are echoed back exactly as the actor wrote them
    static Dictionary<string, object> Row(int idx, int skillIdx, int skillCount, string key, int value)
    {
        var d = new Dictionary<string, object> { { "idx", idx } };
        if (skillIdx >= 0) d["skillIdx"] = skillIdx;
        if (skillCount >= 0) d["skillCount"] = skillCount;
        d[key] = value;
        return d;
    }

    static int Int(Dictionary<string, object> d, string key, int fallback)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToInt32(v.ToString()); } catch { return fallback; }
    }

    static void Note(int idx, int skillIdx, int skillCount, string what)
        => LastTrace.Add("idx=" + idx + " skillIdx=" + skillIdx + " skillCount=" + skillCount + ": " + what);

    static string Root(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }
}
