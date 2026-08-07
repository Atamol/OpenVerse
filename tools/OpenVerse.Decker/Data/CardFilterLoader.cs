using System.Reflection;
using System.Text.Json;

namespace OpenVerse.Decker.Data;

public sealed class CardFilterLoader
{
    private const string UnlimitedResourceName = "OpenVerse.Decker.Resources.unlimited_cards.json";
    private const string ResurgentResourceName = "OpenVerse.Decker.Resources.resurgent_cards.json";

    /// <summary>
    /// every card_id whose card_set_id is not 90000 (token) in card_master_full.csv
    /// </summary>
    public IReadOnlySet<int> UnlimitedCardIds { get; }

    /// <summary>
    /// every card id which has 1 in column 76 (IsResurgentCard) AND
    /// whose card_set_id is not 90000 (token) in card_master_full.csv
    /// </summary>
    public IReadOnlySet<int> Resurgent { get; }

    public CardFilterLoader()
    {
        UnlimitedCardIds = LoadIdSet(UnlimitedResourceName);
        Resurgent = LoadIdSet(ResurgentResourceName);
    }

    private static HashSet<int> LoadIdSet(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded resource '{resourceName}' not found in {assembly.FullName}");

        var ids = JsonSerializer.Deserialize<int[]>(stream) ?? [];
        return [.. ids];
    }
}
