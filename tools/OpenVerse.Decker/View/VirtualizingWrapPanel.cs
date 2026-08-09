using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private int _firstRow;
    private double _wheelNotches;

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

    /// <summary>
    /// Drops the containers the generator just discarded. Without this the panel keeps them in
    /// InternalChildren, its indices drift out of step with the generator, and realizing the next
    /// range tries to insert a container that is already a child.
    /// </summary>
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0)
                {
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }
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

    private int FirstVisibleIndex => _firstRow * _columns;

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
                // a container off the recycle queue reports isNewlyRealized false yet is no longer
                // in the panel, so what matters is whether it already sits at this slot
                var child = (UIElement)generator.GenerateNext(out _);
                if (childIndex >= InternalChildren.Count)
                {
                    AddInternalChild(child);
                    generator.PrepareItemContainer(child);
                }
                else if (!ReferenceEquals(InternalChildren[childIndex], child))
                {
                    InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUpOutside(first, last);
    }

    /// <summary>
    /// Recycles the containers that scrolled away. Remove would discard them instead, and every
    /// row crossed would then have to build a whole row of tiles from scratch.
    /// </summary>
    private void CleanUpOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var recycler = (IRecyclingItemContainerGenerator)generator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            {
                recycler.Recycle(position, 1);
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

    /// <summary>
    /// The scroll position is the first visible row, and the pixel offset is always derived from
    /// it. Carrying the pixel offset instead lets one clamp against a shrunken extent knock it off
    /// a row boundary, and every later scroll inherits that, clipping the top row.
    /// </summary>
    private int MaxFirstRow =>
        Math.Max(0, (int)Math.Ceiling(Math.Max(0, _extent.Height - _viewport.Height) / ItemHeight));

    private void ScrollToRow(int row)
    {
        _firstRow = Math.Clamp(row, 0, MaxFirstRow);
        _offset.Y = _firstRow * ItemHeight;
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        ScrollToRow(_firstRow);

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        var row = Math.Clamp((int)Math.Round(offset / ItemHeight), 0, MaxFirstRow);
        if (row == _firstRow)
        {
            return;
        }
        ScrollToRow(row);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
        // items always wrap to the viewport width, so there is nothing to scroll sideways to
    }

    // everything moves in whole rows now: a sub-row step would round back to the row it started on
    public void LineUp() => ScrollByRows(-1);
    public void LineDown() => ScrollByRows(1);
    public void PageUp() => ScrollByRows(-RowsPerPage);
    public void PageDown() => ScrollByRows(RowsPerPage);

    private int RowsPerPage => Math.Max(1, (int)(_viewport.Height / ItemHeight));

    private void ScrollByRows(int rows) => SetVerticalOffset((_firstRow + rows) * ItemHeight);
    // one notch moves exactly one card
    public void MouseWheelUp() => ScrollByRows(-1);
    public void MouseWheelDown() => ScrollByRows(1);

    /// <summary>
    /// Moves by however many notches the event actually carries. ScrollViewer reads only the sign
    /// of Delta, so a single event that merged several notches - which is exactly what arrives
    /// while the UI thread is busy - would move one row and silently drop the rest.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // a high resolution wheel sends fractions of a notch; carrying the remainder keeps every
        // stop on a row boundary without throwing away the part that did not fill a row yet
        _wheelNotches += e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var rows = (int)_wheelNotches;
        _wheelNotches -= rows;

        if (rows != 0)
        {
            ScrollByRows(-rows);
        }
        e.Handled = true;
    }

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
