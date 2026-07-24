using Microsoft.Win32;

namespace OpenVerse.Common;

// the player name the game shows. the pre-service-end name is NOT recoverable: it lived on Cygames' servers and the
// client only ever read it back from load/index (LoadDetail.cs:146), never persisting it. so: name.txt if the user set
// one, else their Steam persona name, else null (caller falls back to a generated player_xxxxxx)
public static class NameResolver
{
    public static string? Resolve(string baseDir) => FromFile(baseDir) ?? FromSteam();

    // one line, next to the launcher. once written it wins over Steam for good
    public static string? FromFile(string baseDir)
    {
        var f = Path.Combine(baseDir, "name.txt");
        if (!File.Exists(f)) return null;
        var line = File.ReadLines(f).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public static string? FromSteam()
    {
        try
        {
            var vdf = Path.Combine(SteamPath() ?? "", "config", "loginusers.vdf");
            return File.Exists(vdf) ? MostRecentPersona(File.ReadAllLines(vdf)) : null;
        }
        catch { return null; }
    }

    public static string? PersonaFromVdf(string vdf) => MostRecentPersona(vdf.Split('\n'));

    static string? SteamPath()
    {
        var reg = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrEmpty(reg) && Directory.Exists(reg)) return reg;
        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(p)) return p;
        return null;
    }

    // loginusers.vdf nests one block per account; the one flagged MostRecent is who Steam will launch the game as.
    // fall back to the first persona so a single-account install still resolves
    static string? MostRecentPersona(string[] lines)
    {
        string? persona = null, first = null;
        foreach (var line in lines)
        {
            var v = Value(line, "PersonaName");
            if (v is not null) { persona = v; first ??= v; }
            else if (Value(line, "MostRecent") == "1" && persona is not null) return persona;
            else if (line.Trim() == "}") persona = null;  // block ended without MostRecent
        }
        return first;
    }

    // a vdf line is  "Key"<tabs>"Value"
    static string? Value(string line, string key)
    {
        var t = line.Trim();
        if (!t.StartsWith($"\"{key}\"", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = t[(key.Length + 2)..];
        var open = rest.IndexOf('"');
        if (open < 0) return null;
        var close = rest.IndexOf('"', open + 1);
        return close < 0 ? null : rest[(open + 1)..close];
    }
}
