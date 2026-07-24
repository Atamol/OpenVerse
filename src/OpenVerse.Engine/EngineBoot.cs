using System.Reflection;

namespace OpenVerse.Engine;

// brings the client's own battle engine up headlessly. the client expects a logged-in game and a Unity scene; neither
// exists here, so this fills in the static state the login/scene flow would have and plants blank stand-ins for the
// presentation objects the rules incidentally touch. none of it changes a rule: every failure this fixes was a view
public static class EngineBoot
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static Type[] _types = [];
    static readonly object _gate = new();

    public static bool Ready { get; private set; }
    public static string? Failure { get; private set; }
    public static int CardCount { get; private set; }

    public static Type? T(string name) =>
        _types.FirstOrDefault(t => t is not null && (t.FullName == name || t.Name == name));

    /// <param name="cardMasterCsv">the real card master, decompressed (the same data the API serves clients)</param>
    public static bool Boot(string cardMasterCsv)
    {
        lock (_gate)
        {
            if (Ready) return true;
            try
            {
                // the engine assemblies sit beside the exe but nothing references them, so the default resolver never
                // looks: load by path, and answer for the shim when the engine asks for UnityEngine
                var dir = AppContext.BaseDirectory;
                AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                {
                    var file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                    return File.Exists(file) ? Assembly.LoadFrom(file) : null;
                };

                var asm = Assembly.LoadFrom(Path.Combine(dir, "Assembly-CSharp.dll"));
                try { _types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { _types = e.Types.Where(t => t is not null).ToArray()!; }

                SeedLoginState();
                SeedTextDictionaries();
                LoadCardMaster(cardMasterCsv);

                Ready = true;
                return true;
            }
            catch (Exception e)
            {
                Failure = Root(e);
                return false;
            }
        }
    }

    // Data.* is where the login response lands. three passes: later holders reference earlier ones
    static void SeedLoginState()
    {
        var data = T("Wizard.Data") ?? T("Data");
        if (data is null) throw new InvalidOperationException("Wizard.Data not found - is the engine assembly the right build?");
        for (var i = 0; i < 3; i++)
            foreach (var m in data.GetMembers(BindingFlags.Public | BindingFlags.Static))
                TrySeed(m, null, 0);
    }

    // the localisation dictionaries are declared as IDictionary, so the generic seeder skips them, and every skill
    // activation reads its own description text, so a null one takes down the whole rules path. empty is enough: the
    // lookup falls back to the key
    static void SeedTextDictionaries()
    {
        var masterT = T("Wizard.Master");
        var dataT = T("Wizard.Data") ?? T("Data");
        var master = dataT?.GetProperty("Master", Any)?.GetValue(null) ?? dataT?.GetField("Master", Any)?.GetValue(null);
        if (masterT is null || master is null) return;

        foreach (var m in masterT.GetMembers(Any))
        {
            Type ft;
            Func<object?> get;
            Action<object?> set;
            if (m is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0 && p.GetSetMethod(true) is { } setter)
            { ft = p.PropertyType; get = () => p.GetValue(master); set = v => setter.Invoke(master, [v]); }
            else if (m is FieldInfo f && !f.IsInitOnly && !f.IsLiteral && !f.IsStatic)
            { ft = f.FieldType; get = () => f.GetValue(master); set = v => f.SetValue(master, v); }
            else continue;

            if (!ft.IsInterface || !ft.IsGenericType || ft.GetGenericTypeDefinition() != typeof(IDictionary<,>)) continue;
            try
            {
                if (get() is null)
                    set(Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(ft.GetGenericArguments())));
            }
            catch { /* a dictionary we cannot build is one the rules will fall back to the key for */ }
        }
    }

    static void LoadCardMaster(string csv)
    {
        var cm = T("CardMaster") ?? throw new InvalidOperationException("CardMaster not found");
        var idT = T("CardMasterId") ?? throw new InvalidOperationException("CardMasterId not found");
        var def = Enum.Parse(idT, "Default");

        var create = cm.GetMethod("CreateCardMaster", Any) ?? throw new InvalidOperationException("CreateCardMaster not found");
        var master = create.Invoke(null, [def, csv]);

        // CreateCardMaster builds the instance but GetInstance reads a cache, and that cache starts null, so create it
        // before registering, or every later lookup silently returns null
        var cacheField = cm.GetField("_dictCardMaster", Any) ?? throw new InvalidOperationException("_dictCardMaster not found");
        if (cacheField.GetValue(null) is not System.Collections.IDictionary dict)
        {
            dict = (System.Collections.IDictionary)Activator.CreateInstance(cacheField.FieldType)!;
            cacheField.SetValue(null, dict);
        }
        dict[def] = master;
        cm.GetMethod("SetBattleCardMasterId", Any, null, [idT], null)?.Invoke(null, [def]);

        if (cm.GetField("m_cardParameters", Any)?.GetValue(master) is System.Collections.IDictionary cards) CardCount = cards.Count;
    }

    static void TrySeed(MemberInfo m, object? target, int depth)
    {
        if (depth > 3) return;
        Type ft;
        Func<object?> get;
        Action<object?> set;
        if (m is PropertyInfo p && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        { ft = p.PropertyType; get = () => p.GetValue(target); set = v => p.SetValue(target, v); }
        else if (m is FieldInfo f && !f.IsInitOnly && !f.IsLiteral)
        { ft = f.FieldType; get = () => f.GetValue(target); set = v => f.SetValue(target, v); }
        else return;

        if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft.IsArray || ft.IsAbstract || ft.IsInterface) return;
        // a finalizer here is fatal, not untidy: CriWare's p/invokes into an absent native dll kill the process from
        // the finalizer thread, long after the call that created the object returned
        if (Finalizable(ft)) return;

        try
        {
            if (get() is null && ft.GetConstructor(Type.EmptyTypes) is not null)
            {
                set(Activator.CreateInstance(ft));
                if (get() is { } made)
                    foreach (var inner in made.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
                        TrySeed(inner, made, depth + 1);
            }
        }
        catch { /* optional state; the rules guard what they can do without */ }
    }

    static bool Finalizable(Type t) =>
        t.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType != typeof(object);

    static string Root(Exception e)
    {
        while (e.InnerException is not null) e = e.InnerException;
        return $"{e.GetType().Name}: {e.Message}";
    }
}
