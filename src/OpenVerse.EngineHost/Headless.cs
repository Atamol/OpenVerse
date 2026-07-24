using System;
using System.Linq;
using System.Reflection;

// brings the decompiled client up to the state a logged-in game would be in, without Unity. everything here exists
// because the engine reads static singletons the login/scene flow would have populated
public static class Headless
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static Type[] Types { get; private set; }
    public static Type T(string n) => Types.FirstOrDefault(x => x != null && (x.FullName == n || x.Name == n));

    public static int Seeded;

    public static bool Booted { get; private set; }

    /// <param name="cardMasterCsv">the real card master, decompressed (the same data the API serves clients)</param>
    public static void Boot(string cardMasterCsv)
    {
        if (Booted) return;
        var asm = Assembly.Load("Assembly-CSharp");
        try { Types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { Types = ex.Types.Where(t => t != null).ToArray(); }

        // Data.* holds the login response; three passes because later fields reference earlier ones
        var data = T("Wizard.Data") ?? T("Data");
        for (int i = 0; i < 3; i++) SeedStatics(data);

        // GameMgr is a plain class, so its real ctor runs. CreateMgrIns can't: SoundMgr dies inside CriWare and InputMgr
        // touches UnityEngine.Input, so each manager is built individually and the two that throw fall back to a blank
        // instance. the battle ctor only reaches through them for camera/background wiring
        BuildGameMgr();
        FillManagers();
        LoadCardMaster(cardMasterCsv);
        Booted = true;
        SeedLeaders();
        SeedPrefabs();
        SeedToolbox();
        SeedMasterText();
        SeedSystemText();
    }

    // SystemText's ctor parses TextAssets out of Resources, so it throws headlessly and Data.SystemText stays null,
    // yet BattleCardBase.ConvertSkillDescriptionText calls Convert on it for every skill that shows a value in its log
    // line. build it uninitialized, then give it the field initializer and the two members Convert reads
    static void SeedSystemText()
    {
        var stT = T("Wizard.SystemText");
        var dataT = T("Wizard.Data") ?? T("Data");
        var slot = dataT.GetProperty("SystemText", Any);
        var slotF = dataT.GetField("SystemText", Any);
        if (stT == null || (slot == null && slotF == null)) return;
        if ((slot != null ? slot.GetValue(null) : slotF.GetValue(null)) != null) { Console.WriteLine("  Data.SystemText already present"); return; }

        var st = Blank(stT);
        // patternTbl is a field initializer, so an uninitialized object leaves it null and Parse indexes into nothing
        stT.GetField("patternTbl", Any).SetValue(st, new string[2] {
            "{(?<VALUE>[^@{}]*?(?<INDEX>-?\\d+)[^@{}]*?)}",
            "{(?<VALUE>[^@{}]*?(?<INDEX>-?\\d+)[^@{}]*?)@(?<SINGULAR>[^@{}]+?)@(?<PLURAL>[^@{}]+?)}" });
        SetBacking(st, "TextDictionary", new System.Collections.Generic.Dictionary<string, string>());
        SetBacking(st, "RegionCode", "Jpn");

        if (slot != null && slot.GetSetMethod(true) != null) slot.GetSetMethod(true).Invoke(null, new[] { st });
        else slotF?.SetValue(null, st);
        Console.WriteLine("  Data.SystemText seeded: " + ((slot != null ? slot.GetValue(null) : slotF.GetValue(null)) != null));
    }

    public static int TextDicsSeeded;

    // the localisation dictionaries are declared as IDictionary, so SeedInstance skips them, and are normally filled
    // from TextAssets in a resource bundle. SkillBase.CallStart reads SkillDescription on every activation, so a null
    // one takes down the whole rules path. GetText falls back to the key on a miss, so empty is enough
    static void SeedMasterText()
    {
        var masterT = T("Wizard.Master");
        var dataT = T("Wizard.Data") ?? T("Data");
        var ins = dataT.GetProperty("Master", Any)?.GetValue(null) ?? dataT.GetField("Master", Any)?.GetValue(null);
        if (ins == null) return;

        foreach (var m in masterT.GetMembers(Any))
        {
            Type ft; Func<object> get; Action<object> set;
            if (m is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0)
            {
                var setter = p.GetSetMethod(true);
                if (setter == null) continue;
                ft = p.PropertyType; get = () => p.GetValue(ins); set = v => setter.Invoke(ins, new[] { v });
            }
            else if (m is FieldInfo f && !f.IsInitOnly && !f.IsLiteral && !f.IsStatic)
            { ft = f.FieldType; get = () => f.GetValue(ins); set = v => f.SetValue(ins, v); }
            else continue;

            if (!ft.IsInterface || !ft.IsGenericType) continue;
            var def = ft.GetGenericTypeDefinition();
            if (def != typeof(System.Collections.Generic.IDictionary<,>)) continue;

            try
            {
                if (get() != null) continue;
                set(Activator.CreateInstance(typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(ft.GetGenericArguments())));
                TextDicsSeeded++;
            }
            catch { }
        }
        Console.WriteLine("  Master text dictionaries seeded: " + TextDicsSeeded);
    }

    // Cute.Toolbox is the framework service locator; the battle only reaches it for asset loading
    static void SeedToolbox()
    {
        var tb = T("Cute.Toolbox");
        foreach (var f in tb.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            // reading the field is itself enough to fault: resolving its type loads the declaring assembly, and the
            // native-plugin ones (CriWare, Steamworks) are not shipped. none of them is a rule
            try
            {
                if (f.GetValue(null) != null || f.FieldType.IsPrimitive || f.FieldType.IsAbstract || Finalizable(f.FieldType)) continue;
                var o = Blank(f.FieldType);
                f.SetValue(null, o);
                SeedInstance(o, 2);
                Seeded++;
            }
            catch { }
        }
    }

    // Resources.Load returns null without a player, and the battle ctor dereferences this one prefab unconditionally.
    // everything else the client guards on null, so only this entry is planted
    static void SeedPrefabs()
    {
        var pm = T("GameMgr").GetField("_prefabMgr", Any).GetValue(GameMgrIns);
        var dict = (System.Collections.IDictionary)pm.GetType().GetField("m_PrefabData", Any).GetValue(pm);
        var goT = pm.GetType().Assembly.GetType("UnityEngine.GameObject")
                  ?? Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule");
        dict["Prefab/Game/UnityEventAgent"] = Activator.CreateInstance(goT);
    }

    // the real list comes from a CSV inside a Unity resource bundle, and its ctor also reaches into localisation, so
    // the eight leaders get their backing fields written straight instead: chara_id N is clan N
    static void SeedLeaders()
    {
        var ccT = T("Wizard.ClassCharacterMasterData");
        var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(ccT);
        var list = (System.Collections.IList)Activator.CreateInstance(listT);

        for (int i = 1; i <= 8; i++)
        {
            var o = Blank(ccT);
            SetBacking(o, "chara_id", i);
            SetBacking(o, "class_id", i);
            SetBacking(o, "skin_id", i);
            SetBacking(o, "clan", Enum.ToObject(ccT.GetProperty("clan").PropertyType, i));
            SetBacking(o, "ClassColorId", Enum.ToObject(ccT.GetProperty("clan").PropertyType, i));
            SetBacking(o, "is_usable", true);
            SetBacking(o, "IsAcquired", true);
            SetBacking(o, "chara_name", "Leader" + i);
            SetBacking(o, "_className", "Class" + i);
            SetBacking(o, "path", "");
            list.Add(o);
        }

        var master = T("Wizard.Master");
        var dataT = T("Wizard.Data") ?? T("Data");
        var masterIns = dataT.GetProperty("Master", Any)?.GetValue(null) ?? dataT.GetField("Master", Any)?.GetValue(null);
        Console.WriteLine("  Data.Master = " + (masterIns?.ToString() ?? "<null>"));
        master.GetProperty("ClassCharacterList", Any).SetValue(masterIns, list);

        // emotion data is looked up by skin id with a bare indexer, so every leader needs an (empty) entry
        var emoT = master.GetProperty("_emotionDic", Any);
        var emo = (System.Collections.IDictionary)Activator.CreateInstance(emoT.PropertyType);
        var inner = emoT.PropertyType.GetGenericArguments()[1];
        for (int i = 1; i <= 8; i++) emo[i.ToString()] = Activator.CreateInstance(inner);
        emo[""] = Activator.CreateInstance(inner);
        emoT.SetValue(masterIns, emo);

        // without a login response both sides fall back to a PlayerPrefs leader id of 0, which matches no leader
        var gm = T("GameMgr");
        var dm = gm.GetField("_dataMgr", Any).GetValue(GameMgrIns);
        dm.GetType().GetField("_playerCharaId", Any).SetValue(dm, 1);
        dm.GetType().GetField("_enemyCharaId", Any).SetValue(dm, 2);
        Console.WriteLine("  leaders: player chara 1, enemy chara 2, list=" + list.Count);
    }

    // some of these wrap native handles (CriWare audio wraps cri_ware_unity); the finalizer p/invokes into DLLs not
    // present and takes the process down from the finalizer thread, so suppress it here
    static object Blank(Type t)
    {
        var o = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
        GC.SuppressFinalize(o);
        return o;
    }

    // a finalizer means one of those native handles (see Blank); exclude the type from the reflective seeding entirely
    static bool Finalizable(Type t) =>
        t.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType != typeof(object);

    static void SetBacking(object o, string prop, object val)
    {
        var f = o.GetType().GetField("<" + prop + ">k__BackingField", Any) ?? o.GetType().GetField(prop, Any);
        f?.SetValue(o, val);
    }

    public static int CardCount;

    // CreateCardMaster and the instance dictionary are private: the client only ever fills them from a server response
    static void LoadCardMaster(string csv)
    {
        var cmT = T("Wizard.CardMaster");
        var idT = cmT.GetNestedType("CardMasterId", Any);
        var def = Enum.ToObject(idT, 1);
        var master = cmT.GetMethod("CreateCardMaster", Any).Invoke(null, new object[] { def, csv });

        var dictF = cmT.GetField("_dictCardMaster", Any);
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(
            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(idT, cmT));
        dict[def] = master;
        dictF.SetValue(null, dict);

        CardCount = ((System.Collections.ICollection)cmT.GetField("m_cardParameters", Any).GetValue(master)).Count;
    }

    public static object GameMgrIns;

    static void BuildGameMgr()
    {
        var gm = T("GameMgr");
        gm.GetMethod("CreateIns", Any).Invoke(null, null);
        GameMgrIns = gm.GetField("_instance", Any).GetValue(null);

        foreach (var name in new[] { "_gameObjMgr", "_prefabMgr", "_dataMgr", "_soundMgr", "_inputMgr", "_effectMgr",
                                     "_myPageTask", "_mailTopTask", "_deckUpdateTask", "_cardDestructTask",
                                     "_cardCreateTask", "_deckInfoTask", "_missionInfoTask", "_networkUserInfoData" })
        {
            var f = gm.GetField(name, Any);
            if (f == null || f.GetValue(GameMgrIns) != null) continue;
            try
            {
                var ctor = f.FieldType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                      .OrderBy(c => c.GetParameters().Length).First();
                var args = ctor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                f.SetValue(GameMgrIns, ctor.Invoke(args));
                Seeded++;
            }
            catch { }
            if (f.GetValue(GameMgrIns) == null)
            {
                f.SetValue(GameMgrIns, Blank(f.FieldType));
                SeedInstance(f.GetValue(GameMgrIns), 2);
                Seeded++;
            }
        }
    }

    static void FillManagers()
    {
        var gm = T("GameMgr");
        var inst = GameMgrIns;
        if (inst == null) return;
        foreach (var f in gm.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.GetValue(inst) != null || f.FieldType.IsAbstract || f.FieldType.IsInterface || f.FieldType.IsPrimitive || Finalizable(f.FieldType)) continue;
            try
            {
                var o = f.FieldType.GetConstructor(Type.EmptyTypes) != null
                    ? Activator.CreateInstance(f.FieldType)
                    : Blank(f.FieldType);
                f.SetValue(inst, o);
                SeedInstance(o, 2);
                Seeded++;
            }
            catch { }
        }
    }

    // singletons use a private static instance field plus GetIns(), so construct one directly and install it
    static void ForceSingleton(string typeName)
    {
        var t = T(typeName);
        if (t == null) return;
        var inst = t.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(f => t.IsAssignableFrom(f.FieldType));
        if (inst == null || inst.GetValue(null) != null) return;
        try
        {
            var obj = Blank(t);
            inst.SetValue(null, obj);
            SeedInstance(obj, 0);
            Seeded++;
        }
        catch { }
    }

    static void SeedStatics(Type t, int depth = 0)
    {
        if (t == null || depth > 3) return;
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Static))
        {
            if (!Accessor(m, null, out var ft, out var get, out var set)) continue;
            try
            {
                if (get() == null && ft.GetConstructor(Type.EmptyTypes) != null) { set(Activator.CreateInstance(ft)); Seeded++; }
                SeedInstance(get(), depth + 1);
            }
            catch { }
        }
    }

    public static void SeedInstance(object obj, int depth)
    {
        if (obj == null || depth > 4) return;
        foreach (var m in obj.GetType().GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!Accessor(m, obj, out var ft, out var get, out var set)) continue;
            try
            {
                if (get() == null && ft.GetConstructor(Type.EmptyTypes) != null)
                {
                    set(Activator.CreateInstance(ft));
                    Seeded++;
                    SeedInstance(get(), depth + 1);
                }
            }
            catch { }
        }
    }

    static bool Accessor(MemberInfo m, object target, out Type ft, out Func<object> get, out Action<object> set)
    {
        ft = null; get = null; set = null;
        if (m is PropertyInfo p && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        { ft = p.PropertyType; get = () => p.GetValue(target); set = v => p.SetValue(target, v); }
        else if (m is FieldInfo f && !f.IsInitOnly && !f.IsLiteral)
        { ft = f.FieldType; get = () => f.GetValue(target); set = v => f.SetValue(target, v); }
        else return false;
        if (Finalizable(ft)) return false;
        if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft.IsArray || ft.IsAbstract || ft.IsInterface) return false;
        return true;
    }

    public static string Root(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        var frames = (e.StackTrace ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(6);
        return e.GetType().Name + ": " + e.Message + "\n       " + string.Join("\n       ", frames);
    }
}

