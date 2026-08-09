using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OpenVerse.Decker.View;

/// <summary>
/// Fills fixed-size items left-to-right then top-to-bottom, realising only the rows on screen.
/// WPF has no equivalent of UWP's ItemsWrapGrid, and a plain WrapPanel would build all several
/// thousand card items at once; fixed item size is what makes the visible range cheap to compute.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int _columns = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        // ItemContainerGenerator stays null until InternalChildren is touched at least once
        _ = InternalChildren;

        var itemCount = ItemCount;
        _columns = Math.Max(1, (int)Math.Floor(availableSize.Width / ItemWidth));
        var rows = (int)Math.Ceiling(itemCount / (double)_columns);

        UpdateScrollInfo(availableSize, new Size(_columns * ItemWidth, rows * ItemHeight));
        RealizeRange(FirstVisibleIndex, LastVisibleIndex(itemCount), itemCount);

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemCount = ItemCount;
        var first = FirstVisibleIndex;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = first + i;
            var row = index / _columns;
            var column = index % _columns;
            InternalChildren[i].Arrange(new Rect(
                column * ItemWidth - _offset.X,
                row * ItemHeight - _offset.Y,
                ItemWidth,
                ItemHeight));
        }
        return finalSize;
    }

    private int ItemCount =>
        ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    private int FirstVisibleIndex =>
        Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight)) * _columns;

    private int LastVisibleIndex(int itemCount)
    {
        var lastRow = (int)Math.Floor((_offset.Y + _viewport.Height) / ItemHeight);
        return Math.Min(itemCount - 1, ((lastRow + 1) * _columns) - 1);
    }

    /// <summary>
    /// Generates the containers for the visible range and drops every other one, which is what
    /// keeps memory flat no matter how many cards the filter leaves in the list.
    /// </summary>
    private void RealizeRange(int first, int last, int itemCount)
    {
        if (itemCount == 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
            return;
        }

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(first);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNewlyRealized);
                if (isNewlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUpOutside(first, last);
    }

    private void CleanUpOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private Size _extent;
    private Size _viewport;
    private Vector _offset;

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, _extent.Height - _viewport.Height));

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(clamped - _offset.Y) < 0.01)
        {
            return;
        }
        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
        // items always wrap to the viewport width, so there is nothing to scroll sideways to
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight / 3);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight / 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight / 2);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
