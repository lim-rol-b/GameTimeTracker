using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GameActivityTracker.Windows.UI;
public partial class HeatmapView : UserControl
{
    private Point? _pressedAt;
    private double _initialOffset;
    private bool _dragging;
    private Cursor? _previousCursor;
    public HeatmapView()
    {
        InitializeComponent();
        Unloaded += (_, _) => EndDrag();
    }
    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (InsideScrollBar(e.OriginalSource as DependencyObject)) return;
        _pressedAt = e.GetPosition(CalendarScroller);
        _initialOffset = CalendarScroller.HorizontalOffset;
        // Do not capture yet: a regular button click must still open its date detail.
    }
    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } origin) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var delta = e.GetPosition(CalendarScroller).X - origin.X;
        if (!_dragging && Math.Abs(delta) >= SystemParameters.MinimumHorizontalDragDistance && CalendarScroller.ScrollableWidth > 0)
        {
            _dragging = true;
            _previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.SizeWE;
            Mouse.Capture(CalendarScroller, CaptureMode.SubTree);
        }
        if (!_dragging) return;
        CalendarScroller.ScrollToHorizontalOffset(_initialOffset - delta);
        e.Handled = true;
    }
    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true; // Drag release must not execute the day's command.
        EndDrag();
    }
    private void OnCaptureLost(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == CalendarScroller) EndDrag();
    }
    private void EndDrag()
    {
        _pressedAt = null;
        if (_dragging) { _dragging = false; Mouse.OverrideCursor = _previousCursor; }
        if (Mouse.Captured == CalendarScroller) Mouse.Capture(null);
    }
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (CalendarScroller.ScrollableWidth > 0)
                CalendarScroller.ScrollToHorizontalOffset(CalendarScroller.HorizontalOffset - e.Delta);
            return;
        }
        // A horizontal-only ScrollViewer otherwise consumes vertical wheel events before
        // the containing page can see them. Forward once to the vertical parent instead.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(CalendarScroller); parent is not null;
             parent = parent is Visual ? VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer viewer || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                continue;
            viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent
            });
            return;
        }
    }
    private static bool InsideScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
