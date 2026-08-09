using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVerse.Common;
using OpenVerse.Decker.Data;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

public partial class DeckEditScreen : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // a drag only counts as add/remove while it stays near horizontal, so a mostly-vertical
    // flick past a tile does not silently edit the deck.
    private const double HorizontalDragToleranceDegrees = 30;

    private readonly CoreWindow _core;
    private readonly InternalDeckBuilder _builder;
    private readonly Deck _deck;
    private readonly List<int> _cardIds;

    // card id -> NormalOrder (Cost -> CardType -> Rarity -> Id
    private readonly Dictionary<int, int> _normalOrderRank;

    private readonly HashSet<FilterChild> _activeFilters = [];
    private readonly List<ToggleButton> _filterButtons = [];
    private readonly List<(ToggleButton Opener, FilterChild[] Children)> _expandableGroups = [];

    // color for activated filter button
    private static readonly SolidColorBrush ActiveFilterFill = Freeze(Color.FromRgb(0xBC, 0xDD, 0xEE));

    // alpha on the fill, not Opacity on the border: Opacity would fade the description text with it
    private static readonly SolidColorBrush HoverPopupFill = Freeze(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush HoverPopupBorder = Freeze(Color.FromArgb(0xDD, 0x80, 0x80, 0x80));

    private readonly DescUserControl _hoverDesc = new(interactive: false);
    private readonly Popup _hoverPopup;
    private UIElement? _hoveredTile;

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Prevent multiple dynamic filter refresh call while typing characters in search text, just call once for a cluster of inputs.
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<CardItem> DeckCards { get; } = [];

    // a plain list swapped wholesale, not an ObservableCollection: rebuilding 5000+ candidates by
    // Clear + per-item Add raises one CollectionChanged each and measured ~4x slower than rebinding.
    public IReadOnlyList<CardItem> CandidateCards { get; private set; } = [];

    // candidates keep their own CardItem alive across filter changes so a copy-count change can be
    // pushed into the visible tile instead of rebuilding thousands of items.
    private readonly Dictionary<int, CardItem> _candidateItems = [];

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

        _searchDebounce.Tick += (_, _) => ApplyFiltersNow();
        CardViewUserControl.Artwork = _builder.Artwork;

        _hoverPopup = BuildHoverPopup(_hoverDesc);
        // a popup is its own top-level window, so leaving this screen would strand it on screen
        Unloaded += (_, _) => CloseHoverPopup();

        DeckGrid.AddHandler(CardViewUserControl.CardLeftClickEvent, new CardEventHandler(Card_LeftClick));
        DeckGrid.AddHandler(CardViewUserControl.CardRightClickEvent, new CardEventHandler(DeckCard_RightClick));
        DeckGrid.AddHandler(CardViewUserControl.CardDragCompletedEvent, new CardEventHandler(Card_DragCompleted));
        CandidateGrid.AddHandler(CardViewUserControl.CardLeftClickEvent, new CardEventHandler(Card_LeftClick));
        CandidateGrid.AddHandler(CardViewUserControl.CardRightClickEvent, new CardEventHandler(CandidateCard_RightClick));
        CandidateGrid.AddHandler(CardViewUserControl.CardDragCompletedEvent, new CardEventHandler(Card_DragCompleted));
        foreach (var grid in new[] { DeckGrid, CandidateGrid })
        {
            grid.AddHandler(CardViewUserControl.CardHoverEnterEvent, new CardEventHandler(Card_HoverEnter));
            grid.AddHandler(CardViewUserControl.CardHoverLeaveEvent, new CardEventHandler(Card_HoverLeave));
        }

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

        // id to duplication count(number of cards)
        var copies = _cardIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        // remove visible ui from deck if its card id doesn't exist
        for (var i = DeckCards.Count - 1; i >= 0; i--)
        {
            if (!copies.ContainsKey(DeckCards[i].CardId))
            {
                DeckCards.RemoveAt(i);
                CloseHoverPopup();
            }
        }

        var row = 0;
        foreach (var cardId in _cardIds.Distinct())
        {
            if (row < DeckCards.Count && DeckCards[row].CardId == cardId)
            {
                // adjust number counter of visible ui in deck if number of its card is changed.
                DeckCards[row].Count = copies[cardId];
            }
            else
            {
                // create new visible if if its card is created newly.
                DeckCards.Insert(row, MakeCardItem(cardId, copies[cardId]));
            }
            row++;
        }

        SyncCandidateCounts();
        OnPropertyChanged(nameof(TotalCardCount));
    }

    private void BuildFilterButtons()
    {
        foreach (var (label, _) in CardFilterCatalog.Costs)
        {
            CostFilterPanel.Children.Add(MakeFilterButton(FilterChild.Cost(label), label));
        }
        foreach (var (label, _) in CardFilterCatalog.Kinds)
        {
            TypeFilterPanel.Children.Add(MakeFilterButton(FilterChild.Kind(label), label));
        }
        foreach (var label in new[] { CardFilterCatalog.UnlimitedLabel, CardFilterCatalog.ResurgentLabel })
        {
            SpecialFilterPanel.Children.Add(MakeFilterButton(FilterChild.Format(label), label));
        }
        foreach (var clanId in CardFilterCatalog.ClanIds)
        {
            ClassFilterPanel.Children.Add(MakeFilterButton(FilterChild.Clan(clanId), I18n.Text($"Clan{clanId}")));
        }
        foreach (var rarity in CardFilterCatalog.Rarities)
        {
            RarityFilterPanel.Children.Add(MakeFilterButton(FilterChild.Rarity(rarity), I18n.Text($"Rarity{rarity}")));
        }

        AddExpandableGroup(I18n.Text("KeywordFilterButton"),
            _builder.Text.Keywords.Select(keyword => (FilterChild.Keyword(keyword), keyword)));
        AddExpandableGroup(I18n.Text("TribeFilterButton"),
            _builder.Stats.AllTribes.Select(tribe => (FilterChild.Tribe(tribe), tribe)));
        // pack labels live in StringResource: the three-letter codes sit in en-us so every language
        // shares them, and only Basic/Prize are translated. A pack newer than the resources falls
        // back to its set id rather than a blank button.
        AddExpandableGroup(I18n.Text("CardSetFilterButton"),
            _builder.CardSets.Packs.Select(setId =>
                (FilterChild.CardSet(setId), I18n.Text($"CardSet{setId}") ?? setId.ToString())));
    }

    /// <summary>
    /// Collapses a long child list behind a single button, which opens a popup holding every
    /// child, so the filter row stays short until the user actually wants the list.
    /// </summary>
    private void AddExpandableGroup(string label, IEnumerable<(FilterChild Child, string Label)> children)
    {
        var list = new WrapPanel { MaxWidth = 640 };
        var childFilters = new List<FilterChild>();
        foreach (var (child, childLabel) in children)
        {
            list.Children.Add(MakeFilterButton(child, childLabel));
            childFilters.Add(child);
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

        var lastDismissed = DateTime.MinValue;
        opener.Checked += (_, _) =>
        {
            if ((DateTime.UtcNow - lastDismissed).TotalMilliseconds < 250)
            {
                opener.IsChecked = false;
                return;
            }
            popup.IsOpen = true;
        };
        opener.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) =>
        {
            lastDismissed = DateTime.UtcNow;
            opener.IsChecked = false;
        };
        closer.Click += (_, _) => opener.IsChecked = false;

        ExpandableFilterPanel.Children.Add(opener);
        _expandableGroups.Add((opener, [.. childFilters]));
    }

    private void RefreshExpandableOpeners()
    {
        foreach (var (opener, children) in _expandableGroups)
        {
            if (children.Any(_activeFilters.Contains))
            {
                opener.Background = ActiveFilterFill;
            }
            else
            {
                opener.ClearValue(BackgroundProperty);
            }
        }
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

        RefreshExpandableOpeners();

        var arguments = new Dictionary<FilterChild, object?> { [FilterChild.SearchText] = SearchBox.Text };
        var filteredIds = _builder.FilterEngine.Apply(_builder.Stats.NormalOrder, active, arguments);

        CandidateCards = [.. filteredIds.Select(CandidateItem)];
        OnPropertyChanged(nameof(CandidateCards));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
        ApplyFiltersNow();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SearchBox.SelectAll();

    /// <summary>
    /// Clicking an unfocused box would place the caret and drop the selection that
    /// <see cref="SearchBox_GotKeyboardFocus"/> just made, so the first click only takes focus.
    /// </summary>
    private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            SearchBox.Focus();
        }
    }

    private void ApplyFiltersNow()
    {
        _searchDebounce.Stop();
        ApplyFilters();
    }

    private CardItem MakeCardItem(int cardId, int count) => new(
        cardId,
        DisplayName(cardId),
        StatsOf(cardId),
        _builder.Stats.Id2Tribes.GetValueOrDefault(cardId, []),
        count);

    private CardItem CandidateItem(int cardId)
    {
        if (!_candidateItems.TryGetValue(cardId, out var item))
        {
            item = MakeCardItem(cardId, CopiesOf(cardId));
            _candidateItems[cardId] = item;
        }
        return item;
    }

    private int CopiesOf(int cardId) => _cardIds.Count(id => id == cardId);

    private void SyncCandidateCounts()
    {
        foreach (var (cardId, item) in _candidateItems)
        {
            item.Count = CopiesOf(cardId);
        }
    }

    /// <summary>
    /// Anchored to the tile rather than the cursor, and never hit-testable: a popup that took the
    /// mouse would raise MouseLeave on the tile under it and flicker itself shut.
    /// </summary>
    private static Popup BuildHoverPopup(DescUserControl desc) => new()
    {
        Placement = PlacementMode.Right,
        AllowsTransparency = true,
        IsHitTestVisible = false,
        Child = new Border
        {
            IsHitTestVisible = false,
            Background = HoverPopupFill,
            BorderBrush = HoverPopupBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Child = desc,
        },
    };

    private void Card_HoverEnter(object sender, CardRoutedEventArgs e)
    {
        // OriginalSource, not Source: an ItemsControl re-sources events coming from its item
        // containers to itself, which would anchor the popup to the whole grid instead of the card
        if (e.OriginalSource is not UIElement tile)
        {
            return;
        }
        _hoveredTile = tile;
        _hoverDesc.ShowCard(_builder.Text, _builder.Stats, e.Card.CardId);
        _hoverPopup.PlacementTarget = tile;
        _hoverPopup.IsOpen = true;
    }

    private void Card_HoverLeave(object sender, CardRoutedEventArgs e)
    {
        if (ReferenceEquals(_hoveredTile, e.OriginalSource))
        {
            CloseHoverPopup();
        }
    }

    private void CloseHoverPopup()
    {
        _hoveredTile = null;
        _hoverPopup.IsOpen = false;
    }

    private void Card_LeftClick(object sender, CardRoutedEventArgs e)
    {
        CloseHoverPopup();
        _core.ShowFocused(new DescUserControl(_builder.Text, _builder.Stats, e.Card.CardId));
    }

    private void DeckCard_RightClick(object sender, CardRoutedEventArgs e) => RemoveCopy(e.Card.CardId);

    private void CandidateCard_RightClick(object sender, CardRoutedEventArgs e) => AddCopy(e.Card.CardId);

    /// <summary>Left adds a copy and right removes one, as long as the drag stayed near horizontal.</summary>
    private void Card_DragCompleted(object sender, CardRoutedEventArgs e)
    {
        var degrees = Math.Abs(Math.Atan2(e.Direction.Y, e.Direction.X) * 180 / Math.PI);
        var pointsRight = degrees <= HorizontalDragToleranceDegrees;
        var pointsLeft = degrees >= 180 - HorizontalDragToleranceDegrees;

        if (pointsLeft)
        {
            AddCopy(e.Card.CardId);
        }
        else if (pointsRight)
        {
            RemoveCopy(e.Card.CardId);
        }
    }

    private void AddCopy(int cardId)
    {
        _cardIds.Add(cardId);
        RefreshDeckCards();
    }

    private void RemoveCopy(int cardId)
    {
        // Remove drops one instance, and a card that is not in the deck is simply left alone
        _cardIds.Remove(cardId);
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
