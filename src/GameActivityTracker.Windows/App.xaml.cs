using System.Windows;
using System.Windows.Media.Imaging;
using GameActivityTracker.Data;
using GameActivityTracker.Windows.Services;
using GameActivityTracker.Windows.UI;

namespace GameActivityTracker.Windows;
public partial class App : Application
{
    private ThemeService? _theme;
    private Mutex? _instance;
    private bool _ownsMutex;
    private EventWaitHandle? _activate;
    private Thread? _activateThread;
    private TrackingService? _tracking;
    private TrackerDatabase? _database;
    private FileTrackerLog? _log;
    private volatile bool _exiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke=e.Args.Contains("--smoke-test");
        ShutdownMode=ShutdownMode.OnMainWindowClose;

        // The native engine owns the only tray icon. The WPF window has no tray: closing
        // it exits, and a second viewer instance only asks the first one to come forward.
        var suffix=smoke?".SmokeTest":"";
        _activate=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\GameActivityTracker"+suffix+".Activate");
        _instance=new Mutex(true,@"Local\GameActivityTracker"+suffix,out _ownsMutex);
        if(!_ownsMutex)
        {
            try { _activate.Set(); } catch { }
            Shutdown();return;
        }

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
            DispatcherUnhandledException+=(_,args)=>{_log.Write("Unhandled UI error",args.Exception);};
            _tracking.Start();
            _activateThread=new Thread(WaitForActivation){IsBackground=true,Name="GameActivityTracker.Activate"};
            _activateThread.Start();
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
    private void WaitForActivation()
    {
        while(!_exiting)
        {
            try { if(_activate is null || !_activate.WaitOne()) continue; }
            catch(Exception) { break; }
            if(_exiting) break;
            Dispatcher.BeginInvoke(new Action(ActivateWindow));
        }
    }
    private void ActivateWindow()
    {
        if(MainWindow is null) return;
        MainWindow.Show();
        if(MainWindow.WindowState==WindowState.Minimized) MainWindow.WindowState=WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost=true;MainWindow.Topmost=false;
    }
    // Autostart belongs to the native engine: it writes the Run key to itself on start and
    // on every settings reload, so the viewer must not touch the registry.
    private void ApplyStartup(TrackerSettings _) { }
    private void ExitApplication(){_exiting=true;Shutdown();}
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting=true;
        try { _activate?.Set(); } catch { }
        _theme?.Dispose();
        _tracking?.Dispose();
        if(_ownsMutex)_instance?.ReleaseMutex();
        _instance?.Dispose();
        _activate?.Dispose();
        base.OnExit(e);
    }
}
