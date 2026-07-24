using System;
using System.Linq;
using System.Reflection;

// the last few ability failures were all presentation reaching for scene objects, never rule logic, so plant the two
// the skills dereference. NOT via BattleManagerBase.IsForecast: that flag also makes StableRandom return 0, which
// would silently kill every random effect in the game
public static class HeadlessFix
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Apply(object mgr)
    {
        // MoveToDeckVfx reads CardHolder.transform.position when a card animates back to the deck
        var goT = Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule");
        var bmb = Headless.T("BattleManagerBase");
        foreach (var name in new[] { "CardHolder", "ECardHolder", "ChoiceCardHolder", "EvolveCardHolder" })
        {
            var f = bmb.GetField(name, Any);
            if (f != null && f.GetValue(mgr) == null) f.SetValue(mgr, Activator.CreateInstance(goT));
        }

        // Skill_evolve/Skill_metamorphose call BattleLogManager.GetInstance() to append a log line. a blank instance
        // satisfies the call, and its own null guards keep it from touching any UI
        var blm = Headless.T("Wizard.Battle.UI.BattleLogManager") ?? Headless.T("BattleLogManager");
        var inst = blm?.GetField("_instance", Any);
        if (inst != null)
        {
            // Boot's static seeding may already have put one here, so this fills whichever instance is in place
            var log = inst.GetValue(null);
            if (log == null)
            {
                log = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(blm);
                inst.SetValue(null, log);
            }
            // only the fusion path gets filled in. a blanket seed here is actively harmful: most of this object's null
            // fields are the guards that keep the log off UI, and filling them sends PlayCard's logging into the scene
            SeedLogWindow(log);
        }

        // EvolveVfx also toggles the log button, and BattleLogButton.DisableButton lands on the same poisoned
        // UIButton.isEnabled. unlike the log window this one is safe to leave installed: nothing reads it back
        if (inst != null)
        {
            var log2 = inst.GetValue(null);
            var lbF = log2?.GetType().GetField("_logButton", Any);
            if (lbF != null && lbF.GetValue(log2) == null)
            {
                var lb = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(lbF.FieldType);
                lbF.SetValue(log2, lb);
                var innerF = lbF.FieldType.GetField("_button", Any);
                innerF?.SetValue(lb, new HeadlessMenuButton());
            }
        }

        // BattleUIContainer.DisableMenu drives this one straight into NGUI
        var uiC = bmb.GetProperty("BattleUIContainer", Any)?.GetValue(mgr);
        var btn = uiC?.GetType().GetField("BattleMenuBtn", Any);
        if (btn != null) btn.SetValue(uiC, new HeadlessMenuButton());

        // Skill_evolve calls UIManager.GetInstance().CreateNowLoadingVfx unconditionally, and everything it does from
        // there is null-guarded, so a blank instance is enough. GetInstance() reads ToolboxGame.UIManager, so the
        // blank goes there
        var ui = Headless.T("UIManager");
        var tbg = Headless.T("ToolboxGame");
        var slot = tbg?.GetProperty("UIManager", Any) ?? (MemberInfo)tbg?.GetField("UIManager", Any);
        if (ui != null && slot != null)
        {
            object cur = slot is PropertyInfo pp ? pp.GetValue(null) : ((FieldInfo)slot).GetValue(null);
            if (cur == null)
            {
                var blank = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(ui);
                if (slot is PropertyInfo p2) p2.GetSetMethod(true)?.Invoke(null, new[] { blank });
                else ((FieldInfo)slot).SetValue(null, blank);
            }
        }

        Console.WriteLine("  headless fixups applied (card holders + battle log + ui)");
    }

    // fills null reference fields with uninitialized instances. unlike Headless.SeedInstance it does not need a
    // parameterless ctor, which is the point: everything in the log window is a MonoBehaviour
    static void DeepBlank(object o, int depth)
    {
        if (o == null || depth <= 0) return;
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var t = f.FieldType;
            if (t.IsPrimitive || t.IsEnum || t.IsValueType || t == typeof(string) || t.IsArray) continue;
            if (t.IsAbstract || t.IsInterface) continue;
            if (t.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType != typeof(object)) continue;
            try
            {
                if (f.GetValue(o) != null) { DeepBlank(f.GetValue(o), depth - 1); continue; }
                var v = t.GetConstructor(Type.EmptyTypes) != null
                    ? Activator.CreateInstance(t)
                    : System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
                GC.SuppressFinalize(v);
                f.SetValue(o, v);
                DeepBlank(v, depth - 1);
            }
            catch { }
        }
    }

    // fusion routes through the log: BattleLogManager.AddFusionIngredients ends at _logWindow._informationTab
    // .RemoveCache, only two dictionary removes but behind two MonoBehaviours the scene would have wired up
    static object _log, _window;
    static FieldInfo _windowField;

    // BattleLogManager._logWindow is a switch with two settings and no middle: null and the log leaves the scene alone
    // (what every other path needs), non-null and AddFusionIngredients can finish but PlayCard's logging walks into
    // NGUI. so the window is built once and only hung on the manager for the length of a fusion
    public static void WithLogWindow(bool on)
    {
        if (_windowField == null || _log == null) return;
        _windowField.SetValue(_log, on ? _window : null);
    }

    static void SeedLogWindow(object log)
    {
        var winF = log.GetType().GetField("_logWindow", Any);
        if (winF == null) return;

        var win = winF.GetValue(log)
                  ?? System.Runtime.Serialization.FormatterServices.GetUninitializedObject(winF.FieldType);
        winF.SetValue(log, win);

        // GetEmptyTextLabel reaches _logWindow.PlayCardLog/.LogDestruction and their EmptyLog* labels, all
        // MonoBehaviours the scene wires up, so blanket-seeding is confined to the window: doing the same to the
        // manager itself would clear the null guards that keep the rest of the log off the scene
        DeepBlank(win, 3);

        var tabF = win.GetType().GetField("_informationTab", Any);
        if (tabF == null) return;
        var tab = tabF.GetValue(win);
        if (tab == null)
        {
            tab = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(tabF.FieldType);
            tabF.SetValue(win, tab);
        }
        foreach (var name in new[] { "_cacheFusionConditionsPlayer", "_cacheFusionConditionsEnemy" })
        {
            var f = tab.GetType().GetField(name, Any);
            if (f != null && f.GetValue(tab) == null) f.SetValue(tab, Activator.CreateInstance(f.FieldType));
        }

        // BattlePlayerBase.Fusion appends to these before it reaches the tab cache
        foreach (var name in new[] { "PlayerFusionCard", "EnemyFusionCard" })
        {
            var f = log.GetType().GetField(name, Any);
            if (f != null && f.GetValue(log) == null) f.SetValue(log, Activator.CreateInstance(f.FieldType));
        }

        _log = log; _window = win; _windowField = winF;
        WithLogWindow(false);
        Console.WriteLine("  battle log window built (installed only during fusion)");
    }
}
