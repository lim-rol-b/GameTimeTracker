namespace GameActivityTracker.Windows.UI;
public partial class DetailWindow : System.Windows.Window
{
    public DetailWindow(DetailViewModel model){InitializeComponent();DataContext=model;}
}
