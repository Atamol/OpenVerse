using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenVerse.Common;
using OpenVerse.Decker.Data;
using OpenVerse.Decker.Internal;

namespace OpenVerse.Decker.View;

public sealed class DeckListEntry(string deckName, bool isAddButton, Deck? deck) : ObservableObject
{
    public string DeckName { get; } = deckName;
    public bool IsAddButton { get; } = isAddButton;
    public Deck? Deck { get; } = deck;
}

public partial class DeckListScreen : UserControl
{
    // clicking a deck button is not implemented from Click event, its own designed click system.
    // this is used to check tolerance of mouse movement while pressing down and up.
    private const double ClickPositionTolerance = 10;

    private readonly CoreWindow _core;
    private readonly InternalDeckBuilder _builder;

    private DeckListEntry? _pressedEntry;
    private Border? _pressedRow;
    private Point _pressPoint;

    public ObservableCollection<DeckListEntry> Decks { get; } = [];

    public DeckListScreen(CoreWindow core, string lang, string userKey) : this(core, BuildDeckBuilder(lang, userKey))
    {
    }

    public DeckListScreen(CoreWindow core, InternalDeckBuilder builder)
    {
        InitializeComponent();
        _core = core;
        _builder = builder;
        DataContext = this;

        if (!_builder.HasUser)
        {
            ErrorText.Text = I18n.Text("DeckListNoUserError");
        }

        RefreshDecks();
    }

    private static InternalDeckBuilder BuildDeckBuilder(string lang, string userKey)
    {
        var text = new TextLoader(AppConfig.Instance.CardNameTextPath, AppConfig.Instance.SkillDescTextPath, lang, AppConfig.Instance.CardMasterCsvPath);
        var stats = new StatsLoader(AppConfig.Instance.CardMasterCsvPath, text.Id2Name.Keys);
        return new InternalDeckBuilder(text, stats, userKey);
    }

    private void RefreshDecks()
    {
        Decks.Clear();
        Decks.Add(new DeckListEntry(I18n.Text("DeckListAddButton"), true, null));
        foreach (var deck in _builder.ListDecks())
        {
            Decks.Add(new DeckListEntry(deck.DeckName, false, deck));
        }
    }

    private void OpenEntry(DeckListEntry entry)
    {
        var deck = entry.IsAddButton ? _builder.NewDeck(classId: 1) : entry.Deck!;
        _core.ShowScreen(new DeckEditScreen(_core, _builder, deck));
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DeckListEntry entry } row)
        {
            return;
        }
        HandlePress(entry, row, e.GetPosition(row));
    }

    private void HandlePress(DeckListEntry entry, Border row, Point pressPoint)
    {
        _pressedEntry = entry;
        _pressedRow = row;
        _pressPoint = pressPoint;
        row.CaptureMouse(); // guarantees this row gets the matching MouseLeftButtonUp regardless of exactly where the cursor ends up
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedRow is not { } row)
        {
            return;
        }
        HandleRelease(e.GetPosition(row));
    }

    private void HandleRelease(Point releasePoint)
    {
        if (_pressedRow is not { } row || _pressedEntry is not { } entry)
        {
            return;
        }

        row.ReleaseMouseCapture();

        var delta = releasePoint - _pressPoint;
        if (Math.Abs(delta.X) <= ClickPositionTolerance && Math.Abs(delta.Y) <= ClickPositionTolerance)
        {
            OpenEntry(entry);
        }

        _pressedRow = null;
        _pressedEntry = null;
    }

    // --- delete -----------------------------------------------------------------------------------
    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeckListEntry entry } || entry.Deck is not { } deck)
        {
            return;
        }

        // Exclude Decks[0] which is add button.
        var index = Decks.IndexOf(entry) - 1;
        var deckNo = deck.DeckNo;
        var classId = deck.ClassId;
        var createdAt = deck.CreatedAt;

        var dialog = new ConfirmDialog(I18n.Text("DeckDeleteConfirmMessage"));
        dialog.Confirmed += () =>
        {
            var freshDecks = _builder.ListDecks();
            if (index >= 0 && index < freshDecks.Count)
            {
                // deckno is not display order, so we don't have to think about deck no changes.
                var atIndex = freshDecks[index];
                if (atIndex.DeckNo == deckNo && atIndex.ClassId == classId && atIndex.CreatedAt == createdAt)
                {
                    _builder.Delete(atIndex);
                }
            }
            RefreshDecks();
            _core.HideFocused();
        };
        dialog.Cancelled += _core.HideFocused;
        _core.ShowFocused(dialog);
    }
}
