using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GameActivityTracker.Data;
using GameActivityTracker.Windows.Services;
using GameActivityTracker.Windows.UI;
using Forms=System.Windows.Forms;

namespace GameActivityTracker.Windows;
public partial class App : Application
{
    private ThemeService? _theme;
    private Mutex? _instance;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private TrackingService? _tracking;
    private TrackerDatabase? _database;
    private FileTrackerLog? _log;
    private bool _exiting;
    private string _dataDirectory="";
    private bool _locked;
    private bool _sleeping;
    private bool _hiddenNotice;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke=e.Args.Contains("--smoke-test");
        _instance=new Mutex(true,smoke?@"Local\GameActivityTracker.SmokeTest":@"Local\GameActivityTracker",out _ownsMutex);
        if(!_ownsMutex){MessageBox.Show("游戏时长记录器已在运行。请从系统托盘打开。");Shutdown();return;}
        var startupStage="准备数据目录";
        string? logDirectory=null;
        try
        {
            var directory=smoke?Path.Combine(Path.GetTempPath(),"GameActivityTracker.SmokeTest"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameActivityTracker");
            _dataDirectory=directory;
            logDirectory=Path.Combine(directory,"logs");
            Directory.CreateDirectory(logDirectory);_log=new(logDirectory);
            startupStage="初始化数据库";
            _database=new(Path.Combine(directory,"activity.db"));
            var recovered=_database.RecoverOpenSessions();_log.Write($"Startup: recovered {recovered} session(s) at persisted checkpoint");
            var settings=_database.GetSettings();
            _database.SaveSettings(settings); // Persist the initial year for existing installations.
            _theme=new ThemeService();
            _theme.Apply(settings.ThemeMode);
            startupStage="初始化记录服务";
            _tracking=new(_database,_log);
            startupStage="初始化系统托盘";
            CreateTray();
            SystemEvents.PowerModeChanged+=OnPower;
            SystemEvents.SessionSwitch+=OnSessionSwitch;
            SessionEnding+=OnSessionEnding;
            DispatcherUnhandledException+=(_,args)=>{_log.Write("Unhandled UI error",args.Exception);};
            _tracking.Start();
            startupStage="显示主界面";
            if(!e.Args.Contains("--background"))ShowWindow();
            _log.Write("Application initialized");
            if(smoke)
            {
                var phase=0;
                Window? original=null;
                var smokeTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
                smokeTimer.Tick+=(_,_)=>
                {
                    try
                    {
                        if(_tracking.Error is {} error) throw new InvalidOperationException(error);
                        switch(phase++)
                        {
                            case 0:
                                if(e.Args.Contains("--background") && MainWindow is not null)
                                    throw new InvalidOperationException("Background startup eagerly created the window.");
                                ShowWindow();original=MainWindow;
                                break;
                            case 1:
                                if(MainWindow.DataContext is not DashboardViewModel model || model.Metrics.Count!=6)
                                    throw new InvalidOperationException("Dashboard did not refresh after opening.");
                                model.SelectedTab=1;
                                MainWindow.Hide();
                                break;
                            case 2:
                                ShowWindow();
                                if(!ReferenceEquals(original,MainWindow) || ((DashboardViewModel)MainWindow.DataContext).SelectedTab!=1)
                                    throw new InvalidOperationException("Tray restore lost window state.");
                                break;
                            default:
                                GamePresence.ProcessProviderSmokeTests.Run(_log);
                                smokeTimer.Stop();
                                _log.Write("Smoke test passed: lazy startup, tray restore, process rule invalidation, tracking loop");
                                ExitApplication();
                                break;
                        }
                    }
                    catch(Exception ex)
                    {
                        smokeTimer.Stop();_log.Write("Smoke test failed",ex);_exiting=true;Shutdown(2);
                    }
                };
                smokeTimer.Start();
            }
        }
        catch(Exception ex)
        {
            _exiting=true;
            _log?.Write("Startup failed at " + startupStage,ex);
            var details=new List<string>();
            for(Exception? cause=ex;cause is not null;cause=cause.InnerException)
                details.Add($"{cause.GetType().Name}: {cause.Message}");
            MessageBox.Show($"启动失败（{startupStage}）：\n{string.Join("\n",details)}\n\n日志目录：{logDirectory ?? "尚未创建"}","游戏时长记录器",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void LoadWindowIcon(Window window)
    {
        try
        {
            using var stream=GetResourceStream(new Uri("pack://application:,,,/Assets/App.png")).Stream;
            var icon=new BitmapImage();
            icon.BeginInit();
            icon.CacheOption=BitmapCacheOption.OnLoad;
            icon.StreamSource=stream;
            icon.EndInit();
            icon.Freeze();
            window.Icon=icon;
        }
        catch(Exception ex)
        {
            _log?.Write("Custom window icon unavailable; continuing with default icon",ex);
        }
    }
    private void CreateTray()
    {
        try
        {
            using var iconStream=GetResourceStream(new Uri("pack://application:,,,/Assets/App.ico")).Stream;
            using var sourceIcon=new System.Drawing.Icon(iconStream,Forms.SystemInformation.SmallIconSize);
            _trayIcon=(System.Drawing.Icon)sourceIcon.Clone();
        }
        catch(Exception ex)
        {
            _log?.Write("Custom tray icon unavailable; using default icon",ex);
            _trayIcon=(System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }
        _tray=new Forms.NotifyIcon{Icon=_trayIcon,Text="游戏时长记录器",Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("打开",null,(_,_)=>ShowWindow());
        var status=menu.Items.Add("记录状态：记录中");status.Enabled=false;
        var game=menu.Items.Add("当前游戏：无");game.Enabled=false;
        menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出",null,(_,_)=>ExitApplication());
        _tray.ContextMenuStrip=menu;_tray.DoubleClick+=(_,_)=>ShowWindow();
        _trayTimer=new(){Interval=TimeSpan.FromSeconds(2)};
        _trayTimer.Tick+=(_,_)=>
        {
            status.Text=_tracking?.Error is {} error?"记录异常："+error:"记录状态："+(_locked||_sleeping?"暂停":"记录中");
            var live=_tracking?.Snapshot();
            if(live is null||live.Count==0){game.Text="当前游戏：无";return;}
            try
            {
                var games=_tracking!.GameNames();
                game.Text="当前游戏："+string.Join(", ",live.Select(g=>(games.GetValueOrDefault(g.GameId)??g.GameId)+" "+UiText.State(g.State)));
            }
            catch(Exception ex){status.Text="数据库暂时不可读";_log?.Write("Tray metadata read failed",ex);}
        };
        _trayTimer.Start();
    }
    private void HideToTray()
    {
        MainWindow.Hide();
        if(_hiddenNotice)return;_hiddenNotice=true;
        _tray?.ShowBalloonTip(2500,"游戏时长记录器","正在后台记录。双击托盘图标打开；右键选择“退出”可完全退出。",Forms.ToolTipIcon.Info);
    }
    private void ShowWindow()
    {
        if(MainWindow is null)
        {
            var model=new DashboardViewModel(_database!,_tracking!,ApplyStartup,_dataDirectory,_theme!);
            var window=new MainWindow(model);MainWindow=window;
            LoadWindowIcon(window);
            window.Closing+=OnClosing;
            window.StateChanged+=(_,_)=>{if(window.WindowState==WindowState.Minimized&&_database!.GetSettings().RunInBackground)HideToTray();};
        }
        MainWindow.Show();MainWindow.WindowState=WindowState.Normal;MainWindow.Activate();
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_exiting)return;
        if(_database!.GetSettings().MinimizeToTray){e.Cancel=true;HideToTray();}
        else { e.Cancel=true;Dispatcher.BeginInvoke(new Action(ExitApplication)); }
    }
    private void OnPower(object sender,PowerModeChangedEventArgs e)
    {
        if(e.Mode==PowerModes.Suspend){_sleeping=true;_tracking?.Suspend("SystemSuspend");}
        else if(e.Mode==PowerModes.Resume){_sleeping=false;_tracking?.Resume();}
    }
    private void OnSessionSwitch(object sender,SessionSwitchEventArgs e)
    {
        if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
        {_locked=true;_tracking?.SetLocked(true);}
        else if(e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
        {_locked=false;_tracking?.SetLocked(false);}
    }
    private void OnSessionEnding(object sender,SessionEndingCancelEventArgs e){_exiting=true;_tracking?.Suspend("WindowsSessionEnding");}
    private void ApplyStartup(TrackerSettings settings)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(settings.StartWithWindows)
        {
            var executable=Environment.ProcessPath??throw new InvalidOperationException("无法确定应用路径。");
            if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请使用发布后的 GameActivityTracker.exe 设置开机启动。");
            key.SetValue("GameActivityTracker",$"\"{executable}\" --background");
        }
        else key.DeleteValue("GameActivityTracker",false);
    }
    private void ExitApplication(){_exiting=true;Shutdown();}
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged-=OnPower;SystemEvents.SessionSwitch-=OnSessionSwitch;
        _theme?.Dispose();
        _trayTimer?.Stop();_tracking?.Dispose();
        if(_tray is not null){_tray.Visible=false;_tray.ContextMenuStrip?.Dispose();_tray.Dispose();}
        _trayIcon?.Dispose();
        if(_ownsMutex)_instance?.ReleaseMutex();_instance?.Dispose();base.OnExit(e);
    }
}
