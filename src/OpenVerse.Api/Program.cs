using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MessagePack;
using OpenVerse.Api;
using OpenVerse.Common;

var builder = WebApplication.CreateBuilder(args);

// tests leave OPENVERSE_LISTEN unset so WebApplicationFactory keeps its TestServer (real launch binds 80 + 443)
byte[] certDer = [];
if (Environment.GetEnvironmentVariable("OPENVERSE_LISTEN") == "1")
{
    var certPath = Environment.GetEnvironmentVariable("OPENVERSE_CERT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenVerse", "openverse.pfx");
    var certPassword = Environment.GetEnvironmentVariable("OPENVERSE_CERT_PW") ?? "openverse";
    using (var cert = CertGen.EnsureSelfSigned(certPath, certPassword))
        certDer = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);  // served at /openverse.cer for auto-trust
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(80);
        options.ListenAnyIP(443, lo => lo.UseHttps(certPath, certPassword));
    });
}

var app = builder.Build();

var stubs = new Dictionary<string, string>();
var stubDir = Path.Combine(AppContext.BaseDirectory, "stubs");
if (Directory.Exists(stubDir))
    foreach (var f in Directory.GetFiles(stubDir, "*.json"))
        stubs[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f);

var udids = new ConcurrentDictionary<string, string>();

var manifestDir = Path.Combine(AppContext.BaseDirectory, "stubs", "manifest");

// fresh clients download bundles the host already has cached. the CDN keys them by content hash, the client
// cache stores them by name, so map manifest hash -> cached file to serve them from /dl/Resource
var bundleDir = Environment.GetEnvironmentVariable("OPENVERSE_BUNDLE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Cygames", "Shadowverse");
var bundleByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (Directory.Exists(bundleDir) && Directory.Exists(manifestDir))
{
    var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var sub in new[] { "a", "b", "s", "v", "m", "f" })
    {
        var dir = Path.Combine(bundleDir, sub);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir))
                byName[Path.GetFileName(f)] = f;
    }
    foreach (var mf in Directory.GetFiles(manifestDir))
        foreach (var line in File.ReadLines(mf))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            // audio manifests prefix the name with a cache-subdir (b/, s/, v/) that the cached filename lacks
            var name = parts[0];
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (byName.TryGetValue(name, out var full)) bundleByHash[parts[1]] = full;
        }
    Console.WriteLine($"Bundles: mapped {bundleByHash.Count} hashes to cached files under {bundleDir}");
}

var dbPath = Environment.GetEnvironmentVariable("OPENVERSE_DECK_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "openverse.db");
var deckStore = new DeckStore(dbPath);
List<DefaultDeckBuilder.DefaultDeck> defaultDecks = [];
var starterSrc = Path.Combine(AppContext.BaseDirectory, "data", "starter_decks.json");
if (File.Exists(starterSrc))
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(starterSrc));
    defaultDecks = doc.RootElement.EnumerateArray().Select(e => new DefaultDeckBuilder.DefaultDeck(
        e.GetProperty("class_id").GetInt32(),
        [.. e.GetProperty("card_id_array").EnumerateArray().Select(c => c.GetInt32())])).ToList();
    Console.WriteLine($"DefaultDecks: loaded {defaultDecks.Count} official starter decks");
}
else
{
    var defaultDeckSrc = Path.Combine(AppContext.BaseDirectory, "data", "shadowverse_ja.json");
    if (File.Exists(defaultDeckSrc))
    {
        using var fs = File.OpenRead(defaultDeckSrc);
        defaultDecks = DefaultDeckBuilder.Build(fs);
        Console.WriteLine($"DefaultDecks: built {defaultDecks.Count} starter decks (generated fallback)");
    }
}
List<IntroSeries> introSeries = [];
var introPath = Path.Combine(AppContext.BaseDirectory, "data", "deck_intro.json");
if (File.Exists(introPath))
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(introPath));
    foreach (var s in doc.RootElement.EnumerateArray())
    {
        string Str(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        int Int(System.Text.Json.JsonElement e, string k, int def) => e.TryGetProperty(k, out var v) ? v.GetInt32() : def;
        var decks = s.GetProperty("decks").EnumerateArray().Select(e => new IntroDeck(
            e.GetProperty("name").GetString() ?? "デッキ",
            e.GetProperty("clan").GetInt32(),
            e.GetProperty("format").GetInt32(),
            [.. e.GetProperty("card_ids").EnumerateArray().Select(c => c.GetInt32())],
            e.GetProperty("thumbnail_card_id").GetInt32(),
            Str(e, "player_name"), Str(e, "introduction"))).ToList();
        introSeries.Add(new IntroSeries(s.GetProperty("series_id").GetInt32(), s.GetProperty("series_name").GetString() ?? "", decks,
            Int(s, "display_format", 2), Int(s, "is_ts_rotation", 0)));
    }
    Console.WriteLine($"IntroDecks: loaded {introSeries.Count} periods, {introSeries.Sum(x => x.Decks.Count)} decks");
}
var deckHandler = new DeckHandler(deckStore, defaultDecks, introSeries);

var practicePath = Path.Combine(AppContext.BaseDirectory, "data", "practice_info.json");
var practiceRoster = File.Exists(practicePath) ? File.ReadAllText(practicePath).Trim() : "[]";
var practiceHandler = new PracticeHandler(practiceRoster, deckHandler);
Console.WriteLine($"Practice: {(practiceRoster == "[]" ? "no roster" : $"loaded {System.Text.Json.JsonDocument.Parse(practiceRoster).RootElement.GetArrayLength()} opponents")}");

var deckCodeStore = new DeckCodeStore(Path.Combine(AppContext.BaseDirectory, "deckcodes.db"));
var purgedCodes = deckCodeStore.PurgeOlderThan(TimeSpan.FromDays(30));
if (purgedCodes > 0) Console.WriteLine($"DeckCodes: purged {purgedCodes} codes older than 30 days");
var deckCodeHandler = new DeckCodeHandler(deckCodeStore);

var users = new UserStore();
// the host's own game reaches us over loopback (its hosts file points at 127.0.0.1), so this names the host. a joining
// client can't be read from here - its launcher POSTs its name to /openverse/name and we key that by source IP
var hostName = NameResolver.Resolve(AppContext.BaseDirectory);
Console.WriteLine($"Name: host = {hostName ?? "(unresolved, using generated)"}");
var ipNames = new ConcurrentDictionary<string, string>();
// a joining client keeps its decks on its own machine: its launcher pushes them here before the game starts and pulls
// them back after. both are keyed by source IP because only the game knows its udid, and it hasn't spoken yet at push time
var ipUdids = new ConcurrentDictionary<string, string>();
var pendingDecks = new ConcurrentDictionary<string, string>();
var rooms = new RoomStore
{
    NodeServerUrl = Environment.GetEnvironmentVariable("OPENVERSE_NODE_URL") ?? "127.0.0.1:3001",
};
var battleDeckStore = new BattleDeckStore(dbPath);
var roomHandler = new RoomHandler(rooms, users, deckStore, battleDeckStore);

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var cardDataPath = Path.Combine(dataDir, "shadowverse_ja.json");
HashSet<int> LoadIds(string p) => File.Exists(p)
    ? [.. File.ReadLines(p).Select(l => int.TryParse(l.Trim(), out var n) ? n : -1).Where(n => n >= 0)]
    : [];
Dictionary<int, string[]> LoadTextIds(string p)
{
    var d = new Dictionary<int, string[]>();
    if (!File.Exists(p)) return d;
    foreach (var line in File.ReadLines(p))
    {
        var parts = line.Split('\t');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var id)) continue;
        d[id] = [.. Enumerable.Range(1, 4).Select(i => i < parts.Length ? parts[i] : "")];
    }
    return d;
}
// voice_cues.tsv: "<id>\t<cue1>,<cue2>,..."
Dictionary<int, string> LoadVoiceCues(string p)
{
    var d = new Dictionary<int, string>();
    if (!File.Exists(p)) return d;
    foreach (var line in File.ReadLines(p))
    {
        var parts = line.Split('\t');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var id)) continue;
        var cues = parts[1].Trim();
        if (cues.Length > 0) d[id] = cues;
    }
    return d;
}
var textIds = LoadTextIds(Path.Combine(dataDir, "text_ids.tsv"));
var voiceCues = LoadVoiceCues(Path.Combine(dataDir, "voice_cues.tsv"));
var cvIds = LoadIds(Path.Combine(dataDir, "cv_ids.txt"));
var tnKeysPath = Path.Combine(dataDir, "tn_keys.txt");
var tnKeys = File.Exists(tnKeysPath)
    ? File.ReadLines(tnKeysPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
    : new HashSet<string>();
// premium (foil) off by default: no shimmer on the pristine client, breaks related-card nav, and its
// interleaved rows disturb the collection sort. OPENVERSE_PREMIUM=1 to opt in
var premiumEnabled = Environment.GetEnvironmentVariable("OPENVERSE_PREMIUM") == "1";
// The full card_master (all cards incl. sets 130-132, authentic voice cues)
// preferred over the shadowverse_ja.json reconstruction, which stopped at set 129 and mis-mapped voices
var realMasterGz = Path.Combine(dataDir, "card_master_full.csv.gz");
string cardCsv;
if (File.Exists(realMasterGz))
{
    using var fz = File.OpenRead(realMasterGz);
    using var gz = new System.IO.Compression.GZipStream(fz, System.IO.Compression.CompressionMode.Decompress);
    using var sr = new StreamReader(gz);
    cardCsv = sr.ReadToEnd();
    Console.WriteLine($"CardMaster: loaded {cardCsv.Split('\n').Length} real rows from {realMasterGz}");
}
else if (File.Exists(cardDataPath))
{
    using var fs = File.OpenRead(cardDataPath);
    cardCsv = CardMasterBuilder.BuildCsv(fs, textIds, cvIds, tnKeys, premiumEnabled, voiceCues);
    Console.WriteLine($"CardMaster: built {cardCsv.Split('\n').Length - 1} rows from {cardDataPath} (reconstruction fallback)");
}
else
{
    cardCsv = "";
    Console.WriteLine("CardMaster: no source, serving empty CSV");
}
var cardMasterPayload = CardMasterCodec.Encode(cardCsv);
var cardMasterHash = "openverse-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cardCsv)))[..12].ToLowerInvariant();

string userCardListJson = "[]";
if (File.Exists(realMasterGz))
{
    userCardListJson = UserCardListBuilder.BuildFromCsv(cardCsv);
    Console.WriteLine($"UserCardList: granted {userCardListJson.Split("\"card_id\"").Length - 1} cards (from real master)");
}
else if (File.Exists(cardDataPath))
{
    using var fs = File.OpenRead(cardDataPath);
    userCardListJson = UserCardListBuilder.BuildJson(fs, premium: premiumEnabled);
    Console.WriteLine($"UserCardList: granted {userCardListJson.Split("\"card_id\"").Length - 1} cards");
}

var sleeveManifest = Path.Combine(manifestDir, "sleeve_assetmanifest");
var sleeveListJson = SleeveListBuilder.BuildJson(sleeveManifest);
Console.WriteLine($"SleeveList: granted {sleeveListJson.Split("\"sleeve_id\"").Length - 1} sleeves");

// re-key the pushed decks onto this host's udid for the player: deck_no/order carry over, the udid does not
void ImportDecks(string udid, string json)
{
    try
    {
        var decks = JsonSerializer.Deserialize<List<Deck>>(json);
        if (decks is null) return;
        foreach (var d in decks) { d.UserKey = udid; deckStore.Save(d); }
        Console.WriteLine($"Decks: imported {decks.Count} for udid={udid}");
    }
    catch (Exception e) { Console.WriteLine($"Decks: import failed ({e.Message})"); }
}

app.MapMethods("/{**path}", ["GET", "POST"], async context =>
{
    var req = context.Request;
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    var path = req.Path.Value ?? "";

    string? Hdr(string name) { var v = req.Headers[name]; return v.Count > 0 ? v.ToString() : null; }
    var sid = Hdr("SID");
    var rawUdid = Hdr("UDID");
    string? udid = null;
    if (rawUdid is not null)
    {
        udid = HeaderCodec.Decode(rawUdid);
        if (sid is not null) udids[sid] = udid;
    }
    else if (sid is not null)
        udids.TryGetValue(sid, out udid);

    Console.WriteLine($"\n{req.Method} {path}  ({body.Length} bytes) udid={udid}");
    // bind whoever this box registered (or the host, over loopback) to the udid, so every screen shows the same name
    if (udid is not null)
    {
        var ip = context.Connection.RemoteIpAddress;
        var resolved = ip is not null && IPAddress.IsLoopback(ip)
            ? hostName
            : ipNames.GetValueOrDefault(ip?.ToString() ?? "");
        if (resolved is not null) users.GetOrCreate(udid).Name = resolved;
        // now that this box's udid is known, adopt whatever its launcher pushed and let it pull back later
        if (ip is not null)
        {
            ipUdids[ip.ToString()] = udid;
            if (pendingDecks.TryRemove(ip.ToString(), out var pushed)) ImportDecks(udid, pushed);
        }
    }
    string? reqJson = null;
    if (udid is not null && body.Length > 32)
    {
        try { reqJson = WireCodec.DecodeRequest(body, udid); Console.WriteLine($"  req: {reqJson}"); }
        catch (Exception e) { Console.WriteLine($"  decode failed: {e.Message}"); }
    }

    // a joining client's launcher posts its own name here (plain HTTP, same as the cert fetch) since we can't read that
    // machine's name.txt or Steam install. keyed by source IP: the game then connects from the same box
    if (path.Equals("/openverse/name", StringComparison.OrdinalIgnoreCase))
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var posted = System.Text.Encoding.UTF8.GetString(body).Trim();
        if (ip is not null && posted.Length > 0)
        {
            ipNames[ip] = posted;
            Console.WriteLine($"Name: {ip} = {posted}");
        }
        context.Response.StatusCode = 204;
        return;
    }

    // a client's decks live on its own machine, not on whoever it happened to join. push before the game starts (the
    // udid is not known yet, so hold it by IP), pull after it exits
    if (path.Equals("/openverse/decks", StringComparison.OrdinalIgnoreCase))
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (ip is null) { context.Response.StatusCode = 400; return; }
        if (req.Method == "POST")
        {
            var pushed = System.Text.Encoding.UTF8.GetString(body);
            if (ipUdids.TryGetValue(ip, out var known)) ImportDecks(known, pushed);
            else pendingDecks[ip] = pushed;
            context.Response.StatusCode = 204;
            return;
        }
        var decks = ipUdids.TryGetValue(ip, out var u) ? deckStore.List(u) : [];
        Console.WriteLine($"Decks: {ip} pulled {decks.Count}");
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(decks));
        return;
    }

    if (path.Equals("/openverse.cer", StringComparison.OrdinalIgnoreCase) && certDer.Length > 0)
    {
        Console.WriteLine("  -> serving cert");
        context.Response.ContentType = "application/x-x509-ca-cert";
        await context.Response.Body.WriteAsync(certDer);
        return;
    }

    if (path.StartsWith("/dl/", StringComparison.OrdinalIgnoreCase))
    {
        var name = Path.GetFileName(path);
        var file = Path.Combine(manifestDir, name);
        if (!File.Exists(file) && bundleByHash.TryGetValue(name, out var cached)) file = cached;
        if (File.Exists(file))
        {
            Console.WriteLine($"  -> serving {file} ({new FileInfo(file).Length} bytes)");
            await context.Response.SendFileAsync(file);
        }
        else
        {
            Console.WriteLine($"  -> 404 {name}");
            context.Response.StatusCode = 404;
        }
        return;
    }

    // deck_code (dead portal backend), self-hosted: request is raw msgpack, reply is base64(msgpack) of { data_headers, data }
    if (DeckCodeHandler.CanHandle(path))
    {
        string reqMp;
        try { reqMp = MessagePackSerializer.ConvertToJson(body); }
        catch { reqMp = "{}"; }
        Console.WriteLine($"  deck_code req: {reqMp}");
        var (rc, data) = deckCodeHandler.Handle(reqMp);
        long dcNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wrapped = $"{{\"data_headers\":{{\"result_code\":{rc},\"servertime\":{dcNow}}},\"data\":{data}}}";
        Console.WriteLine($"  -> [deck_code rc={rc}] {data}");
        await context.Response.WriteAsync(Convert.ToBase64String(MessagePackSerializer.ConvertFromJson(wrapped)));
        return;
    }

    if (udid is not null)
        await context.Response.WriteAsync(WireCodec.EncodeResponse(Response(path, reqJson, udid), udid));
    else
        await context.Response.WriteAsync("{}");

    // the stub ships a placeholder, not a name: whoever runs the host would otherwise be the name every client sees
    string WithName(string stub, string userKey)
    {
        var name = users.GetOrCreate(userKey).Name;
        var quoted = JsonSerializer.Serialize(name);
        return stub.Replace("\"__OPENVERSE_NAME__\"", quoted);
    }

    string Response(string p, string? json, string userKey)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // SignUpTask reads viewer_id/short_udid/udid from data_headers (not data), and aborts if the udid isn't its own
        if (p.Contains("tool/signup"))
        {
            var u = users.GetOrCreate(userKey);
            Console.WriteLine($"  -> [signup] viewer_id={u.ViewerId}");
            return $"{{\"data_headers\":{{\"result_code\":1,\"servertime\":{now},\"viewer_id\":{u.ViewerId},\"short_udid\":{u.ViewerId},\"udid\":\"{userKey}\"}},\"data\":{{}}}}";
        }
        string data;
        string handler;
        if (DeckHandler.CanHandle(p) && json is not null) { handler = "deck"; data = deckHandler.Handle(p, json, userKey); }
        else if (RoomHandler.CanHandle(p) && json is not null) { handler = "room"; data = roomHandler.Handle(p, json, userKey); }
        else if (p.Contains("immutable_data/card_master")) { handler = "card_master"; data = $"{{\"card_master\":\"{cardMasterPayload}\"}}"; }
        else if (p.Contains("load/index"))
        {
            handler = "stub:load_index";
            var raw = WithName(stubs.GetValueOrDefault("load_index", "{}"), userKey);
            raw = raw.Replace("\"user_sleeve_list\": []", "\"user_sleeve_list\": " + sleeveListJson);
            var deckGroups = deckHandler.BuildLoadIndexDeckGroups(userKey);
            var deckGroupsInner = deckGroups.Substring(1, deckGroups.Length - 2);
            // open_battle_field_id_list: without it the battle scene NREs in CalculationRandomStage at start
            const string battleFields = "[1,2,3,4,5,6,7,10,18,30,31,41,43,51,61,71]";
            data = raw.TrimEnd().EndsWith("}")
                ? raw.TrimEnd().TrimEnd('}') + $",\"card_master_hash\":\"{cardMasterHash}\",\"user_card_list\":{userCardListJson},\"open_battle_field_id_list\":{battleFields},{deckGroupsInner}}}"
                : raw;
        }
        else if (p.Contains("mypage/index")) { handler = "stub:mypage_index"; data = WithName(stubs.GetValueOrDefault("mypage_index", "{}"), userKey); }
        else if (p.Contains("mypage/refresh")) { handler = "stub:mypage_refresh"; data = stubs.GetValueOrDefault("mypage_refresh", "{}"); }
        else if (p.Contains("introduce_deck/series_list")) { handler = "introduce_deck_series"; data = deckHandler.IntroduceDeckSeriesList(); }
        else if (p.Contains("introduce_deck/info")) { handler = "introduce_deck"; data = deckHandler.IntroduceDeckInfo(json ?? "{}"); }
        else if (PracticeHandler.CanHandle(p)) { handler = "practice"; data = practiceHandler.Handle(p, userKey); }
        else if (p.Contains("game_start")) { handler = "stub:game_start"; data = stubs.GetValueOrDefault("game_start", "{}"); }
        else { handler = "UNHANDLED"; data = "{}"; }
        Console.WriteLine($"  -> [{handler}] {data.Substring(0, Math.Min(200, data.Length))}");
        return $"{{\"data_headers\":{{\"result_code\":1,\"servertime\":{now}}},\"data\":{data}}}";
    }
});

app.Run();

public partial class Program;
