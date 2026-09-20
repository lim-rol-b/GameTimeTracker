using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
namespace GameActivityTracker.Windows.UI;
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly DashboardViewModel _model;
    private int _ticks;
    public MainWindow(DashboardViewModel model)
    {
        InitializeComponent();DataContext=_model=model;
        Loaded+=async (_,_)=>await _model.Refresh();
        _timer.Tick+=async (_,_)=>await OnTick();
        _timer.Start();Closed+=(_,_)=>_timer.Stop();
    }
    private bool _tickBusy;
    private async Task OnTick()
    {
        // Polls run off the UI thread, and a slow refresh must not let ticks stack up.
        if(_tickBusy)return;
        _tickBusy=true;
        try
        {
            await _model.UpdateLiveAsync();
            if(++_ticks%5==0 && IsVisible)await _model.Refresh();
        }
        finally{_tickBusy=false;}
    }
    private void OnToggleSettings(object sender,RoutedEventArgs e) => _model.SelectedTab=_model.SelectedTab==1?0:1;
    private void OnYearWheel(object sender,MouseWheelEventArgs e)
    {
        if(sender is not System.Windows.Controls.ListBox list) return;
        var border=System.Windows.Media.VisualTreeHelper.GetChildrenCount(list)>0 ? System.Windows.Media.VisualTreeHelper.GetChild(list,0) : null;
        var viewer=border is System.Windows.Controls.Decorator decorator ? decorator.Child as System.Windows.Controls.ScrollViewer : border as System.Windows.Controls.ScrollViewer;
        if(viewer is null) return;
        if(e.Delta>0) viewer.LineUp(); else viewer.LineDown();
        e.Handled=true;
    }
    private void OnGameDoubleClick(object sender,MouseButtonEventArgs e)
    {if(_model.OpenGame.CanExecute(null))_model.OpenGame.Execute(null);}
}
