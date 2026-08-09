using CommunityToolkit.Mvvm.ComponentModel;
using OpenVerse.Decker.Data;

namespace OpenVerse.Decker.View;

/// <summary>
/// One card as the card grid shows it, for both the deck side and the candidate side. The two
/// differ only in whether <see cref="Count"/> is meaningful, so they share one type rather than
/// keeping two near-identical entries in step.
/// </summary>
public sealed class CardItem : ObservableObject
{
    public int CardId { get; }
    public string Name { get; }
    public string TypeAbbreviation { get; }
    public IReadOnlyList<string> Tribes { get; }
    public int Cost { get; }
    public int Power { get; }
    public int Life { get; }
    public int Rarity { get; }

    /// <summary>Power is -1 for everything except followers, and then no stats are drawn.</summary>
    public bool HasStats => Power >= 0;

    /// <summary>Copies in the deck. Always 0 on the candidate side, where it is not shown.</summary>
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
    private int _count;

    public CardItem(int cardId, string name, CardStats stats, IReadOnlyList<string> tribes, int count = 0)
    {
        CardId = cardId;
        Name = name;
        TypeAbbreviation = stats.CardType.Abbreviation();
        Tribes = tribes;
        Cost = stats.Cost;
        Power = stats.Power;
        Life = stats.Life;
        Rarity = stats.Rarity;
        _count = count;
    }
}
