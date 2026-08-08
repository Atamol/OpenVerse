using System.Reflection;
using System.Text.Json;

namespace OpenVerse.Decker.Data;

public sealed class CardFilterLoader
{
    private const string UnlimitedResourceName = "OpenVerse.Decker.Resources.unlimited_cards.json";
    private const string ResurgentResourceName = "OpenVerse.Decker.Resources.resurgent_cards.json";

    /// <summary>
    /// every card_id whose card_set_id is not 90000 (token) in card_master_full.csv, minus the
    /// resurgent ones. The two are kept disjoint so the buttons select separate pools, which also
    /// makes the count match the client's Unlimited pool (4032 cards rather than 4221).
    /// </summary>
    public IReadOnlySet<int> UnlimitedCardIds { get; }

    /// <summary>
    /// every card id which has 1 in column 76 (IsResurgentCard) AND
    /// whose card_set_id is not 90000 (token) in card_master_full.csv
    /// </summary>
    public IReadOnlySet<int> Resurgent { get; }

    public CardFilterLoader()
    {
        var resurgent = LoadIdSet(ResurgentResourceName);
        var unlimited = LoadIdSet(UnlimitedResourceName);
        unlimited.ExceptWith(resurgent);

        UnlimitedCardIds = unlimited;
        Resurgent = resurgent;
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
