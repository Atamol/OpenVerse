using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using OpenVerse.Common;

namespace OpenVerse.Launcher;

static class Program
{
    const string Marker = "openverse";
    const string SteamAppId = "453480";
    const string CertPassword = "openverse";
    static readonly string[] RedirectHosts = ["utoongaize.shadowverse.jp", "shadowverse.akamaized.net"];

    static int Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var joinHost = ArgVal(args, "--host") ?? ReadHostFile(baseDir);  // client mode: point the game at a host's server

        if (!IsAdmin())
        {
            Console.WriteLine("elevating (hosts + cert need admin)...");
            return RelaunchAsAdmin(args) ? 0 : 1;
        }

        // tee stdout to launcher.log so debugging never depends on shell redirection
        try
        {
            var logPath = Path.Combine(baseDir, "openverse.log");
            var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var tee = new TeeWriter(Console.Out, new StreamWriter(logStream, new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetOut(tee);
            Console.SetError(tee);
        }
        catch { }

        return joinHost is not null ? RunJoin(baseDir, joinHost) : RunHost(baseDir, args);
    }

    // fan out writes to both console and log file so the server + battle stdout end up in the log along with our own prints
    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char c) { a.Write(c); b.Write(c); }
        public override void Write(string? s) { a.Write(s); b.Write(s); }
        public override void WriteLine(string? s) { a.WriteLine(s); b.WriteLine(s); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    // the game's own data folder (where the card-master cache lives). decks go here rather than beside the exe so that
    // replacing the OpenVerse folder with a new build never touches them - the same reason the cert lives outside it
    static string GameDataDir(string[] args) =>
        ArgVal(args, "--client")
        ?? Environment.GetEnvironmentVariable("OPENVERSE_CLIENT_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "LocalLow", "Cygames", "Shadowverse");

    // db in the game folder. on the first run after the move, carry over a db still beside the exe so decks are not
    // orphaned. returns the path both servers use
    static string ResolveDeckDb(string baseDir, string[] args)
    {
        var explicitPath = Environment.GetEnvironmentVariable("OPENVERSE_DECK_DB");
        if (explicitPath is not null) return explicitPath;

        var gameDir = GameDataDir(args);
        var target = Path.Combine(gameDir, "openverse.db");
        var legacy = Path.Combine(baseDir, "openverse.db");
        try
        {
            if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);
            if (!File.Exists(target) && File.Exists(legacy))
            {
                File.Copy(legacy, target);
                Console.WriteLine($"moved your decks into the game folder: {target}");
            }
        }
        catch (Exception e)
        {
            // the game folder is unwritable or absent: fall back to beside the exe so decks still save somewhere
            Console.WriteLine($"deck db: using the OpenVerse folder ({e.Message})");
            return legacy;
        }
        return target;
    }

    static int RunHost(string baseDir, string[] args)
    {
        // machine-wide (not per-user) so elevation resolves the same file regardless of account, and it survives a rebuild
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenVerse", "openverse.pfx");

        var deckDb = ResolveDeckDb(baseDir, args);

        var cardMaster = Path.Combine(baseDir, "data", "card_master_full.csv.gz");
        if (!File.Exists(cardMaster))
        {
            Console.Error.WriteLine($"card_master not found: {cardMaster}");
            Console.Error.WriteLine("Run openverse-setup first.");
            return 1;
        }

        var advertise = ArgVal(args, "--advertise") ?? DetectLanIp();  // host's reachable IP so friends get a valid node_server_url

        Process? server = null, battle = null;
        var hostsEdited = false;
        X509Certificate2? cert = null;
        try
        {
            cert = CertGen.EnsureSelfSigned(certPath, CertPassword);
            InstallCert(cert);
            File.WriteAllBytes(Path.Combine(baseDir, "openverse.cer"), cert.Export(X509ContentType.Cert));  // hand this to clients
            EditHosts();
            hostsEdited = true;
            battle = StartBattle(baseDir, deckDb);
            server = StartServer(baseDir, advertise, certPath, deckDb);
            Console.WriteLine("server up on 80/443, battle on 3001.");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            Console.WriteLine("launched Shadowverse. close the game to stop.");
            WaitForGameExit();
        }
        finally
        {
            if (hostsEdited) RestoreHosts();
            if (cert != null) RemoveCert(cert);
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            if (battle is { HasExited: false }) battle.Kill(entireProcessTree: true);
            cert?.Dispose();
            Console.WriteLine("stopped. hosts restored.");
        }
        return 0;
    }

    // client side: trust the host's cert, redirect hosts to the host, launch the game, undo on exit. no server, no card_master
    static int RunJoin(string baseDir, string host)
    {
        X509Certificate2 cert;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            cert = X509CertificateLoader.LoadCertificate(http.GetByteArrayAsync($"http://{host}/openverse.cer").GetAwaiter().GetResult());
            Console.WriteLine($"got cert from {host}.");
        }
        catch (Exception e)
        {
            var local = Path.Combine(baseDir, "openverse.cer");  // offline fallback
            if (!File.Exists(local))
            {
                Console.Error.WriteLine($"could not fetch cert from host {host} ({e.Message}). is the host running and reachable (ping {host})?");
                return 1;
            }
            cert = X509CertificateLoader.LoadCertificateFromFile(local);
        }
        // the host serves our load/index, so it has to be told what to call us - it can't read this machine's
        // name.txt or Steam install. do it before the hosts redirect, while {host} still resolves normally
        RegisterName(baseDir, host);
        // our decks live here, not on whoever we joined: hand them over before the game starts
        PushDecks(baseDir, host);
        var hostsEdited = false;
        try
        {
            InstallCert(cert);
            EditHosts(host);
            hostsEdited = true;
            Console.WriteLine($"joined {host}. launching Shadowverse...");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            Console.WriteLine("close the game to stop.");
            WaitForGameExit();
            // pull back whatever we built this session, so the decks follow us to the next host
            PullDecks(baseDir, host);
        }
        finally
        {
            if (hostsEdited) RestoreHosts();
            RemoveCert(cert);
            cert.Dispose();
            Console.WriteLine("stopped. hosts restored.");
        }
        return 0;
    }

    static string DeckFile(string baseDir) => Path.Combine(baseDir, "decks.json");

    static void PushDecks(string baseDir, string host)
    {
        var f = DeckFile(baseDir);
        if (!File.Exists(f)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = File.ReadAllText(f);
            http.PostAsync($"http://{host}/openverse/decks", new StringContent(json)).GetAwaiter().GetResult();
            Console.WriteLine($"sent your decks to {host}.");
        }
        catch (Exception e) { Console.WriteLine($"could not send decks ({e.Message}); the host may not have them."); }
    }

    // the raw IP is used on purpose: hosts is still redirected at this point
    static void PullDecks(string baseDir, string host)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = http.GetStringAsync($"http://{host}/openverse/decks").GetAwaiter().GetResult();
            if (json.Trim() is "" or "[]") return;  // nothing to save; keep the previous file
            File.WriteAllText(DeckFile(baseDir), json);
            Console.WriteLine("saved your decks locally.");
        }
        catch (Exception e) { Console.WriteLine($"could not save decks ({e.Message}); they stay on the host."); }
    }

    // name.txt if the player set one, else their Steam persona. a failure here only costs a nicer name, never the launch
    static void RegisterName(string baseDir, string host)
    {
        var name = NameResolver.Resolve(baseDir);
        if (name is null) { Console.WriteLine("no name.txt and no Steam persona found; the host will generate one."); return; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.PostAsync($"http://{host}/openverse/name", new StringContent(name)).GetAwaiter().GetResult();
            Console.WriteLine($"registered name \"{name}\" with {host}.");
        }
        catch (Exception e) { Console.WriteLine($"could not register name with {host} ({e.Message}); the host will generate one."); }
    }

    static string? ReadHostFile(string baseDir)
    {
        var f = Path.Combine(baseDir, "host.txt");  // one line with the host IP, so a client can just double-click
        return File.Exists(f)
            ? File.ReadLines(f).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
            : null;
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool RelaunchAsAdmin(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args))
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try { Process.Start(psi); return true; }
        catch { Console.Error.WriteLine("elevation cancelled."); return false; }
    }

    static void InstallCert(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        if (!store.Certificates.Contains(cert))
        {
            store.Add(cert);
            Console.WriteLine("cert trusted.");
        }
    }

    static void RemoveCert(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Contains(cert)) store.Remove(cert);
        }
        catch { /* leave it if removal fails */ }
    }

    static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    static string BackupPath => HostsPath + ".openverse.bak";

    static void EditHosts(string ip = "127.0.0.1")
    {
        var text = File.ReadAllText(HostsPath);
        if (!File.Exists(BackupPath)) File.Copy(HostsPath, BackupPath);
        File.WriteAllText(HostsPath, AddBlock(text, RedirectHosts, ip), new UTF8Encoding(false));
        Console.WriteLine($"hosts redirected to {ip}.");
    }

    static void RestoreHosts()
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                File.Copy(BackupPath, HostsPath, overwrite: true);
                File.Delete(BackupPath);
            }
            else
            {
                File.WriteAllText(HostsPath, RemoveBlock(File.ReadAllText(HostsPath)), new UTF8Encoding(false));
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"hosts restore failed: {e.Message}"); }
    }

    static string AddBlock(string content, IEnumerable<string> hosts, string ip)
    {
        // both redirected names still have AAAA records, so an IPv4-only entry is no redirect on a machine with working
        // IPv6: the A lookup is answered here, the AAAA lookup falls through to real DNS and reaches the dead official
        // server. pin v6 alongside. host mode -> ::1; join mode knows only the host's v4, so point v6 at a black hole
        // (::) instead so the name still cannot escape
        var v6 = ip == "127.0.0.1" ? "::1" : "::";
        var sb = new StringBuilder(RemoveBlock(content).TrimEnd('\r', '\n'));
        sb.Append('\n').Append($"# >>> {Marker} >>>\n");
        foreach (var h in hosts)
        {
            sb.Append(ip).Append(' ').Append(h).Append('\n');
            sb.Append(v6).Append(' ').Append(h).Append('\n');
        }
        sb.Append($"# <<< {Marker} <<<\n");
        return sb.ToString();
    }

    static string RemoveBlock(string content)
    {
        var start = $"# >>> {Marker} >>>";
        var end = $"# <<< {Marker} <<<";
        var kept = new List<string>();
        var skip = false;
        foreach (var l in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (l.Trim() == start) { skip = true; continue; }
            if (l.Trim() == end) { skip = false; continue; }
            if (!skip) kept.Add(l);
        }
        return string.Join('\n', kept);
    }

    static string? ArgVal(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    // pick the IP friends connect to: prefer a Radmin VPN adapter, else the first non-loopback IPv4
    static string? DetectLanIp()
    {
        try
        {
            var addrs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => new { n.Name, u.Address }))
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address))
                .ToList();
            var pick = addrs.FirstOrDefault(x => x.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase)) ?? addrs.FirstOrDefault();
            if (pick is null) return null;
            Console.WriteLine($"advertising {pick.Address} (auto-detected: {pick.Name})");
            return pick.Address.ToString();
        }
        catch { return null; }
    }

    static Process StartServer(string baseDir, string? advertise, string certPath, string deckDb)
    {
        var overridePath = Environment.GetEnvironmentVariable("OPENVERSE_SERVER");
        var exe = Path.Combine(baseDir, "OpenVerse.Api.exe");
        ProcessStartInfo psi;
        if (overridePath is { } o)  // dev: a .dll runs via dotnet, an .exe directly
            psi = o.EndsWith(".dll") ? new ProcessStartInfo("dotnet", $"\"{o}\"") : new ProcessStartInfo(o);
        else if (File.Exists(exe))
            psi = new ProcessStartInfo(exe);
        else
            throw new FileNotFoundException(
                $"OpenVerse.Api.exe not found next to the launcher ({baseDir}). Run from the full release folder.");
        psi.Environment["OPENVERSE_LISTEN"] = "1";
        psi.Environment["OPENVERSE_CERT"] = certPath;
        psi.Environment["OPENVERSE_DECK_DB"] = deckDb;
        if (advertise is not null) psi.Environment["OPENVERSE_NODE_URL"] = $"{advertise}:3001";  // battle server address friends connect to
        psi.UseShellExecute = false;
        psi.WorkingDirectory = baseDir;
        return StartWithTee(psi);
    }

    static Process StartBattle(string baseDir, string deckDb)
    {
        var exe = Path.Combine(baseDir, "OpenVerse.Battle.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"OpenVerse.Battle.exe not found next to the launcher ({baseDir}). Run from the full release folder.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = baseDir };
        psi.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:3001";
        psi.Environment["OPENVERSE_DECK_DB"] = deckDb;
        return StartWithTee(psi);
    }

    // capture the child's stdout/stderr and forward through Console.Out so the tee catches them (console + launcher.log)
    static Process StartWithTee(ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("start failed");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static void WaitForGameExit()
    {
        for (var i = 0; i < 30; i++)
        {
            var p = Process.GetProcessesByName("Shadowverse").FirstOrDefault();
            if (p != null) { p.WaitForExit(); return; }
            Thread.Sleep(1000);
        }
        Console.WriteLine("(game not detected. press Enter to stop)");
        Console.ReadLine();
    }
}
