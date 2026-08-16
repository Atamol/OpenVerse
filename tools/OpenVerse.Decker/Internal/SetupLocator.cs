using System.IO;
using System.Linq;

namespace OpenVerse.Decker.Internal;

/// <summary>
/// Finds openverse-setup.exe and the files it writes, for when the configured paths point nowhere.
///
public static class SetupLocator
{
    public const string SetupExeName = "openverse-setup.exe";
    public const string LauncherExeName = "openverse-launcher.exe";

    /// <summary>Two levels below each root, which reaches server/data from either side of it.</summary>
    private const int DescendDepth = 2;

    private const int MaxDirectories = 400;

    public static IReadOnlyList<string> SearchDirectories()
    {
        var start = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var roots = new List<string> { start };
        if (Directory.GetParent(start)?.FullName is { } parent)
        {
            roots.Add(parent);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var frontier = new Queue<(string Dir, int Depth)>(roots.Select(root => (root, 0)));

        while (frontier.Count > 0 && ordered.Count < MaxDirectories)
        {
            var (dir, depth) = frontier.Dequeue();
            if (!visited.Add(dir))
            {
                continue;
            }
            ordered.Add(dir);

            if (depth < DescendDepth)
            {
                foreach (var child in ChildrenOf(dir))
                {
                    frontier.Enqueue((child, depth + 1));
                }
            }
        }
        return ordered;
    }

    /// <summary>First directory in the search order that holds the file, or null.</summary>
    public static string? FindFile(string fileName, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string[] ChildrenOf(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception)
        {
            // an unreadable folder is simply not part of the search
            return [];
        }
    }
}
