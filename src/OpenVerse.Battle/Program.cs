using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenVerse.Common;

namespace OpenVerse.Battle;

public partial class BattleServer
{
    public static event Action<Session>? SessionCreated;

    public static void Main(string[] args) => CreateApp(args).Run();

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.UseWebSockets();

        var sessions = new SessionManager();
        var hub = new BattleHub(sessions);

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
            sessions.Add(session);
            SessionCreated?.Invoke(session);
            try { await session.Run(ctx.RequestAborted); }
            finally { sessions.Remove(session); Console.WriteLine($"[{session.Id}] disconnect"); }
        });

        return app;
    }
}
