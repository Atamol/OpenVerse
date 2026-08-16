using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using OpenVerse.Decker.Data;

namespace OpenVerse.Decker.View;

/// <summary>
/// Direction is the unit vector of a drag in screen axes, so X&gt;0 points right; it is default
/// for the click events. Icon is the tile's GhostIcon.
/// </summary>
public sealed class CardRoutedEventArgs(RoutedEvent routedEvent, CardItem card, UIElement icon, Vector direction = default)
    : RoutedEventArgs(routedEvent)
{
    public CardItem Card { get; } = card;
    public UIElement Icon { get; } = icon;
    public Vector Direction { get; } = direction;
}

public delegate void CardEventHandler(object sender, CardRoutedEventArgs e);

/// <summary>Hides the copy badge while a card is not in the deck.</summary>
public sealed class PositiveToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
/// <summary>
/// One card tile. ItemFrame owns the hit box and GhostIcon is the part that reacts - it blinks on
/// right click, presses in on left click, and follows the cursor while dragging.
/// </summary>


/// <summary>
/// a card tile which reacts to right click, left click, dragging with left mouse.
/// - ui
///     - background frame: has actual hitbox.
///     - foreground icon(GhostIcon): has visual but no hitbox and reacts to ui interactions
/// 
/// </summary>
public partial class CardViewUserControl : UserControl
{
    /// <summary>
    /// if drag length is less than this then assumes that is click and doesn't render transform transition.
    /// </summary>
    private const double ClickMovementLimit = 6;

    /// <summary>
    /// if drag length is longer than this then assumes that is drag and render transform transition.
    /// </summary>
    private const double DragMovementThreshold = 14;

    /// <summary>
    /// Set once by the screen. Tiles are built by a DataTemplate so there is no constructor to
    /// inject through, and every tile must share the one loader's thread and cache.
    /// </summary>
    public static CardArtworkLoader? Artwork { get; set; }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush UnknownRarityFill = Freeze(0x44, 0x44, 0x4A);

    private static readonly IReadOnlyDictionary<int, SolidColorBrush> RarityFills = new Dictionary<int, SolidColorBrush>
    {
        [1] = Freeze(0x7A, 0x5C, 0x3E), // bronze
        [2] = Freeze(0x8A, 0x91, 0x99), // silver
        [3] = Freeze(0xB3, 0x92, 0x37), // gold
        [4] = Freeze(0x9C, 0x5A, 0x34), // legend(but i gave up rainbow)
    };

    internal enum ReleaseGesture { Ignored, Click, Drag }

    /// <summary>
    /// take mouse releasing as drag or click or ignored operation.
    /// </summary>
    /// <param name="wasDragging"></param>
    /// <param name="travel"></param>
    /// <returns></returns>
    internal static ReleaseGesture ClassifyRelease(bool wasDragging, Vector travel)
    {
        if (wasDragging)
        {
            return ReleaseGesture.Drag;
        }
        return travel.Length <= ClickMovementLimit ? ReleaseGesture.Click : ReleaseGesture.Ignored;
    }

    // routed rather than CLR events: the grid recycles these tiles, so a per-tile subscription
    // would either leak or fire twice. The owner handles all three once, on the grid.
    public static readonly RoutedEvent CardLeftClickEvent = EventManager.RegisterRoutedEvent(
        nameof(CardLeftClick), RoutingStrategy.Bubble, typeof(CardEventHandler), typeof(CardViewUserControl));

    public static readonly RoutedEvent CardRightClickEvent = EventManager.RegisterRoutedEvent(
        nameof(CardRightClick), RoutingStrategy.Bubble, typeof(CardEventHandler), typeof(CardViewUserControl));

    public static readonly RoutedEvent CardDragCompletedEvent = EventManager.RegisterRoutedEvent(
        nameof(CardDragCompleted), RoutingStrategy.Bubble, typeof(CardEventHandler), typeof(CardViewUserControl));

    public static readonly RoutedEvent CardHoverEnterEvent = EventManager.RegisterRoutedEvent(
        nameof(CardHoverEnter), RoutingStrategy.Bubble, typeof(CardEventHandler), typeof(CardViewUserControl));

    public static readonly RoutedEvent CardHoverLeaveEvent = EventManager.RegisterRoutedEvent(
        nameof(CardHoverLeave), RoutingStrategy.Bubble, typeof(CardEventHandler), typeof(CardViewUserControl));

    public event CardEventHandler CardLeftClick
    {
        add => AddHandler(CardLeftClickEvent, value);
        remove => RemoveHandler(CardLeftClickEvent, value);
    }

    public event CardEventHandler CardRightClick
    {
        add => AddHandler(CardRightClickEvent, value);
        remove => RemoveHandler(CardRightClickEvent, value);
    }

    public event CardEventHandler CardDragCompleted
    {
        add => AddHandler(CardDragCompletedEvent, value);
        remove => RemoveHandler(CardDragCompletedEvent, value);
    }

    public event CardEventHandler CardHoverEnter
    {
        add => AddHandler(CardHoverEnterEvent, value);
        remove => RemoveHandler(CardHoverEnterEvent, value);
    }

    public event CardEventHandler CardHoverLeave
    {
        add => AddHandler(CardHoverLeaveEvent, value);
        remove => RemoveHandler(CardHoverLeaveEvent, value);
    }

    private CardItem? _card;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragging;

    public CardViewUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Bind(e.NewValue as CardItem);
    }

    private void Bind(CardItem? card)
    {
        _card = card;
        ResetGhost();
        if (card is null)
        {
            return;
        }

        var tribes = string.Join(" ", card.Tribes);
        var cost = card.Cost.ToString();
        var attack = card.HasStats ? card.Power.ToString() : string.Empty;
        var life = card.HasStats ? card.Life.ToString() : string.Empty;

        NameText.Text = card.Name;
        TypeText.Text = card.TypeAbbreviation;
        TribeText.Text = tribes;
        CostText.Text = cost;
        AttackText.Text = attack;
        LifeText.Text = life;

        ShowArtwork(card);

        // scrolling swaps a recycled tile's card under a stationary cursor and WPF raises no
        // MouseEnter for that, so the tile re-announces itself rather than leaving a stale popup
        if (IsMouseOver)
        {
            RaiseEvent(new CardRoutedEventArgs(CardHoverEnterEvent, card, GhostIcon));
        }
    }

    private void ShowArtwork(CardItem card)
    {
        var fallback = RarityFills.GetValueOrDefault(card.Rarity, UnknownRarityFill);

        if (Artwork is not { IsAvailable: true } loader)
        {
            ArtworkHost.Content = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = fallback };
            return;
        }

        // a recycled tile already holds a view, so re-point it rather than building another
        if (ArtworkHost.Content is CardArtworkView existing)
        {
            loader.Rebind(existing, card.CardId);
        }
        else
        {
            ArtworkHost.Content = loader.CreateView(card.CardId, fallback);
        }
    }

    private void ItemFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressed = true;
        _dragging = false;
        ItemFrame.CaptureMouse();
    }

    private void ItemFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var travel = e.GetPosition(this) - _pressPoint;
        if (!_dragging && travel.Length >= DragMovementThreshold)
        {
            _dragging = true;
        }
        if (_dragging)
        {
            GhostTranslate.X = travel.X;
            GhostTranslate.Y = travel.Y;
        }
    }

    private void ItemFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        _pressed = false;
        ItemFrame.ReleaseMouseCapture();

        var travel = e.GetPosition(this) - _pressPoint;
        var card = _card;
        var gesture = ClassifyRelease(_dragging, travel);
        _dragging = false;

        if (gesture == ReleaseGesture.Drag)
        {
            SnapGhostBack();
            if (card is not null && travel.Length > 0)
            {
                travel.Normalize();
                RaiseEvent(new CardRoutedEventArgs(CardDragCompletedEvent, card, GhostIcon, travel));
            }
        }
        else if (gesture == ReleaseGesture.Click && card is not null)
        {
            PlayPressIn();
            RaiseEvent(new CardRoutedEventArgs(CardLeftClickEvent, card, GhostIcon));
        }
    }

    // the tile itself is the event Source, which is what the owner anchors its popup to - a
    // cursor-relative popup would slide the text around under the reader as they move the mouse
    private void ItemFrame_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_card is { } card)
        {
            RaiseEvent(new CardRoutedEventArgs(CardHoverEnterEvent, card, GhostIcon));
        }
    }

    private void ItemFrame_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_card is { } card)
        {
            RaiseEvent(new CardRoutedEventArgs(CardHoverLeaveEvent, card, GhostIcon));
        }
    }

    private void ItemFrame_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_card is not { } card)
        {
            return;
        }
        PlayBlink();
        RaiseEvent(new CardRoutedEventArgs(CardRightClickEvent, card, GhostIcon));
    }

    // GhostIcon is rendered according to background operations.
    private void ResetGhost()
    {
        GhostTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        GhostTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        GhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        GhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GhostIcon.BeginAnimation(OpacityProperty, null);
        GhostTranslate.X = GhostTranslate.Y = 0;
        GhostScale.ScaleX = GhostScale.ScaleY = 1;
        GhostIcon.Opacity = 1;
    }

    /// <summary>
    /// Animations default to FillBehavior.HoldEnd, which keeps owning the property afterwards and
    /// makes the next drag's direct assignment do nothing - so each one is released on completion.
    /// </summary>
    private static void AnimateThenRelease(
        IAnimatable target, DependencyProperty property, AnimationTimeline animation, double restingValue)
    {
        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, restingValue);
        };
        target.BeginAnimation(property, animation);
    }

    private void SnapGhostBack()
    {
        DoubleAnimation Glide() => new(0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        AnimateThenRelease(GhostTranslate, TranslateTransform.XProperty, Glide(), 0);
        AnimateThenRelease(GhostTranslate, TranslateTransform.YProperty, Glide(), 0);
    }

    private void PlayPressIn()
    {
        DoubleAnimation Press() => new(1, 0.88, TimeSpan.FromMilliseconds(90))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        AnimateThenRelease(GhostScale, ScaleTransform.ScaleXProperty, Press(), 1);
        AnimateThenRelease(GhostScale, ScaleTransform.ScaleYProperty, Press(), 1);
    }

    private void PlayBlink()
    {
        AnimateThenRelease(GhostIcon, OpacityProperty, new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(70))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
        }, 1);
    }
}
