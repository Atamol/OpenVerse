using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenVerse.Common;
using OpenVerse.Decker.Data;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

file static class MissingStats
{
    public static readonly CardStats Value = new(Cost: 0, Power: -1, Life: -1, CardType: default, Rarity: 0);
}

public sealed class DeckCardEntry(int cardId, string cardName, int count, CardStats stats) : ObservableObject
{
    public int CardId { get; } = cardId;
    public string CardName { get; } = cardName;
    public int Cost { get; } = stats.Cost;
    public int Power { get; } = stats.Power;
    public int Life { get; } = stats.Life;
    public string TypeAbbreviation { get; } = stats.CardType.Abbreviation();

    // Power/Life (and the "/" between them) are only meaningful for Followers - StatsLoader gives
    // everything else Power == Life == -1
    public Visibility StatsVisibility { get; } = stats.Power == -1 ? Visibility.Collapsed : Visibility.Visible;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
    private int _count = count;
}

// one row in the right (candidate) list: a card that can be added to the deck.
public sealed class CandidateCardEntry(int cardId, string cardName, CardStats stats)
{
    public int CardId { get; } = cardId;
    public string CardName { get; } = cardName;
    public int Cost { get; } = stats.Cost;
    public int Power { get; } = stats.Power;
    public int Life { get; } = stats.Life;
    public string TypeAbbreviation { get; } = stats.CardType.Abbreviation();
    public Visibility StatsVisibility { get; } = stats.Power == -1 ? Visibility.Collapsed : Visibility.Visible;
}

public partial class DeckEditScreen : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly (string Label, Func<int, bool> Matches)[] CostBuckets =
    [
        ("≦1", cost => cost <= 1),
        ("2", cost => cost == 2),
        ("3", cost => cost == 3),
        ("4", cost => cost == 4),
        ("5", cost => cost == 5),
        ("6", cost => cost == 6),
        ("7", cost => cost == 7),
        ("8", cost => cost == 8),
        ("9", cost => cost == 9),
        ("10≦", cost => cost >= 10),
    ];

    private static readonly (string Label, CardType[] Types)[] TypeBuckets =
    [
        ("Fol", [CardType.Follower]),
        ("Spl", [CardType.Spell]),
        ("Amu", [CardType.CooltimeAmulet, CardType.PermanentAmulet]),
    ];

    private readonly CoreWindow _core;
    private readonly InternalDeckBuilder _builder;
    private readonly Deck _deck;
    private readonly List<int> _cardIds;

    // card id -> its position in _builder.Stats.NormalOrder (Cost -> CardType -> Rarity -> Id, the
    // same order the candidate list uses) - built once since NormalOrder itself never changes for
    // the lifetime of this screen. Backs SortByNormalOrder below, the one place that ordering is
    // actually applied, shared by both lists so they can never disagree on card order.
    private readonly Dictionary<int, int> _normalOrderRank;

    // selected filter-button labels (from CostBuckets/TypeBuckets above) - empty means "no
    // restriction on this dimension", multiple selected means "OR" within that dimension
    private readonly HashSet<string> _selectedCostBuckets = [];
    private readonly HashSet<string> _selectedTypeBuckets = [];

    // Unlimited/Resurgent - each its own standalone on/off toggle (not a multi-select bucket group
    // like cost/type above), backed by CardFilterLoader's embedded card-id allowlists
    private bool _filterUnlimited;
    private bool _filterResurgent;

    public ObservableCollection<DeckCardEntry> DeckCards { get; } = [];
    public ObservableCollection<CandidateCardEntry> CandidateCards { get; } = [];

    public int TotalCardCount => DeckCards.Sum(e => e.Count);

    public DeckEditScreen(CoreWindow core, InternalDeckBuilder builder, Deck deck)
    {
        InitializeComponent();
        _core = core;
        _builder = builder;
        _deck = deck;
        _cardIds = [.. deck.CardIdArray];
        _normalOrderRank = _builder.Stats.NormalOrder
            .Select((cardId, rank) => (cardId, rank))
            .ToDictionary(x => x.cardId, x => x.rank);
        DataContext = this;

        DeckNameBox.Text = deck.DeckName;

        // clan display names are locale-dependent (Resources/StringResource.*.xaml, "Clan1".."Clan8")
        var clanNames = InternalDeckBuilder.ValidClanIds.ToDictionary(id => id, id => I18n.Text($"Clan{id}"));
        ClassIdComboBox.ItemsSource = clanNames;
        ClassIdComboBox.SelectedValue = clanNames.ContainsKey(deck.ClassId)
            ? deck.ClassId
            : InternalDeckBuilder.ValidClanIds[0];

        BuildFilterButtons();
        RefreshDeckCards();
        ApplyFilters();
    }

    private string DisplayName(int cardId) =>
        CardTextMarkup.StripNotation(_builder.Text.Id2Name.GetValueOrDefault(cardId, $"#{cardId}"));

    private CardStats StatsOf(int cardId) => _builder.Stats.Id2UnevolvedStats.GetValueOrDefault(cardId, MissingStats.Value);

    private IEnumerable<int> SortByNormalOrder(IEnumerable<int> ids) =>
        ids.OrderBy(id => _normalOrderRank.GetValueOrDefault(id, int.MaxValue)).ThenBy(id => id);

    private void RefreshDeckCards()
    {
        var sorted = SortByNormalOrder(_cardIds).ToList();
        _cardIds.Clear();
        _cardIds.AddRange(sorted);

        DeckCards.Clear();
        foreach (var group in _cardIds.GroupBy(id => id))
        {
            DeckCards.Add(new DeckCardEntry(group.Key, DisplayName(group.Key), group.Count(), StatsOf(group.Key)));
        }
        OnPropertyChanged(nameof(TotalCardCount));
    }

    private void BuildFilterButtons()
    {
        foreach (var (label, _) in CostBuckets)
        {
            CostFilterPanel.Children.Add(MakeFilterButton(label, CostFilterButton_Click));
        }
        foreach (var (label, _) in TypeBuckets)
        {
            TypeFilterPanel.Children.Add(MakeFilterButton(label, TypeFilterButton_Click));
        }
        SpecialFilterPanel.Children.Add(MakeFilterButton("Unlimited", UnlimitedFilterButton_Click));
        SpecialFilterPanel.Children.Add(MakeFilterButton("Resurgent", ResurgentFilterButton_Click));
    }

    private static ToggleButton MakeFilterButton(string label, RoutedEventHandler handler)
    {
        var button = new ToggleButton { Content = label, Tag = label, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2) };
        button.Click += handler;
        return button;
    }

    private void CostFilterButton_Click(object sender, RoutedEventArgs e) => ToggleBucket(sender, _selectedCostBuckets);
    private void TypeFilterButton_Click(object sender, RoutedEventArgs e) => ToggleBucket(sender, _selectedTypeBuckets);

    private void ToggleBucket(object sender, HashSet<string> selected)
    {
        if (sender is not ToggleButton { Tag: string key } button)
        {
            return;
        }
        if (button.IsChecked == true)
        {
            selected.Add(key);
        }
        else
        {
            selected.Remove(key);
        }
        ApplyFilters();
    }

    private void UnlimitedFilterButton_Click(object sender, RoutedEventArgs e) => ToggleFlag(sender, ref _filterUnlimited);
    private void ResurgentFilterButton_Click(object sender, RoutedEventArgs e) => ToggleFlag(sender, ref _filterResurgent);

    private void ToggleFlag(object sender, ref bool flag)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }
        flag = button.IsChecked == true;
        ApplyFilters();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _selectedCostBuckets.Clear();
        _selectedTypeBuckets.Clear();
        _filterUnlimited = false;
        _filterResurgent = false;
        foreach (var button in CostFilterPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = false;
        }
        foreach (var button in TypeFilterPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = false;
        }
        foreach (var button in SpecialFilterPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = false;
        }
        SearchBox.Text = string.Empty;
        ApplyFilters();
    }

    private Func<int, bool>[] BuildFilters()
    {
        var filters = new List<Func<int, bool>>();

        var terms = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var term in terms)
        {
            filters.Add(id =>
                (_builder.Text.Id2RawFullDesc.TryGetValue(id, out var full) &&
                    full.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (_builder.Text.Id2Name.TryGetValue(id, out var name) &&
                    name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        if (_selectedCostBuckets.Count > 0)
        {
            var active = CostBuckets.Where(b => _selectedCostBuckets.Contains(b.Label)).ToArray();
            filters.Add(id => _builder.Stats.Id2UnevolvedStats.TryGetValue(id, out var s) &&
                active.Any(b => b.Matches(s.Cost)));
        }

        if (_selectedTypeBuckets.Count > 0)
        {
            var activeTypes = TypeBuckets.Where(b => _selectedTypeBuckets.Contains(b.Label))
                .SelectMany(b => b.Types).ToHashSet();
            filters.Add(id => _builder.Stats.Id2CardType.TryGetValue(id, out var t) && activeTypes.Contains(t));
        }

        if (_filterUnlimited)
        {
            filters.Add(id => _builder.Filters.UnlimitedCardIds.Contains(id));
        }

        if (_filterResurgent)
        {
            filters.Add(id => _builder.Filters.Resurgent.Contains(id));
        }

        return filters.ToArray();
    }

    private void ApplyFilters()
    {
        var filters = BuildFilters();
        var filteredIds = new List<int>();

        foreach (var cardId in _builder.Stats.NormalOrder)
        {
            var passesAll = true;
            foreach (var filter in filters)
            {
                if (filter(cardId))
                {
                    continue;
                }
                passesAll = false;
                break;
            }
            if (passesAll)
            {
                filteredIds.Add(cardId);
            }
        }

        CandidateCards.Clear();
        foreach (var cardId in SortByNormalOrder(filteredIds))
        {
            CandidateCards.Add(new CandidateCardEntry(cardId, DisplayName(cardId), StatsOf(cardId)));
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

    private void DeckCardRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeckCardEntry entry })
        {
            return;
        }
        _core.ShowFocused(new DescUserControl(_builder.Text, _builder.Stats, entry.CardId));
    }

    private void DeckCardRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeckCardEntry entry })
        {
            return;
        }

        _cardIds.Remove(entry.CardId); // removes only the first matching instance, i.e. one copy
        RefreshDeckCards();
    }

    private void CandidateRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CandidateCardEntry entry })
        {
            return;
        }
        _core.ShowFocused(new DescUserControl(_builder.Text, _builder.Stats, entry.CardId));
    }

    private void CandidateRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CandidateCardEntry entry })
        {
            return;
        }

        _cardIds.Add(entry.CardId);
        RefreshDeckCards();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _deck.DeckName = DeckNameBox.Text;
        _deck.ClassId = (int)ClassIdComboBox.SelectedValue;
        _deck.CardIdArray = [.. _cardIds];
        _builder.Save(_deck);
        _core.ShowScreen(new DeckListScreen(_core, _builder));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _core.ShowScreen(new DeckListScreen(_core, _builder));
}
