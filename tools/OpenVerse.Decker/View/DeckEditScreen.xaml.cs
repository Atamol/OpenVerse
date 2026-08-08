using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
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

    private static readonly (string Label, CardType[] Types)[] KindBuckets =
    [
        ("Fol", [CardType.Follower]),
        ("Spl", [CardType.Spell]),
        ("Amu", [CardType.CooltimeAmulet, CardType.PermanentAmulet]),
    ];

    private const string UnlimitedLabel = "Unlimited";
    private const string ResurgentLabel = "Resurgent";

    // 0 is Neutral, which ValidClanIds omits because a deck cannot be built on it.
    private static readonly int[] FilterableClanIds = [0, .. InternalDeckBuilder.ValidClanIds];
    private static readonly int[] Rarities = [1, 2, 3, 4];

    // a press that travels no further than this re-opens the card detail; past the action
    // threshold it changes the copy count instead, and the gap between the two is inert so a
    // half-committed drag does nothing.
    private const double DetailDragLimit = 10;
    private const double CountDragThreshold = 40;

    private enum RowGesture { Ignored, ShowDetail, AddCopy, RemoveCopy }

    private readonly CoreWindow _core;
    private readonly InternalDeckBuilder _builder;
    private readonly Deck _deck;
    private readonly List<int> _cardIds;

    // card id -> its position in _builder.Stats.NormalOrder (Cost -> CardType -> Rarity -> Id, the
    // same order the candidate list uses) - built once since NormalOrder itself never changes for
    // the lifetime of this screen.
    private readonly Dictionary<int, int> _normalOrderRank;

    private readonly FilterEngine _filterEngine;
    private readonly HashSet<FilterChild> _activeFilters = [];
    private readonly List<ToggleButton> _filterButtons = [];

    private Point _rowPressPoint;
    private bool _rowPressed;

    // typing re-filters only once input settles: every keystroke would otherwise rebuild the whole
    // candidate list, and WPF raises TextChanged mid-IME-composition too (e.g. while "だめ" is
    // still unconfirmed), so those intermediate results are built and discarded unseen.
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<DeckCardEntry> DeckCards { get; } = [];

    // a plain list swapped wholesale, not an ObservableCollection: rebuilding 5000+ candidates by
    // Clear + per-item Add raises one CollectionChanged each and measured ~4x slower than rebinding.
    public IReadOnlyList<CandidateCardEntry> CandidateCards { get; private set; } = [];

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
        _filterEngine = BuildFilterEngine();
        DataContext = this;

        DeckNameBox.Text = deck.DeckName;

        // clan display names are locale-dependent (Resources/StringResource.*.xaml, "Clan1".."Clan8")
        var clanNames = InternalDeckBuilder.ValidClanIds.ToDictionary(id => id, id => I18n.Text($"Clan{id}"));
        ClassIdComboBox.ItemsSource = clanNames;
        ClassIdComboBox.SelectedValue = clanNames.ContainsKey(deck.ClassId)
            ? deck.ClassId
            : InternalDeckBuilder.ValidClanIds[0];

        _searchDebounce.Tick += (_, _) => ApplyFiltersNow();

        BuildFilterButtons();
        RefreshDeckCards();
        ApplyFilters();
    }

    private string DisplayName(int cardId) =>
        _builder.Text.Id2DisplayName.GetValueOrDefault(cardId, $"#{cardId}");

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

    private FilterEngine BuildFilterEngine()
    {
        var stats = _builder.Stats;
        var order = stats.NormalOrder;
        var engine = new FilterEngine();

        foreach (var (label, matches) in CostBuckets)
        {
            engine.AddStatic(FilterChild.Cost(label), order.Where(id => matches(StatsOf(id).Cost)));
        }
        foreach (var (label, types) in KindBuckets)
        {
            engine.AddStatic(FilterChild.Kind(label), order.Where(id => types.Contains(StatsOf(id).CardType)));
        }
        engine.AddStatic(FilterChild.Format(UnlimitedLabel), _builder.Filters.UnlimitedCardIds);
        engine.AddStatic(FilterChild.Format(ResurgentLabel), _builder.Filters.Resurgent);

        foreach (var clanId in FilterableClanIds)
        {
            engine.AddStatic(FilterChild.Clan(clanId), order.Where(id => StatsOf(id).Clan == clanId));
        }
        foreach (var rarity in Rarities)
        {
            engine.AddStatic(FilterChild.Rarity(rarity), order.Where(id => StatsOf(id).Rarity == rarity));
        }
        foreach (var tribe in stats.AllTribes)
        {
            engine.AddStatic(FilterChild.Tribe(tribe),
                order.Where(id => stats.Id2Tribes.GetValueOrDefault(id, []).Contains(tribe)));
        }
        foreach (var keyword in _builder.Text.Keywords)
        {
            // Id2SearchText is already lowercased, so the needle has to be lowered to match
            var needle = keyword.ToLowerInvariant();
            engine.AddStatic(FilterChild.Keyword(keyword), order.Where(id =>
                _builder.Text.Id2SearchText.GetValueOrDefault(id, string.Empty).Contains(needle, StringComparison.Ordinal)));
        }

        engine.AddDynamic(FilterChild.SearchText, MatchSearchTerms);
        return engine;
    }

    // every whitespace-separated term must match (AND), so it lives inside one dynamic child
    // rather than one child per term, which the engine would OR together instead.
    private IEnumerable<int> MatchSearchTerms(object? argument, IReadOnlyCollection<int> candidates)
    {
        if (argument is not string query)
        {
            return candidates;
        }
        var terms = query.Replace('　', ' ').ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return candidates;
        }

        var id2SearchText = _builder.Text.Id2SearchText;
        return candidates.Where(id =>
            id2SearchText.TryGetValue(id, out var text) &&
            terms.All(term => text.Contains(term, StringComparison.Ordinal)));
    }

    private void BuildFilterButtons()
    {
        foreach (var (label, _) in CostBuckets)
        {
            CostFilterPanel.Children.Add(MakeFilterButton(FilterChild.Cost(label), label));
        }
        foreach (var (label, _) in KindBuckets)
        {
            TypeFilterPanel.Children.Add(MakeFilterButton(FilterChild.Kind(label), label));
        }
        foreach (var label in new[] { UnlimitedLabel, ResurgentLabel })
        {
            SpecialFilterPanel.Children.Add(MakeFilterButton(FilterChild.Format(label), label));
        }
        foreach (var clanId in FilterableClanIds)
        {
            ClassFilterPanel.Children.Add(MakeFilterButton(FilterChild.Clan(clanId), I18n.Text($"Clan{clanId}")));
        }
        foreach (var rarity in Rarities)
        {
            RarityFilterPanel.Children.Add(MakeFilterButton(FilterChild.Rarity(rarity), I18n.Text($"Rarity{rarity}")));
        }

        AddExpandableGroup(I18n.Text("KeywordFilterButton"),
            _builder.Text.Keywords.Select(keyword => (FilterChild.Keyword(keyword), keyword)));
        AddExpandableGroup(I18n.Text("TribeFilterButton"),
            _builder.Stats.AllTribes.Select(tribe => (FilterChild.Tribe(tribe), tribe)));
    }

    /// <summary>
    /// Collapses a long child list behind a single button, which opens a popup holding every
    /// child, so the filter row stays short until the user actually wants the list.
    /// </summary>
    private void AddExpandableGroup(string label, IEnumerable<(FilterChild Child, string Label)> children)
    {
        var list = new WrapPanel { MaxWidth = 640 };
        foreach (var (child, childLabel) in children)
        {
            list.Children.Add(MakeFilterButton(child, childLabel));
        }

        var opener = new ToggleButton
        {
            Content = label,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(8, 2, 8, 2),
        };
        var closer = new Button
        {
            Content = "▲ " + label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 3, 0, 3),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var body = new StackPanel();
        body.Children.Add(closer);
        body.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var popup = new Popup
        {
            PlacementTarget = opener,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = SystemColors.WindowBrush,
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = body,
            },
        };

        opener.Checked += (_, _) => popup.IsOpen = true;
        opener.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => opener.IsChecked = false;
        closer.Click += (_, _) => opener.IsChecked = false;

        ExpandableFilterPanel.Children.Add(opener);
    }

    private ToggleButton MakeFilterButton(FilterChild child, string label)
    {
        var button = new ToggleButton
        {
            Content = label,
            Tag = child,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(8, 2, 8, 2),
        };
        button.Click += FilterButton_Click;
        _filterButtons.Add(button);
        return button;
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: FilterChild child } button)
        {
            return;
        }
        if (button.IsChecked == true)
        {
            _activeFilters.Add(child);
        }
        else
        {
            _activeFilters.Remove(child);
        }
        ApplyFiltersNow();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _activeFilters.Clear();
        foreach (var button in _filterButtons)
        {
            button.IsChecked = false;
        }
        SearchBox.Text = string.Empty;
        ApplyFiltersNow();
    }

    private void ApplyFilters()
    {
        var active = new HashSet<FilterChild>(_activeFilters);
        if (SearchBox.Text.Length > 0)
        {
            active.Add(FilterChild.SearchText);
        }

        var arguments = new Dictionary<FilterChild, object?> { [FilterChild.SearchText] = SearchBox.Text };
        var filteredIds = _filterEngine.Apply(_builder.Stats.NormalOrder, active, arguments);

        CandidateCards = [.. filteredIds.Select(id => new CandidateCardEntry(id, DisplayName(id), StatsOf(id)))];
        OnPropertyChanged(nameof(CandidateCards));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Filters straight away and drops any keystroke still waiting on the debounce, so a click
    /// never gets re-applied a moment later by a timer the user has already moved past.
    /// </summary>
    private void ApplyFiltersNow()
    {
        _searchDebounce.Stop();
        ApplyFilters();
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowPressPoint = e.GetPosition(this);
        _rowPressed = true;
    }

    /// <summary>
    /// Reads one press-drag-release as a gesture: a near-stationary release opens the card detail,
    /// a clear horizontal drag changes the copy count (left adds, right removes), and anything in
    /// between is discarded so an unsure drag cannot silently edit the deck.
    /// </summary>
    private RowGesture ClassifyGesture(Point releasePoint)
    {
        if (!_rowPressed)
        {
            return RowGesture.Ignored;
        }
        _rowPressed = false;

        var travel = releasePoint - _rowPressPoint;
        if (travel.Length <= DetailDragLimit)
        {
            return RowGesture.ShowDetail;
        }
        if (Math.Abs(travel.X) < CountDragThreshold)
        {
            return RowGesture.Ignored;
        }
        return travel.X < 0 ? RowGesture.AddCopy : RowGesture.RemoveCopy;
    }

    private void ShowCardDetail(int cardId) =>
        _core.ShowFocused(new DescUserControl(_builder.Text, _builder.Stats, cardId));

    private void AddCopy(int cardId)
    {
        _cardIds.Add(cardId);
        RefreshDeckCards();
    }

    private void RemoveCopy(int cardId)
    {
        _cardIds.Remove(cardId);   // removes one copy, not every copy
        RefreshDeckCards();
    }

    private void ApplyRowGesture(RowGesture gesture, int cardId)
    {
        switch (gesture)
        {
            case RowGesture.ShowDetail:
                ShowCardDetail(cardId);
                break;
            case RowGesture.AddCopy:
                AddCopy(cardId);
                break;
            case RowGesture.RemoveCopy:
                RemoveCopy(cardId);
                break;
        }
    }

    private void DeckCardRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckCardEntry entry })
        {
            ApplyRowGesture(ClassifyGesture(e.GetPosition(this)), entry.CardId);
        }
    }

    private void DeckCardRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckCardEntry entry })
        {
            RemoveCopy(entry.CardId);
        }
    }

    private void CandidateRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CandidateCardEntry entry })
        {
            ApplyRowGesture(ClassifyGesture(e.GetPosition(this)), entry.CardId);
        }
    }

    private void CandidateRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CandidateCardEntry entry })
        {
            AddCopy(entry.CardId);
        }
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
