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
    private bool _locked;
    private bool _sleeping;
    private bool _hiddenNotice;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke=e.Args.Contains("--smoke-test");
        _instance=new Mutex(true,smoke?@"Local\GameActivityTracker.SmokeTest":@"Local\GameActivityTracker",out _ownsMutex);
        if(!_ownsMutex){MessageBox.Show("Game Activity Tracker 已在运行。请从系统托盘打开。");Shutdown();return;}
        var startupStage="准备数据目录";
        string? logDirectory=null;
        try
        {
            var directory=smoke?Path.Combine(Path.GetTempPath(),"GameActivityTracker.SmokeTest"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameActivityTracker");
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
            var model=new DashboardViewModel(_database,_tracking,ApplyStartup,directory,_theme);
            startupStage="加载主界面";
            var window=new MainWindow(model);MainWindow=window;
            LoadWindowIcon(window);
            window.Closing+=OnClosing;
            window.StateChanged+=(_,_)=>{if(window.WindowState==WindowState.Minimized&&_database.GetSettings().RunInBackground)HideToTray();};
            startupStage="初始化系统托盘";
            CreateTray();
            SystemEvents.PowerModeChanged+=OnPower;
            SystemEvents.SessionSwitch+=OnSessionSwitch;
            SessionEnding+=OnSessionEnding;
            DispatcherUnhandledException+=(_,args)=>{_log.Write("Unhandled UI error",args.Exception);};
            _tracking.Start();
            startupStage="显示主界面";
            if(!e.Args.Contains("--background"))window.Show();
            _log.Write("Application initialized");
            if(smoke)
            {
                var smokeTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(4)};
                smokeTimer.Tick+=(_,_)=>
                {
                    smokeTimer.Stop();
                    if(_tracking.Error is {} error){_log.Write("Smoke test failed: "+error);_exiting=true;Shutdown(2);}
                    else { _log.Write("Smoke test passed: window initialized, dispatcher and tracking loop alive");ExitApplication(); }
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
            MessageBox.Show($"启动失败（{startupStage}）：\n{string.Join("\n",details)}\n\n日志目录：{logDirectory ?? "尚未创建"}","Game Activity Tracker",MessageBoxButton.OK,MessageBoxImage.Error);
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
        _tray=new Forms.NotifyIcon{Icon=_trayIcon,Text="Game Activity Tracker",Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Open / 打开",null,(_,_)=>ShowWindow());
        var status=menu.Items.Add("Tracking Status: 记录中");status.Enabled=false;
        var game=menu.Items.Add("Current Game: 无");game.Enabled=false;
        menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("Exit / 退出",null,(_,_)=>ExitApplication());
        _tray.ContextMenuStrip=menu;_tray.DoubleClick+=(_,_)=>ShowWindow();
        _trayTimer=new(){Interval=TimeSpan.FromSeconds(2)};
        _trayTimer.Tick+=(_,_)=>
        {
            status.Text=_tracking?.Error is {} error?"Tracking error: "+error:"Tracking Status: "+(_locked||_sleeping?"暂停":"记录中");
            var live=_tracking?.Snapshot();
            if(live is null||live.Count==0){game.Text="Current Game: 无";return;}
            try
            {
                var games=_database!.GetGames();
                game.Text="Current Game: "+string.Join(", ",live.Select(g=>(games.FirstOrDefault(x=>x.Id==g.GameId)?.Name??g.GameId)+" "+g.State));
            }
            catch(Exception ex){status.Text="数据库暂时不可读";_log?.Write("Tray metadata read failed",ex);}
        };
        _trayTimer.Start();
    }
    private void HideToTray()
    {
        MainWindow.Hide();
        if(_hiddenNotice)return;_hiddenNotice=true;
        _tray?.ShowBalloonTip(2500,"Game Activity Tracker","正在后台记录。双击托盘图标打开；右键 Exit 完全退出。",Forms.ToolTipIcon.Info);
    }
    private void ShowWindow(){MainWindow.Show();MainWindow.WindowState=WindowState.Normal;MainWindow.Activate();}
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
