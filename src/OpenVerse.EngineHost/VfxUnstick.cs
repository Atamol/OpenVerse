using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Wizard.Battle.View.Vfx;

// EvolveVfx (and any skill with a visual) gates on the asset pipeline: WaitLoadResourceVfx hands off to
// ResourcesManager.StartCoroutine_LoadAssetGroupAsync and WaitCallbackVfx sits until that load calls back. with no
// bundles on disk the callback never arrives, so the vfx queue wedges and everything behind it (the end-of-game
// sequence included) never runs. releasing exactly those two leaf types is the headless reading of "the effect is
// already loaded"; nothing else is touched, so no rules-carrying vfx gets skipped
public static class VfxUnstick
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static readonly HashSet<string> LoadGated = new HashSet<string>
    {
        "WaitCallbackVfx", "WaitLoadResourceVfx"
    };

    static FieldInfo _isEnd;
    static FieldInfo IsEndField => _isEnd ??= typeof(VfxBase).GetField("<IsEnd>k__BackingField", Any);

    public static int Release(VfxMgr mgr)
    {
        int n = 0;
        foreach (var root in Roots(mgr)) n += Walk(root, 0);
        return n;
    }

    static IEnumerable<object> Roots(VfxMgr mgr)
    {
        var t = typeof(VfxMgr);
        foreach (var name in new[] { "_sequentialVfxPlayer", "vfxList", "addedVfxList" })
        {
            var f = t.GetField(name, Any);
            var v = f?.GetValue(mgr);
            if (v == null) continue;
            if (v is IEnumerable e && !(v is VfxBase)) { foreach (var x in e) if (x != null) yield return x; }
            else yield return v;
        }
    }

    static FieldInfo Find(Type t, string name)
    {
        for (; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

    static int Walk(object vfx, int depth)
    {
        if (vfx == null || depth > 24) return 0;
        int n = 0;
        var t = vfx.GetType();

        if (LoadGated.Contains(t.Name))
        {
            var already = vfx is VfxBase vb && vb.IsEnd;
            if (!already) { IsEndField.SetValue(vfx, true); n++; }
            return n;
        }

        // VfxWithLoading keeps its two branches under their own names, so a walker that only knows the sequential and
        // parallel players' fields stops at it and never sees the load waiters inside
        foreach (var name in new[] { "_currentVfx", "_vfxQueue", "_vfxList", "_parallelLoadingVfx", "_sequentialMainVfx", "_loadingVfx", "_mainVfx" })
        {
            // WaitLoadEffectVfx derives from SequentialVfxPlayer, whose queue is private, so GetField on the runtime
            // type alone would not see it
            var f = Find(t, name);
            if (f == null) continue;
            var v = f.GetValue(vfx);
            if (v == null) continue;
            if (v is IEnumerable e && !(v is VfxBase)) { foreach (var x in e) n += Walk(x, depth + 1); }
            else n += Walk(v, depth + 1);
        }
        return n;
    }
}
