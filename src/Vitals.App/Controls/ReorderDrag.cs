using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vitals.App.Controls;

/// <summary>
/// Minimal drag-to-reorder for a Panel's children. Press a registered handle, drag, and the item
/// live-reorders as the pointer crosses its siblings' centres (which gives natural hysteresis, so
/// nothing oscillates at the boundaries). Mouse capture goes to the panel, which never leaves the
/// visual tree, so shuffling children mid-drag is safe. Works for a vertical stack and, in
/// reading-order mode, for a wrapping row of chips.
/// </summary>
public sealed class ReorderDrag
{
    private readonly Panel _panel;
    private readonly bool _readingOrder;
    private readonly Action _onDropped;
    private FrameworkElement? _item;
    private Brush? _itemBorder;
    private Point _start;
    private bool _moved;
    private ScrollViewer? _scroll;
    private bool _scrollSearched;

    private ReorderDrag(Panel panel, bool readingOrder, Action onDropped)
    {
        _panel = panel;
        _readingOrder = readingOrder;
        _onDropped = onDropped;
        panel.PreviewMouseMove += OnMove;
        panel.PreviewMouseLeftButtonUp += OnUp;
        panel.LostMouseCapture += (_, _) => Finish();
    }

    /// <param name="readingOrder">true for wrapping rows (compare left-to-right, then top-to-bottom); false for a vertical list.</param>
    public static ReorderDrag For(Panel panel, bool readingOrder, Action onDropped) => new(panel, readingOrder, onDropped);

    /// <summary>Make <paramref name="handle"/> (inside <paramref name="item"/>, a direct child of the panel) start a drag of that item.</summary>
    public void Handle(FrameworkElement handle, FrameworkElement item)
    {
        handle.Cursor = Cursors.SizeAll;
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_item != null) return;
            _item = item;
            _start = e.GetPosition(_panel);
            _moved = false;
            _panel.CaptureMouse();
            e.Handled = true;
        };
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_item == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _panel.ReleaseMouseCapture();
            return;
        }

        var p = e.GetPosition(_panel);
        if (!_moved)
        {
            if (Math.Abs(p.X - _start.X) < 4 && Math.Abs(p.Y - _start.Y) < 4) return;
            _moved = true;
            _item.Opacity = 0.55;
            if (_item is Border b) { _itemBorder = b.BorderBrush; b.BorderBrush = UiBrushes.Accent; }
        }

        AutoScroll(e);

        int current = _panel.Children.IndexOf(_item);
        if (current < 0) return;
        int target = TargetIndex(p);
        if (target != current)
        {
            _panel.Children.RemoveAt(current);
            _panel.Children.Insert(target, _item);
            _panel.UpdateLayout();
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_item == null) return;
        _panel.ReleaseMouseCapture(); // raises LostMouseCapture -> Finish
        Finish();
        e.Handled = true;
    }

    private void Finish()
    {
        if (_item == null) return;
        _item.Opacity = 1;
        if (_item is Border b && _itemBorder != null) b.BorderBrush = _itemBorder;
        bool moved = _moved;
        _item = null;
        _itemBorder = null;
        _moved = false;
        if (moved) _onDropped();
    }

    /// <summary>Number of siblings the pointer has passed, i.e. the slot the dragged item belongs in.</summary>
    private int TargetIndex(Point p)
    {
        int before = 0;
        foreach (FrameworkElement sib in _panel.Children)
        {
            if (ReferenceEquals(sib, _item)) continue;
            var tl = sib.TranslatePoint(new Point(0, 0), _panel);
            double cx = tl.X + sib.ActualWidth / 2;
            double cy = tl.Y + sib.ActualHeight / 2;
            bool isBefore = _readingOrder
                ? tl.Y + sib.ActualHeight <= p.Y                                   // whole row above the pointer
                  || (p.Y >= tl.Y && p.Y < tl.Y + sib.ActualHeight && cx < p.X)    // same row, left of the pointer
                : cy < p.Y;
            if (isBefore) before++;
        }
        return before;
    }

    private void AutoScroll(MouseEventArgs e)
    {
        if (!_scrollSearched)
        {
            _scrollSearched = true;
            DependencyObject? d = _panel;
            while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
            _scroll = d as ScrollViewer;
        }
        if (_scroll == null) return;
        double y = e.GetPosition(_scroll).Y;
        if (y < 28) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 10);
        else if (y > _scroll.ActualHeight - 28) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 10);
    }
}
