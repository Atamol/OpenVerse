namespace OpenVerse.Common;

// Root is what a person edits or keeps, Server is what the build produces and what the server exes read relative to
// themselves
public static class Layout
{
    public static string Root { get; }
    public static string Server { get; }

    static Layout()
    {
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Server = FindServer(here);
        // The next build deletes the staging copy, so user files and logs go up beside the repo. The build-release.ps1
        // marker keeps that from firing outside the repo
        var up = Directory.GetParent(here)?.FullName;
        Root = up is not null && File.Exists(Path.Combine(up, "build-release.ps1")) ? up : here;
    }

    static string FindServer(string here)
    {
        // nearest first: the staging copy has both server/ and release/server, and must find its own
        foreach (var candidate in new[]
                 {
                     Path.Combine(here, "server"),
                     Path.Combine(here, "release", "server"),
                     Path.Combine(here, "release"),
                     here,
                 })
            if (File.Exists(Path.Combine(candidate, "OpenVerse.Api.exe"))) return candidate;
        return Path.Combine(here, "server");
    }

    public static string InRoot(params string[] parts) => Path.Combine([Root, .. parts]);
    public static string InServer(params string[] parts) => Path.Combine([Server, .. parts]);
}
