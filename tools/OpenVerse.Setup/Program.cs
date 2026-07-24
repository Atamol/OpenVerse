using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenVerse.Common;

// content hash of the final (post-shutdown) card_master. a client whose cache hashes differently synced
// earlier, so some cards render pre-patch data
const string LatestHash = "0b82bbcc494650f0079f5142636b6d3fc8770e8cae5a52f08d2aaeea057d912f";

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

try { Console.OutputEncoding = Encoding.UTF8; } catch { }
Console.WriteLine($"client: {clientData}");
Console.WriteLine($"out:    {outDir}");
Directory.CreateDirectory(outDir);

var csv = ExtractCardMaster(clientData, outDir);
var extracted = csv is not null;
var latest = extracted && ContentHash(csv!) == LatestHash;

string[] bundled = ["practice_info.json", "starter_decks.json", "deck_intro.json"];
var haveBundled = bundled.All(f => File.Exists(Path.Combine(outDir, f)));

Console.WriteLine();
Console.WriteLine(extracted ? "  [ok] card_master" : "  [--] card_master (not extracted)");
foreach (var f in bundled)
    Console.WriteLine(File.Exists(Path.Combine(outDir, f)) ? $"  [ok] {f}" : $"  [--] {f} (missing)");
if (extracted && !latest)
    Console.WriteLine("  [!] card_master is not the final version");

var ready = extracted && haveBundled;
Console.WriteLine();

const uint MbError = 0x10, MbWarn = 0x30, MbInfo = 0x40;
var (title, body, icon) =
    !ready ? ("セットアップ失敗", "セットアップに失敗しました。コンソールの [--] を確認してください。", MbError)
    : !latest ? ("セットアップ完了 (警告)", "セットアップは完了しましたが、card_master が最新ではありません。\n終了直前まで同期していないクライアントのため、一部のカードが古い可能性があります。", MbWarn)
    : ("セットアップ完了", "セットアップが完了しました。openverse-launcher を実行して遊べます。", MbInfo);

Console.WriteLine(body.Replace("\n", " "));
if (!args.Contains("--quiet")) Notify(title, body, icon);
return ready ? 0 : 1;

// try each cache line and take the one that decrypts to the CSV (line layout differs across cache slots)
static string? ExtractCardMaster(string clientData, string outDir)
{
    var cache = Path.Combine(clientData, "cardmaster", "card_master_1");
    if (!File.Exists(cache))
    {
        Console.Error.WriteLine($"card_master: cache not found at {cache}");
        Console.Error.WriteLine("  Launch the logged-in client once, and run this before connecting to OpenVerse.");
        return null;
    }

    string? csv = null;
    foreach (var line in File.ReadAllLines(cache).Where(l => l.Length > 0))
    {
        try
        {
            var d = WireCrypto.DecryptNode(line);
            if (d.Split('\n').Length > 100) { csv = d; break; }
        }
        catch { }
    }
    if (csv is null) { Console.Error.WriteLine("card_master: could not decode the cache"); return null; }

    var outPath = Path.Combine(outDir, "card_master_full.csv.gz");
    using (var fs = File.Create(outPath))
    using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        gz.Write(Encoding.UTF8.GetBytes(csv));

    Console.WriteLine($"card_master: {csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} rows -> {outPath}");
    return csv;
}

static string ContentHash(string csv)
{
    var rows = csv.Replace("\r", "").Split('\n').Where(l => l.Length > 0).OrderBy(l => l, StringComparer.Ordinal);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows)))).ToLowerInvariant();
}

static void Notify(string title, string body, uint icon)
{
    if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, body, title, icon);
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
