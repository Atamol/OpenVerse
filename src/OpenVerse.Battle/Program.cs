using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenVerse.Common;

namespace OpenVerse.Battle;

public partial class BattleServer
{
    public static event Action<Session>? SessionCreated;

    public static void Main(string[] args) => CreateApp(args).Run();

    // Boot the engine off the same card master the API serves. Failure is never fatal: the relay is what actually
    // carries battles today, and a host that has not built the engine assemblies should still run.
    static void StartEngine(string dataDir)
    {
        var gz = Path.Combine(dataDir, "card_master_full.csv.gz");
        if (!File.Exists(gz)) { Console.WriteLine("Engine: no card master, not started"); return; }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll")))
        { Console.WriteLine("Engine: assemblies not present, not started"); return; }
        try
        {
            using var fs = File.OpenRead(gz);
            using var z = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new StreamReader(z);
            var t0 = DateTime.UtcNow;
            var csv = sr.ReadToEnd();
            Console.WriteLine(Engine.EngineBoot.Boot(csv)
                ? $"Engine: ready, {Engine.EngineBoot.CardCount} cards in {(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms"
                : $"Engine: boot failed ({Engine.EngineBoot.Failure})");

            // the role is printed so an env var that silently failed to parse is not mistaken for one that took
            Console.WriteLine(Engine.ShadowBridge.Init(csv)
                ? $"Shadow: role={Engine.ShadowBridge.Role} (set OPENVERSE_ENGINE_ROLE to change)"
                : $"Shadow: off ({Engine.ShadowBridge.Failure})");
        }
        catch (Exception e) { Console.WriteLine($"Engine: boot threw ({e.Message})"); }
    }

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        // a silent network loss (cable pull) never sends FIN/RST, so ReceiveAsync would block forever and the peer's
        // disconnect would stay invisible. the client (BestHTTP) answers Ping with Pong off its receive thread, so a
        // ping/pong keepalive surfaces the dead transport well inside the client's 95s BattleStop
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });

        var sessions = new SessionManager();
        // same db the API writes resolved decks into (both processes run from the release dir)
        var dbPath = Environment.GetEnvironmentVariable("OPENVERSE_DECK_DB")
            ?? Path.Combine(AppContext.BaseDirectory, "openverse.db");
        // same data dir the API serves the card master from (both processes run from the release dir)
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var hub = new BattleHub(sessions, new BattleDeckStore(dbPath), BaseCardIdMap.Load(dataDir), CardCostMap.Load(dataDir));

        // the client's own battle engine, running headless. it is only loaded here - nothing routes through it yet -
        // so a host without the (non-redistributable) engine assemblies just runs the relay as before
        StartEngine(dataDir);

        app.Map("/{**_}", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            var battleId = ctx.Request.Headers["BattleId"].ToString();
            var viewerIdEnc = ctx.Request.Headers["viewerId"].ToString();
            var userAgent = ctx.Request.Headers["User-Agent"].ToString();
            string viewerId;
            try { viewerId = WireCrypto.DecryptNode(viewerIdEnc); }
            catch { viewerId = viewerIdEnc; }

            Console.WriteLine($"WS connect: battleId={battleId} viewerId={viewerId} UA={userAgent}");
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = new Session(ws, battleId, viewerId);
            session.OnEvent += (s, pkt, bin) =>
                Console.WriteLine($"[{s.Id}] event: {pkt.EventName ?? "(none)"} ackId={pkt.AckId} binaries={bin.Length}");
            session.OnMsg += (s, uri, payload, ackId) => _ = hub.Dispatch(s, uri, payload, ackId);
            session.OnAliveEmit += s => _ = hub.Alive(s);
            sessions.Add(session);
            SessionCreated?.Invoke(session);
            try { await session.Run(ctx.RequestAborted); }
            finally
            {
                // notify before Remove so the peer lookup still resolves the survivor. a throw here must not skip Remove
                try { await hub.PeerClosed(session); }
                catch (Exception e) { Console.WriteLine($"[{session.Id}] peer-closed notify failed: {e.Message}"); }
                sessions.Remove(session);
                Console.WriteLine($"[{session.Id}] disconnect");
            }
        });

        return app;
    }
}
