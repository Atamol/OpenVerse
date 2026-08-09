using System.Linq;

namespace OpenVerse.Decker.Data;

/// <summary>
/// The card packs, bucketed the way the client does (Wizard.CardSetNameMgr): the basic set, the
/// standard packs, and every prize set as one entry - the client gives them all the same name.
/// Labels are a view concern, see the "CardSet&lt;setId&gt;" keys in Resources/StringResource.
/// </summary>
public sealed class CardSetNames
{
    private const int BasicSetId = 10000;
    private const int MinStandardPackId = 10001, MaxStandardPackId = 14999;
    private const int MinPrizeSetId = 70000, MaxPrizeSetId = 79999;

    /// <summary>Stands for all the prize sets at once; no real card carries it.</summary>
    public const int PrizeBucketId = MinPrizeSetId;

    public static bool IsPrize(int cardSetId) => cardSetId is >= MinPrizeSetId and <= MaxPrizeSetId;

    /// <summary>Which filter button a card's set belongs under.</summary>
    public static int BucketOf(int cardSetId) => IsPrize(cardSetId) ? PrizeBucketId : cardSetId;

    private static bool IsBasicOrStandard(int cardSetId) =>
        cardSetId == BasicSetId || cardSetId is >= MinStandardPackId and <= MaxStandardPackId;

    /// <summary>
    /// Basic, then the standard packs in release order, then prize. Only packs that some card
    /// actually belongs to, so an older card_master does not grow empty buttons.
    /// </summary>
    public IReadOnlyList<int> Packs { get; }

    public CardSetNames(IEnumerable<int> cardSetIdsInUse)
    {
        var inUse = cardSetIdsInUse.ToHashSet();
        var packs = inUse.Where(IsBasicOrStandard).OrderBy(setId => setId).ToList();
        if (inUse.Any(IsPrize))
        {
            packs.Add(PrizeBucketId);
        }
        Packs = packs;
    }
}
