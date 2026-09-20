using System.Windows;

namespace GameActivityTracker.Windows.UI;

public partial class SteamLibraryImportWindow : Window
{
    private readonly SteamLibraryImportViewModel _model;
    public IReadOnlyList<SteamGameImportSelection> SelectedGames { get; private set; } = [];
    public SteamLibraryImportWindow(SteamLibraryImportViewModel model)
    {
        InitializeComponent();
        DataContext = _model = model;
    }
    private void Import(object sender, RoutedEventArgs e)
    {
        try { SelectedGames = _model.Selection(); DialogResult = true; }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "请选择游戏主程序", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
