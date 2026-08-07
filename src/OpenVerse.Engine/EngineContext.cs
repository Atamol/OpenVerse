using System.Reflection;
using System.Runtime.Loader;

namespace OpenVerse.Engine;

// One load context per engine instance. The engine keeps the live battle in statics (BattleManagerBase.main,
// GameMgr._instance), so two instances in one context silently share a board
public sealed class EngineContext : AssemblyLoadContext
{
    static bool Shared(string n) =>
        n is "mscorlib" or "System" or "System.Core" or "System.Xml" or "System.Data" or "netstandard"
        || n.StartsWith("System.", StringComparison.Ordinal)
        || n.StartsWith("Microsoft.", StringComparison.Ordinal);

    // The output dir also holds the server's own net10 packages, so this is an allow-list and not a File.Exists probe
    static bool Engine(string n) =>
        n.StartsWith("UnityEngine", StringComparison.Ordinal) || n.StartsWith("Unity.", StringComparison.Ordinal)
        || n is "Assembly-CSharp" or "Assembly-CSharp-firstpass" or "OpenVerse.EngineHost"
             or "CriMw.CriWare.Runtime" or "com.rlabrecque.steamworks.net" or "spine-unity" or "zxing.unity"
             or "Facebook.Unity" or "Facebook.Unity.Settings" or "P31RestKit" or "RedShellSDK" or "Ookii.Dialogs";

    public EngineContext(string name) : base(name, isCollectible: true) { }

    protected override Assembly? Load(AssemblyName an)
    {
        var n = an.Name ?? "";
        if (Shared(n)) return null;
        if (Engine(n))
        {
            var path = Path.Combine(AppContext.BaseDirectory, n + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        // NOT FileNotFoundException: the binder swallows that one and falls through to AppDomain.AssemblyResolve
        throw new InvalidOperationException(
            $"{Name}: refusing to bind '{n}'. it is not in the engine closure, and binding it outside this context " +
            "would share it with every other engine instance");
    }
}
