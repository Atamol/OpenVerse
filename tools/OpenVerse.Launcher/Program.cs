using System.Diagnostics;
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
        var certPath = Path.Combine(baseDir, "certs", "openverse.pfx");

        if (!IsAdmin())
        {
            Console.WriteLine("elevating (hosts + cert need admin)...");
            return RelaunchAsAdmin(args) ? 0 : 1;
        }

        var cardMaster = Path.Combine(baseDir, "data", "card_master_full.csv.gz");
        if (!File.Exists(cardMaster))
        {
            Console.Error.WriteLine($"card_master not found: {cardMaster}");
            Console.Error.WriteLine("Run openverse-setup first.");
            return 1;
        }

        Process? server = null;
        var hostsEdited = false;
        X509Certificate2? cert = null;
        try
        {
            cert = CertGen.EnsureSelfSigned(certPath, CertPassword);
            InstallCert(cert);
            EditHosts();
            hostsEdited = true;
            server = StartServer(baseDir);
            Console.WriteLine("server up on 80/443.");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            Console.WriteLine("launched Shadowverse. close the game (or press Enter here) to stop.");
            WaitForGameExit();
        }
        finally
        {
            if (hostsEdited) RestoreHosts();
            if (cert != null) RemoveCert(cert);
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            cert?.Dispose();
            Console.WriteLine("stopped. hosts restored.");
        }
        return 0;
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

    static void EditHosts()
    {
        var text = File.ReadAllText(HostsPath);
        if (!File.Exists(BackupPath)) File.Copy(HostsPath, BackupPath);
        File.WriteAllText(HostsPath, AddBlock(text, RedirectHosts), new UTF8Encoding(false));
        Console.WriteLine("hosts redirected.");
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

    static string AddBlock(string content, IEnumerable<string> hosts)
    {
        var sb = new StringBuilder(RemoveBlock(content).TrimEnd('\r', '\n'));
        sb.Append('\n').Append($"# >>> {Marker} >>>\n");
        foreach (var h in hosts) sb.Append("127.0.0.1 ").Append(h).Append('\n');
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

    static Process StartServer(string baseDir)
    {
        var overridePath = Environment.GetEnvironmentVariable("OPENVERSE_SERVER");
        var exe = Path.Combine(baseDir, "OpenVerse.Api.exe");
        var dll = Path.Combine(baseDir, "OpenVerse.Api.dll");
        ProcessStartInfo psi = overridePath is { } o
            ? (o.EndsWith(".dll") ? new ProcessStartInfo("dotnet", $"\"{o}\"") : new ProcessStartInfo(o))
            : File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet", $"\"{dll}\"");
        psi.Environment["OPENVERSE_LISTEN"] = "1";
        psi.UseShellExecute = false;
        psi.WorkingDirectory = baseDir;
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start server");
    }

    static void WaitForGameExit()
    {
        for (var i = 0; i < 30; i++)
        {
            var p = Process.GetProcessesByName("Shadowverse").FirstOrDefault();
            if (p != null) { p.WaitForExit(); return; }
            Thread.Sleep(1000);
        }
        Console.WriteLine("(game not detected; press Enter to stop)");
        Console.ReadLine();
    }
}
