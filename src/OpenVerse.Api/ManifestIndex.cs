using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenVerse.Api;

// a client handed another install's manifests asks for a multi-GB download nobody can serve, and it saves whatever it
// was given, so one bad session poisons its own copy for good
public sealed class ManifestIndex
{
    static readonly string[] CacheDirs = ["a", "b", "s", "v", "m", "f"];

    // the client pre-fetches these before letting you past the title, so a row it can't satisfy has to leave them
    static readonly HashSet<string> Predownload = new(StringComparer.Ordinal) { "common", "tutorial" };

    // an existing category that isn't pre-fetched, so unservable rows stay well-formed
    const string Deferred = "everytmp";

    readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    readonly record struct Entry(string Md5, long Size, long Stamp);

    public int Count => _byName.Count;
    public int Hashed { get; private set; }

    ManifestIndex() { }

    // a full pass reads tens of GB, so it runs across cores and the sidecar keeps it to once per install
    public static ManifestIndex Build(string bundleDir, string sidecar, Action<string>? log = null)
    {
        var index = new ManifestIndex();
        var cached = LoadSidecar(sidecar);

        var files = CacheDirs
            .Select(sub => Path.Combine(bundleDir, sub))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .ToList();

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var hashed = 0;
        Parallel.ForEach(files, path =>
        {
            FileInfo info;
            long stamp;
            try
            {
                info = new FileInfo(path);
                stamp = info.LastWriteTimeUtc.Ticks;
            }
            catch (IOException) { return; }

            if (cached.TryGetValue(info.Name, out var hit) && hit.Size == info.Length && hit.Stamp == stamp)
            {
                found[info.Name] = new Entry(hit.Md5, hit.Size, stamp);
                return;
            }
            try
            {
                using var fs = File.OpenRead(path);
                found[info.Name] = new Entry(Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant(), info.Length, stamp);
                Interlocked.Increment(ref hashed);
            }
            catch (IOException) { }
        });

        foreach (var (name, e) in found) index._byName[name] = e;
        index.Hashed = hashed;

        if (hashed > 0)
            SaveSidecar(sidecar, found.Select(kv => $"{kv.Key},{kv.Value.Size},{kv.Value.Stamp},{kv.Value.Md5}"), log);
        return index;
    }

    readonly record struct Cached(string Md5, long Size, long Stamp);

    static Dictionary<string, Cached> LoadSidecar(string path)
    {
        var d = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split(',');
                if (p.Length != 4) continue;
                if (long.TryParse(p[1], out var size) && long.TryParse(p[2], out var stamp))
                    d[p[0]] = new Cached(p[3], size, stamp);
            }
        }
        catch (IOException) { }
        return d;
    }

    static void SaveSidecar(string path, IEnumerable<string> lines, Action<string>? log)
    {
        try
        {
            File.WriteAllLines(path, lines);
        }
        catch (IOException e) { log?.Invoke($"could not save the bundle index: {e.Message}"); }
    }

    public bool TryGet(string assetName, out string md5, out long size)
    {
        // audio rows carry the cache subdir the filename itself lacks
        var slash = assetName.LastIndexOf('/');
        var leaf = slash >= 0 ? assetName[(slash + 1)..] : assetName;
        if (_byName.TryGetValue(leaf.Trim(), out var e)) { md5 = e.Md5; size = e.Size; return true; }
        md5 = ""; size = 0; return false;
    }

    static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.###", CultureInfo.InvariantCulture);

    // Name,hash,category,sizeMiB,hash,sizeMiB. Columns 1 and 4 are the normal and small-resource variants, and this host
    // only ever has one copy of a bundle
    public byte[] Rewrite(byte[] manifest, out int rewritten, out int deferred)
    {
        rewritten = 0;
        deferred = 0;
        var outLines = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(manifest).Split('\n'))
        {
            var p = line.Split(',');
            if (p.Length < 6) { outLines.Add(line); continue; }
            if (TryGet(p[0], out var md5, out var size))
            {
                var mib = Mib(size);
                outLines.Add($"{p[0]},{md5},{p[2]},{mib},{md5},{mib}");
                rewritten++;
            }
            else if (Predownload.Contains(p[2]))
            {
                outLines.Add($"{p[0]},{p[1]},{Deferred},{p[3]},{p[4]},{p[5]}");
                deferred++;
            }
            else outLines.Add(line);
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', outLines));
    }
}
