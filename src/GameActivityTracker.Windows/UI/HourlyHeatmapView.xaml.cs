using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameActivityTracker.Windows.UI;
public partial class HourlyHeatmapView : UserControl
{
    public HourlyHeatmapView() => InitializeComponent();
    private void OnHourEnter(object sender, MouseEventArgs e) => Select(sender);
    private void OnHourFocus(object sender, KeyboardFocusChangedEventArgs e) => Select(sender);
    private void OnHourClick(object sender, RoutedEventArgs e) => Select(sender);
    private void Select(object sender)
    {
        if (sender is not FrameworkElement { DataContext: HourTile hour } || DataContext is not HourlyHeatmapViewModel model) return;
        model.SelectedHour = hour;
    }
    private void OnLeave(object sender, MouseEventArgs e)
    {
        // Retain the selected hour while the pointer is over its card so records can be scrolled.
        if (DataContext is HourlyHeatmapViewModel model && !IsKeyboardFocusWithin) model.SelectedHour = null;
    }
}
