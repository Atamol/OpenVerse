using System.IO.Compression;
using System.Text;
using OpenVerse.Common;

// run before connecting the client to OpenVerse, which overwrites the card_master cache

static string? Arg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var clientData = Arg(args, "--client")
    ?? Path.Combine(userProfile, "AppData", "LocalLow", "Cygames", "Shadowverse");
var outDir = Arg(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "data");

Console.WriteLine($"client: {clientData}");
Console.WriteLine($"out:    {outDir}");
Directory.CreateDirectory(outDir);

var extracted = ExtractCardMaster(clientData, outDir);

string[] bundled = ["practice_info.json", "starter_decks.json", "deck_intro.json"];

Console.WriteLine();
Console.WriteLine(extracted ? "  [ok] card_master" : "  [--] card_master (not extracted)");
foreach (var f in bundled)
    Console.WriteLine(File.Exists(Path.Combine(outDir, f)) ? $"  [ok] {f}" : $"  [--] {f} (missing)");

var ready = extracted && bundled.All(f => File.Exists(Path.Combine(outDir, f)));
Console.WriteLine();
Console.WriteLine(ready ? "ready. run openverse-launcher to play." : "not ready, see [--] above.");
return ready ? 0 : 1;

// line 0 is the hash, line 1 the CSV
static bool ExtractCardMaster(string clientData, string outDir)
{
    var cache = Path.Combine(clientData, "cardmaster", "card_master_1");
    if (!File.Exists(cache))
    {
        Console.Error.WriteLine($"card_master: cache not found at {cache}");
        Console.Error.WriteLine("  Launch the logged-in client once, and run this before connecting to OpenVerse.");
        return false;
    }

    var lines = File.ReadAllLines(cache).Where(l => l.Length > 0).ToArray();
    if (lines.Length < 2)
    {
        Console.Error.WriteLine($"card_master: unexpected cache with {lines.Length} lines");
        return false;
    }

    var csv = WireCrypto.DecryptNode(lines[1]);
    var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    var outPath = Path.Combine(outDir, "card_master_full.csv.gz");
    using (var fs = File.Create(outPath))
    using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        gz.Write(Encoding.UTF8.GetBytes(csv));

    Console.WriteLine($"card_master: {rows} rows -> {outPath}");
    return true;
}
