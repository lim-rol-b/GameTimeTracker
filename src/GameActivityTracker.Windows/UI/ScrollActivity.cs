using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameActivityTracker.Windows.UI;

/// <summary>Shared transient scrollbar state for normal viewers and controls with internal viewers.</summary>
public static class ScrollActivity
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyPropertyKey IsActivePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsActive", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false));
    public static readonly DependencyProperty IsActiveProperty = IsActivePropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey IsExpandedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsExpanded", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false));
    public static readonly DependencyProperty IsExpandedProperty = IsExpandedPropertyKey.DependencyProperty;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ScrollActivity), new PropertyMetadata(null));

    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollBar bar) return;
        if ((bool)e.NewValue)
        {
            bar.Loaded += OnLoaded;
            bar.Unloaded += OnUnloaded;
            if (bar.IsLoaded) Attach(bar);
        }
        else
        {
            bar.Loaded -= OnLoaded;
            bar.Unloaded -= OnUnloaded;
            Detach(bar);
        }
    }
    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((ScrollBar)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((ScrollBar)sender);
    private static void Attach(ScrollBar bar)
    {
        if (bar.GetValue(StateProperty) is State) return;
        bar.ApplyTemplate();
        var viewer = Ancestor<ScrollViewer>(bar);
        var track = bar.Template?.FindName("PART_Track", bar) as Track;
        if (viewer is not null && track?.Thumb is { } thumb)
            bar.SetValue(StateProperty, new State(bar, viewer, thumb));
    }
    private static void Detach(ScrollBar bar)
    {
        (bar.GetValue(StateProperty) as State)?.Dispose();
        bar.ClearValue(StateProperty);
    }
    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T result) return result;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private sealed class State : IDisposable
    {
        private readonly ScrollBar _bar;
        private readonly ScrollViewer _viewer;
        private readonly Thumb _thumb;
        private readonly DispatcherTimer _idle;
        private readonly MouseWheelEventHandler _wheelHandler;
        public State(ScrollBar bar, ScrollViewer viewer, Thumb thumb)
        {
            _bar = bar;
            _viewer = viewer;
            _thumb = thumb;
            _idle = new DispatcherTimer(DispatcherPriority.Background, bar.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1200) };
            _idle.Tick += OnIdle;
            _wheelHandler = OnWheel;
            viewer.AddHandler(UIElement.PreviewMouseWheelEvent, _wheelHandler, true);
            viewer.ScrollChanged += OnScroll;
            thumb.MouseEnter += OnThumbEnter;
            thumb.MouseLeave += OnThumbLeave;
            thumb.DragStarted += OnDragStarted;
            thumb.DragCompleted += OnDragCompleted;
        }
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // Plain wheel input over the annual calendar is forwarded to the page vertically.
            if (_viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            // Do not light up outer scrollbars simply because an inner panel receives a wheel event.
            if (e.Delta != 0 && Ancestor<ScrollViewer>(e.OriginalSource as DependencyObject) == _viewer) Show();
        }
        private void OnScroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource == _viewer && (e.HorizontalChange != 0 || e.VerticalChange != 0)) Show();
        }
        private void Show()
        {
            if (!_bar.IsVisible) return;
            _bar.SetValue(IsActivePropertyKey, true);
            _idle.Stop();
            if (!GetIsExpanded(_bar)) _idle.Start();
        }
        private void SetExpanded(bool expanded)
        {
            _bar.SetValue(IsExpandedPropertyKey, expanded);
            Show();
        }
        private void OnThumbEnter(object sender, MouseEventArgs e) => SetExpanded(true);
        private void OnThumbLeave(object sender, MouseEventArgs e)
        {
            if (!_thumb.IsDragging) SetExpanded(false);
        }
        private void OnDragStarted(object sender, DragStartedEventArgs e) => SetExpanded(true);
        private void OnDragCompleted(object sender, DragCompletedEventArgs e) => SetExpanded(_thumb.IsMouseOver);
        private void OnIdle(object? sender, EventArgs e)
        {
            _idle.Stop();
            if (!_thumb.IsDragging && !_thumb.IsMouseOver) _bar.SetValue(IsActivePropertyKey, false);
        }
        public void Dispose()
        {
            _idle.Stop();
            _idle.Tick -= OnIdle;
            _viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, _wheelHandler);
            _viewer.ScrollChanged -= OnScroll;
            _thumb.MouseEnter -= OnThumbEnter;
            _thumb.MouseLeave -= OnThumbLeave;
            _thumb.DragStarted -= OnDragStarted;
            _thumb.DragCompleted -= OnDragCompleted;
            _bar.SetValue(IsExpandedPropertyKey, false);
            _bar.SetValue(IsActivePropertyKey, false);
        }
    }
}
