using System.IO;
using System.Linq;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Decker.Internal;

/// <summary>
/// Keeps decks.json in step with what the decker saves into openverse.db.
///
/// The launcher pushes that file to a host when joining one and pulls it back on exit, but never
/// touches it when hosting - so without this a deck built here reaches a host only if it had
/// already been played on one.
/// </summary>
public sealed class DeckJsonMirror
{
    public const string FileName = "decks.json";

    /// <summary>Null when the launcher could not be located, in which case mirroring is skipped.</summary>
    public string? FilePath { get; }

    public DeckJsonMirror() => FilePath = Locate();

    /// <summary>
    /// The launcher keeps decks.json in its own <c>Layout.Root</c>. That is evaluated against the
    /// launcher's folder, not this one, so the rule is applied here rather than read from Layout.
    /// </summary>
    private static string? Locate()
    {
        if (SetupLocator.FindFile(SetupLocator.LauncherExeName, SetupLocator.SearchDirectories()) is not { } launcher)
        {
            return null;
        }

        var here = Path.GetDirectoryName(launcher)!;
        var up = Directory.GetParent(here)?.FullName;
        var root = up is not null && File.Exists(Path.Combine(up, "build-release.ps1")) ? up : here;
        return Path.Combine(root, FileName);
    }

    public void Save(Deck deck) => Rewrite(decks =>
    {
        var existing = decks.FindIndex(other => IsSameDeck(other, deck));
        if (existing >= 0)
        {
            decks[existing] = deck;
        }
        else
        {
            decks.Add(deck);
        }
    });

    /// <summary>Otherwise the next join would push a deck back that was deleted here.</summary>
    public void Delete(Deck deck) => Rewrite(decks => decks.RemoveAll(other => IsSameDeck(other, deck)));

    /// <summary>
    /// Matched on CreatedAt alone. Don't reply on UserKey because it is depends on server side.
    /// </summary>
    private static bool IsSameDeck(Deck a, Deck b) =>
        a.CreatedAt.ToUniversalTime() == b.CreatedAt.ToUniversalTime();

    private void Rewrite(Action<List<Deck>> change)
    {
        if (FilePath is null)
        {
            return;
        }

        try
        {
            var decks = Read();
            change(decks);
            // the same shape the API writes when the launcher pulls: a flat array, default naming
            File.WriteAllText(FilePath, JsonSerializer.Serialize(decks));
        }
        catch (Exception)
        {
            // openverse.db is the source of truth; a mirror that cannot be written must not take
            // the save down with it
        }
    }

    private List<Deck> Read()
    {
        if (FilePath is null || !File.Exists(FilePath))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<Deck>>(File.ReadAllText(FilePath)) ?? [];
    }
}
