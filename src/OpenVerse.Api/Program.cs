using System.Collections.Concurrent;
using MessagePack;
using OpenVerse.Api;
using OpenVerse.Common;

var builder = WebApplication.CreateBuilder(args);

// Real launch sets OPENVERSE_LISTEN=1 to bind 80 + 443. Tests leave it unset so WebApplicationFactory keeps its TestServer.
if (Environment.GetEnvironmentVariable("OPENVERSE_LISTEN") == "1")
{
    var certPath = Environment.GetEnvironmentVariable("OPENVERSE_CERT")
        ?? Path.Combine(AppContext.BaseDirectory, "certs", "openverse.pfx");
    var certPassword = Environment.GetEnvironmentVariable("OPENVERSE_CERT_PW") ?? "openverse";
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(80);
        if (File.Exists(certPath))
            options.ListenAnyIP(443, lo => lo.UseHttps(certPath, certPassword));
        else
            Console.WriteLine($"cert not found at {certPath}, HTTPS disabled");
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

var dbPath = Environment.GetEnvironmentVariable("OPENVERSE_DECK_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "openverse.db");
var deckStore = new DeckStore(dbPath);
List<DefaultDeckBuilder.DefaultDeck> defaultDecks = [];
// Official per-class starter decks extracted from the reference server's default_deck_list (all basic-set,
// 40 cards). Falls back to a generated deck if the file is missing.
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
// 大会上位デッキ紹介 decks (data/deck_intro.json), grouped by expansion period
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

// Practice (CP対戦) opponent roster from the reference server
var practicePath = Path.Combine(AppContext.BaseDirectory, "data", "practice_info.json");
var practiceRoster = File.Exists(practicePath) ? File.ReadAllText(practicePath).Trim() : "[]";
var practiceHandler = new PracticeHandler(practiceRoster, deckHandler);
Console.WriteLine($"Practice: {(practiceRoster == "[]" ? "no roster" : $"loaded {System.Text.Json.JsonDocument.Parse(practiceRoster).RootElement.GetArrayLength()} opponents")}");

var deckCodeStore = new DeckCodeStore(Path.Combine(AppContext.BaseDirectory, "deckcodes.db"));
var purgedCodes = deckCodeStore.PurgeOlderThan(TimeSpan.FromDays(30));
if (purgedCodes > 0) Console.WriteLine($"DeckCodes: purged {purgedCodes} codes older than 30 days");
var deckCodeHandler = new DeckCodeHandler(deckCodeStore);

var users = new UserStore();
var rooms = new RoomStore
{
    NodeServerUrl = Environment.GetEnvironmentVariable("OPENVERSE_NODE_URL") ?? "127.0.0.1:3001",
};
var roomHandler = new RoomHandler(rooms, users);

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
// voice_cues.tsv: "<id>\t<cue1>,<cue2>,..." used only by the CardMasterBuilder fallback
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
// Premium (foil) is off by default: it does not render foil shimmer on the pristine client, breaks
// related-card navigation, and its interleaved rows disturb the collection sort order. Opt in with
// OPENVERSE_PREMIUM=1 once a real foil pipeline exists.
var premiumEnabled = Environment.GetEnvironmentVariable("OPENVERSE_PREMIUM") == "1";
// The real card_master (all cards incl. sets 130-132, authentic voice cues) from the reference dump.
// Preferred over the shadowverse_ja.json reconstruction, which stopped at set 129 and mis-mapped voices.
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
    string? reqJson = null;
    if (udid is not null && body.Length > 32)
    {
        try { reqJson = WireCodec.DecodeRequest(body, udid); Console.WriteLine($"  req: {reqJson}"); }
        catch (Exception e) { Console.WriteLine($"  decode failed: {e.Message}"); }
    }

    if (path.StartsWith("/dl/", StringComparison.OrdinalIgnoreCase))
    {
        var name = Path.GetFileName(path);
        var file = Path.Combine(manifestDir, name);
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

    // deck_code goes to the portal (dead backend), unencrypted msgpack, self-hosted here: the body is
    // raw msgpack, the reply is base64(msgpack) of the usual { data_headers, data } envelope.
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

    string Response(string p, string? json, string userKey)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string data;
        string handler;
        if (DeckHandler.CanHandle(p) && json is not null) { handler = "deck"; data = deckHandler.Handle(p, json, userKey); }
        else if (RoomHandler.CanHandle(p) && json is not null) { handler = "room"; data = roomHandler.Handle(p, json, userKey); }
        else if (p.Contains("immutable_data/card_master")) { handler = "card_master"; data = $"{{\"card_master\":\"{cardMasterPayload}\"}}"; }
        else if (p.Contains("load/index"))
        {
            handler = "stub:load_index";
            var raw = stubs.GetValueOrDefault("load_index", "{}");
            raw = raw.Replace("\"user_sleeve_list\": []", "\"user_sleeve_list\": " + sleeveListJson);
            var deckGroups = deckHandler.BuildLoadIndexDeckGroups(userKey);
            var deckGroupsInner = deckGroups.Substring(1, deckGroups.Length - 2);
            // open_battle_field_id_list: unlocked battle backgrounds. Without it LoadDetail leaves
            // OpenBattleFieldIdList null and the battle scene NREs in CalculationRandomStage at start.
            const string battleFields = "[1,2,3,4,5,6,7,10,18,30,31,41,43,51,61,71]";
            data = raw.TrimEnd().EndsWith("}")
                ? raw.TrimEnd().TrimEnd('}') + $",\"card_master_hash\":\"{cardMasterHash}\",\"user_card_list\":{userCardListJson},\"open_battle_field_id_list\":{battleFields},{deckGroupsInner}}}"
                : raw;
        }
        else if (p.Contains("mypage/index")) { handler = "stub:mypage_index"; data = stubs.GetValueOrDefault("mypage_index", "{}"); }
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
