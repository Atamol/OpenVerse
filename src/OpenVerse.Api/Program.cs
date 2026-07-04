using System.Collections.Concurrent;
using OpenVerse.Common;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var stubs = new Dictionary<string, string>();
var stubDir = Path.Combine(AppContext.BaseDirectory, "stubs");
if (Directory.Exists(stubDir))
    foreach (var f in Directory.GetFiles(stubDir, "*.json"))
        stubs[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f);

var udids = new ConcurrentDictionary<string, string>();

var manifestDir = Path.Combine(AppContext.BaseDirectory, "stubs", "manifest");

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
    if (udid is not null && body.Length > 32)
    {
        try { Console.WriteLine($"  req: {WireCodec.DecodeRequest(body, udid)}"); }
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

    if (udid is not null)
        await context.Response.WriteAsync(WireCodec.EncodeResponse(Response(path), udid));
    else
        await context.Response.WriteAsync("{}");

    string Response(string p)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string data =
            p.Contains("load/index") ? stubs.GetValueOrDefault("load_index", "{}") :
            p.Contains("game_start") ? stubs.GetValueOrDefault("game_start", "{}") :
            "{}";
        return $"{{\"data_headers\":{{\"result_code\":1,\"servertime\":{now}}},\"data\":{data}}}";
    }
});

app.Run();

public partial class Program;
