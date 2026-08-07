using System.IO;
using System.IO.Compression;
using System.Linq;
using OpenVerse.Common;

namespace OpenVerse.Decker.Data;

public enum CardType
{
    Follower = 1,
    CooltimeAmulet = 2, // countdown amulet
    PermanentAmulet = 3,
    Spell = 4,
}

public static class CardTypeExtensions
{
    public static string Abbreviation(this CardType type) => type switch
    {
        CardType.Follower => "Fol",
        CardType.Spell => "Spl",
        CardType.CooltimeAmulet or CardType.PermanentAmulet => "Amu",
        _ => "?",
    };
}

public sealed record CardStats(int Cost, int Power, int Life, CardType CardType, int Rarity);

public sealed class StatsLoader
{
    private const int CardIdCol = 0, CharTypeCol = 6, CostCol = 10, AtkCol = 11, LifeCol = 12,
        EvoAtkCol = 13, EvoLifeCol = 14, RarityCol = 16;

    // card id -> unevolved-state stats
    public IReadOnlyDictionary<int, CardStats> Id2UnevolvedStats { get; }

    // card id -> evolved-state stats
    public IReadOnlyDictionary<int, CardStats> Id2EvolvedStats { get; }

    // card id to card type
    public IReadOnlyDictionary<int, CardType> Id2CardType { get; }

    // default card display order with these priorities : cost -> type -> rarity -> card id.
    public IReadOnlyList<int> NormalOrder { get; }

    public StatsLoader(string cardMasterCsvGzPath, IEnumerable<int> cardIds)
    {
        var (unevolved, evolved, types) = ParseCardMaster(cardMasterCsvGzPath);

        Id2UnevolvedStats = unevolved;
        Id2EvolvedStats = evolved;
        Id2CardType = types;

        var typeOrder = new Dictionary<CardType, int>
        {
            [CardType.Follower] = 0,
            [CardType.Spell] = 1,
            [CardType.CooltimeAmulet] = 2,
            [CardType.PermanentAmulet] = 2,
        };
        NormalOrder = cardIds
            .Where(unevolved.ContainsKey)
            .OrderBy(id => unevolved[id].Cost)
            .ThenBy(id => typeOrder.GetValueOrDefault(unevolved[id].CardType, 99))
            .ThenBy(id => unevolved[id].Rarity)
            .ThenBy(id => id)
            .ToArray();
    }

    public static IReadOnlyDictionary<int, CardStats> LoadUnevolvedStats(string cardMasterCsvGzPath) =>
        ParseCardMaster(cardMasterCsvGzPath).Unevolved;

    private static (Dictionary<int, CardStats> Unevolved, Dictionary<int, CardStats> Evolved, Dictionary<int, CardType> Types)
        ParseCardMaster(string cardMasterCsvGzPath)
    {
        if (!File.Exists(cardMasterCsvGzPath))
        {
            throw new FileNotFoundException("card_master_full.csv.gz not found", cardMasterCsvGzPath);
        }

        var unevolved = new Dictionary<int, CardStats>();
        var evolved = new Dictionary<int, CardStats>();
        var types = new Dictionary<int, CardType>();

        using (var fs = File.OpenRead(cardMasterCsvGzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var sr = new StreamReader(gz))
        {
            while (sr.ReadLine() is { } line)
            {
                var f = BaseCardIdMap.SplitCsv(line);
                if (f.Count <= RarityCol)
                {
                    continue;
                }
                if (!int.TryParse(f[CardIdCol], out var id))
                {
                    continue;
                }
                if (!int.TryParse(f[CharTypeCol], out var charTypeValue) || !Enum.IsDefined(typeof(CardType), charTypeValue))
                {
                    continue; // CLASS(0)/EVOLUTION(5)/etc. - not a real card_master card row
                }

                var cardType = (CardType)charTypeValue;
                if (!int.TryParse(f[CostCol], out var cost) || cost < 0)
                {
                    continue; // some rows (leader "技巧" abilities etc.) carry a -1/-99 cost
                              // sentinel instead of a real one - not real deck-buildable cards,
                              // same reasoning/precedent as OpenVerse.Common.CardCostMap
                }

                int.TryParse(f[RarityCol], out var rarity);

                int atk, life, evoAtk, evoLife;
                if (cardType == CardType.Follower)
                {
                    int.TryParse(f[AtkCol], out atk);
                    int.TryParse(f[LifeCol], out life);
                    int.TryParse(f[EvoAtkCol], out evoAtk);
                    int.TryParse(f[EvoLifeCol], out evoLife);
                }
                else
                {
                    atk = life = evoAtk = evoLife = -1;
                }

                unevolved[id] = new CardStats(cost, atk, life, cardType, rarity);
                evolved[id] = new CardStats(cost, evoAtk, evoLife, cardType, rarity);
                types[id] = cardType;
            }
        }

        return (unevolved, evolved, types);
    }
}
