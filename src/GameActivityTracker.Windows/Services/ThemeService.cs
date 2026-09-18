using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameActivityTracker.Windows.Services;

public sealed class ThemeService : IDisposable
{
    private sealed class ColorToken : INotifyPropertyChanged
    {
        private Color _color;
        public Color Color { get => _color; set { _color=value; PropertyChanged?.Invoke(this,new(nameof(Color))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly Dictionary<string,ColorToken> _tokens=[];
    private string _mode="System";
    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged+=OnPreferenceChanged;
    }
    public void Apply(string mode)
    {
        _mode=mode;
        var dark=mode=="Dark" || mode=="System" && SystemIsDark();
        var colors=new Dictionary<string,(string Light,string Dark)>
        {
            ["BackgroundBrush"]=("#F6F8FA","#0D1117"), ["PanelBrush"]=("#FFFFFF","#161B22"),
            ["TextBrush"]=("#1F2328","#E6EDF3"), ["MutedBrush"]=("#59636E","#9198A1"),
            ["BorderBrush"]=("#D1D9E0","#3D444D"), ["ControlBrush"]=("#EAEEF2","#21262D"),
            ["AccentSoftBrush"]=("#DAFBE1","#17452A"), ["SelectionBrush"]=("#0969DA","#1F6FEB"),
            ["HeatmapBackground"]=("#FFFFFF","#0D1117"), ["HeatBorder"]=("#D8DEE4","#1B222A"),
            ["Heat0"]=("#EFF2F5","#151B23"), ["Heat1"]=("#ACEEBB","#003B1B"),
            ["Heat2"]=("#6FDD8B","#0E652D"), ["Heat3"]=("#4AC26B","#239A3B"),
            ["Heat4"]=("#2DA44E","#39C653"), ["Heat5"]=("#116329","#56D364")
        };
        foreach(var (name,pair) in colors)
        {
            if(!_tokens.TryGetValue(name,out var token))
            {
                token=new(); _tokens.Add(name,token);
                var brush=new SolidColorBrush();
                // A bound color cannot be frozen by a WPF style/template. Existing day/hour
                // view models keep the same brush and update immediately when themes change.
                BindingOperations.SetBinding(brush,SolidColorBrush.ColorProperty,new Binding(nameof(ColorToken.Color)){Source=token});
                Application.Current.Resources[name]=brush;
            }
            token.Color=(Color)ColorConverter.ConvertFromString(dark?pair.Dark:pair.Light);
        }
        foreach(var key in new[]{SystemColors.WindowBrushKey,SystemColors.ControlBrushKey}) Application.Current.Resources[key]=Application.Current.Resources["PanelBrush"];
        foreach(var key in new[]{SystemColors.WindowTextBrushKey,SystemColors.ControlTextBrushKey}) Application.Current.Resources[key]=Application.Current.Resources["TextBrush"];
        Application.Current.Resources[SystemColors.GrayTextBrushKey]=Application.Current.Resources["MutedBrush"];
        Application.Current.Resources[SystemColors.HighlightBrushKey]=Application.Current.Resources["SelectionBrush"];
        Application.Current.Resources[SystemColors.HighlightTextBrushKey]=Brushes.White;
    }
    private static bool SystemIsDark()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light==0;
        }
        catch(Exception ex) when(ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
    private void OnPreferenceChanged(object sender,UserPreferenceChangedEventArgs e)
    {
        var dispatcher=Application.Current.Dispatcher;
        if(!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(()=>{if(_mode=="System") Apply(_mode);}));
    }
    public void Dispose()=>SystemEvents.UserPreferenceChanged-=OnPreferenceChanged;
}
