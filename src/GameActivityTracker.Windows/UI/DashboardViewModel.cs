using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using GameActivityTracker.Core.Statistics;
using GameActivityTracker.Core.GamePresence;
using GameActivityTracker.Data;
using GameActivityTracker.Windows.Services;

namespace GameActivityTracker.Windows.UI;
public sealed record ThemeOption(string Value,string Label);
public sealed record Metric(string Label,string Value);
public sealed record GameRow(Game Game,string Active,string Running,int Sessions)
{
    public string Name=>Game.Name;
    public string Executable=>Game.ExecutablePath;
}
public sealed record SessionRow(string Game,string Start,string End,string Active,string Running,string Idle,string Background,string Source);
public sealed class DashboardViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly TrackerDatabase _db;
    private readonly TrackingService _tracking;
    private readonly StatisticsService _statistics=new();
    private readonly Action<TrackerSettings> _applyStartup;
    private readonly string _dataDirectory;
    private List<Game> _games=[];
    private IReadOnlyList<GameSession> _history=[];
    private SortedDictionary<DateOnly,DailyStatistics> _daily=[];
    private int _lastYearCheck;
    private int _year=DateTime.Now.Year;
    private int _tab;
    private int _settingsCategory;
    private string _status="正在启动本地记录服务…";
    private string _currentGameTitle="等待游戏启动";
    private string _live="等待游戏启动";
    private string _periods="";
    private bool _refreshing;
    private bool _rendering;
    private bool _importing;
    public int Year { get=>_year; set { if(value>=1 && Years.Contains(value) && Set(ref _year,value)) _=RenderAsync(); } }
    public ObservableCollection<int> Years { get; }=[];
    public ThemeOption[] Themes { get; }=[new("System","跟随系统"),new("Light","浅色"),new("Dark","深色")];
    public string ThemeMode
    {
        get=>Settings.ThemeMode;
        set
        {
            if(value==Settings.ThemeMode || value is not ("System" or "Light" or "Dark")) return;
            try
            {
                var saved=_db.GetSettings(); saved.ThemeMode=value; _db.SaveSettings(saved);
                Settings.ThemeMode=value; _theme.Apply(value); Changed(nameof(ThemeMode));
            }
            catch(Exception ex) { Status="主题保存失败："+ex.Message; Changed(nameof(ThemeMode)); }
        }
    }
    private void UpdateYears(bool force=false)
    {
        var currentYear=DateTime.Now.Year;
        if(!force && _lastYearCheck==currentYear) return;
        _lastYearCheck=currentYear;
        var years=TrackingYears.Available(Settings.FirstTrackingYear,currentYear,_history.Select(s=>s.StartTime.ToLocalTime().Year));
        if(Years.SequenceEqual(years)) return;
        foreach(var old in Years.Except(years).ToList()) Years.Remove(old);
        for(var i=0;i<years.Length;i++) if(!Years.Contains(years[i])) Years.Insert(i,years[i]);
        if(!Years.Contains(Year)) Year=years[0];
    }
    public int SelectedTab { get=>_tab; set { if(Set(ref _tab,value)) Changed(nameof(PageTitle)); } }
    public string PageTitle => SelectedTab==1 ? "设置" : CurrentGameTitle;
    public int SettingsCategory { get=>_settingsCategory; set=>Set(ref _settingsCategory,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public string CurrentGameTitle { get=>_currentGameTitle; private set { if(Set(ref _currentGameTitle,value)) Changed(nameof(PageTitle)); } }
    public string LiveStatus { get=>_live; private set=>Set(ref _live,value); }
    public string Periods { get=>_periods; private set=>Set(ref _periods,value); }
    public TrackerSettings Settings { get; }
    public string IdleThresholdText { get; set; }
    public string ScanIntervalText { get; set; }
    public string DeadZoneText { get; set; }
    public string DataDirectory=>_dataDirectory;
    public ObservableCollection<Metric> Metrics { get; }=[];
    public ObservableCollection<GameRow> Games { get; }=[];
    public HeatmapViewModel Heatmap { get; }
    private GameRow? _selectedGame;
    public GameRow? SelectedGame { get=>_selectedGame; set=>Set(ref _selectedGame,value); }
    public ICommand AddGame { get; }
    public ICommand ImportSteamLibrary { get; }
    public ICommand EditGame { get; }
    public ICommand DeleteGame { get; }
    public ICommand OpenGame { get; }
    public ICommand SaveSettings { get; }
    public ICommand OpenLogs { get; }
    public DashboardViewModel(TrackerDatabase db,TrackingService tracking,Action<TrackerSettings> applyStartup,string dataDirectory,ThemeService theme)
    {
        _theme=theme;
        _db=db;_tracking=tracking;_applyStartup=applyStartup;_dataDirectory=dataDirectory; Settings=db.GetSettings();
        IdleThresholdText=Settings.IdleThresholdSeconds.ToString(); ScanIntervalText=Settings.ProcessScanIntervalSeconds.ToString(); DeadZoneText=Settings.ControllerDeadZone.ToString(System.Globalization.CultureInfo.CurrentCulture);
        UpdateYears();
        Heatmap=new(OpenDay);
        AddGame=new RelayCommand(_=>Edit(null));
        ImportSteamLibrary=new RelayCommand(parameter=>ImportLibrary(parameter as string == "manual"),_=>!_importing);
        EditGame=new RelayCommand(_=>Edit(SelectedGame?.Game),_=>SelectedGame is not null);
        DeleteGame=new RelayCommand(_=>Remove(),_=>SelectedGame is not null);
        OpenGame=new RelayCommand(_=>ShowGame(),_=>SelectedGame is not null);
        SaveSettings=new RelayCommand(_=>Save());
        OpenLogs=new RelayCommand(_=>Safe(()=>Process.Start(new ProcessStartInfo(Path.Combine(_dataDirectory,"logs")){UseShellExecute=true})));
    }
    public async Task UpdateLiveAsync()
    {
        UpdateYears();
        var snapshot=await _tracking.PollAsync();
        ApplyLive(snapshot.Games,snapshot.Error);
    }
    public void UpdateLive()
    {
        UpdateYears();
        ApplyLive(_tracking.Snapshot(),_tracking.Error);
    }
    private void ApplyLive(IReadOnlyList<LiveGame> live,string? error)
    {
        CurrentGameTitle=live.Count==0 ? "等待游戏启动" : string.Join(" · ",live
            .OrderByDescending(g=>g.State==ActivityState.ACTIVE)
            .ThenBy(g=>g.GameId,StringComparer.Ordinal)
            .Select(g=>_games.FirstOrDefault(x=>x.Id==g.GameId)?.Name??"正在记录的游戏"));
        LiveStatus=live.Count==0 ? "等待游戏启动 · 添加游戏后将自动识别" : string.Join("\n",live.Select(g=>$"●  {_games.FirstOrDefault(x=>x.Id==g.GameId)?.Name??g.GameId}   {UiText.State(g.State)}    活跃 {DurationFormat.Clock(g.ActiveSeconds)}    运行 {DurationFormat.Clock(g.RunningSeconds)}"+(g.State==ActivityState.IDLE?$"    已空闲 {DurationFormat.Clock(g.InputIdleSeconds)}":"")));
        if(!_importing) Status=error is {} message ? "记录异常，将自动重试："+message : $"本地记录中  ·  {_games.Count} 个游戏  ·  每 {Settings.ProcessScanIntervalSeconds} 秒扫描  ·  {TimeZoneInfo.Local.DisplayName}";
    }
    public async Task Refresh()
    {
        if(_refreshing) return;_refreshing=true;
        try
        {
            var result=await Task.Run(()=>(_db.GetGames(),_db.GetSessions()));
            _games=result.Item1.ToList();_history=result.Item2; UpdateYears(true);
            await RenderAsync();await UpdateLiveAsync();
        }
        catch(Exception ex) { Status="读取失败："+ex.Message; }
        finally { _refreshing=false; }
    }
    private async Task RenderAsync()
    {
        if(_rendering) return;_rendering=true;
        try
        {
            while(true)
            {
                var year=_year;var games=_games;var history=_history;
                var view=await Task.Run(()=>Compute(games,history,TimeZoneInfo.Local,DateOnly.FromDateTime(DateTime.Now),year));
                if(year!=_year) continue; // A newer year selection arrived while computing; recompute for it.
                Apply(view);break;
            }
        }
        catch(Exception ex) { Status="读取失败："+ex.Message; }
        finally { _rendering=false; }
    }
    private DashboardView Compute(IReadOnlyList<Game> games,IReadOnlyList<GameSession> history,TimeZoneInfo zone,DateOnly today,int year)
    {
        var daily=_statistics.Daily(history,zone);
        var yearly=_statistics.Year(history,zone,year,today);
        var metrics=new[]{new Metric("累计活跃时长",DurationFormat.Short(yearly.ActiveSeconds)),new Metric("活跃天数",yearly.ActiveDays.ToString()),new Metric("游玩次数",yearly.SessionCount.ToString()),new Metric("最长单次游玩",DurationFormat.Short(yearly.LongestSession)),new Metric("当前连续天数",yearly.CurrentStreak+" 天"),new Metric("最长连续天数",yearly.LongestStreak+" 天")};
        var weekStart=today.AddDays(-((int)today.DayOfWeek+6)%7);
        var periods=$"今天  {DurationFormat.Short(daily.GetValueOrDefault(today)?.ActiveSeconds??0)}     本周  {DurationFormat.Short(daily.Values.Where(d=>d.Date>=weekStart && d.Date<=today).Sum(d=>d.ActiveSeconds))}     本月  {DurationFormat.Short(daily.Values.Where(d=>d.Date.Year==today.Year && d.Date.Month==today.Month).Sum(d=>d.ActiveSeconds))}     {year} 年运行时长  {DurationFormat.Short(yearly.RunningSeconds)}";
        var rows=new List<GameRow>(games.Count);
        foreach(var game in games)
        {
            var stats=_statistics.Game(history.Where(s=>s.GameId==game.Id),zone);
            rows.Add(new(game,DurationFormat.Short(stats.ActiveSeconds),DurationFormat.Short(stats.RunningSeconds),stats.Sessions));
        }
        return new(daily,metrics,periods,rows);
    }
    private void Apply(DashboardView view)
    {
        Metrics.Clear();foreach(var metric in view.Metrics) Metrics.Add(metric);
        _daily=view.Daily;
        Heatmap.Update(_year,view.Daily,_games);
        Periods=view.Periods;
        var selected=SelectedGame?.Game.Id;Games.Clear();
        foreach(var game in view.Rows) Games.Add(game);
        SelectedGame=Games.FirstOrDefault(g=>g.Game.Id==selected);
    }
    private sealed record DashboardView(SortedDictionary<DateOnly,DailyStatistics> Daily,Metric[] Metrics,string Periods,List<GameRow> Rows);
    public static SessionRow Row(GameSession s,IReadOnlyList<Game> games)=>new(games.FirstOrDefault(g=>g.Id==s.GameId)?.Name??"未知游戏",s.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),s.EndTime?.ToLocalTime().ToString("MM-dd HH:mm:ss")??"记录中",DurationFormat.Short(s.ActiveDuration),DurationFormat.Short(s.RunningDuration),DurationFormat.Short(s.IdleDuration),DurationFormat.Short(s.BackgroundDuration),$"{UiText.Source(s.Source)} / {UiText.EndReason(s.EndReason)}");
    private void OpenDay(DateOnly day)
    {
        var daily=_daily.GetValueOrDefault(day);
        var sessions=_history.Where(s=>s.Segments.Any(seg=>_statistics.Split(seg.StartTime,seg.EndTime,TimeZoneInfo.Local).Any(d=>d.Day==day))).ToList();
        new DetailWindow(new DetailViewModel(day,daily,sessions,_games)){Owner=Application.Current.MainWindow}.Show();
    }
    private void ShowGame()
    {
        if(SelectedGame is null)return;
        new DetailWindow(new DetailViewModel(SelectedGame.Game,_history.Where(s=>s.GameId==SelectedGame.Game.Id).ToList(),_games,Year,OpenDay)){Owner=Application.Current.MainWindow}.Show();
    }
    private async void Edit(Game? game)
    {
        try
        {
            var editor=new GameEditorWindow(game,_db.GetRules().Where(r=>r.GameId==game?.Id).ToList()){Owner=Application.Current.MainWindow};
            if(editor.ShowDialog()!=true)return;
            _db.SaveGame(editor.Game,editor.Rules.ToList());_tracking.Reload();
            await Refresh();
        }
        catch(Exception ex){MessageBox.Show(ex.Message,"操作未完成",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private async void ImportLibrary(bool manual)
    {
        if(_importing) return;
        _importing=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            Status="正在自动扫描 Steam 默认位置和已登记游戏库…";
            var scan = manual ? null : await Task.Run(GamePresence.SteamLibraryDiscovery.Scan);
            if (scan is null || scan.Games.Count == 0)
            {
                var folder=new Microsoft.Win32.OpenFolderDialog { Title=manual ? "选择 Steam 游戏库文件夹" : "未发现已安装游戏，请选择 Steam 游戏库文件夹", Multiselect=false };
                if(folder.ShowDialog(Application.Current.MainWindow)!=true) return;
                Status="正在读取 Steam 游戏库清单和可执行文件…";
                var previousWarnings=scan?.Warnings;
                scan=await Task.Run(()=>new SteamLibraryReader().Read(folder.FolderName));
                if(previousWarnings is not null) scan=scan with { Warnings=previousWarnings.Concat(scan.Warnings).ToList() };
            }
            var result=(Scan:scan,Existing:await Task.Run(()=>_db.GetGames()));
            if(result.Scan.Games.Count==0)
            {
                var detail=result.Scan.Warnings.Count==0
                    ? "没有找到 appmanifest_*.acf 游戏清单。请确认选中了已安装游戏的 Steam 库；仅复制 common 下的游戏文件夹不会生成清单。"
                    : "游戏清单无法读取：\n"+string.Join("\n",result.Scan.Warnings.Take(5));
                MessageBox.Show(detail,"未找到可导入的游戏",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            Status=$"已读取 {result.Scan.Games.Count} 款 Steam 游戏，请选择要导入的项目。";
            var dialog=new SteamLibraryImportWindow(new(result.Scan,result.Existing)){Owner=Application.Current.MainWindow};
            if(dialog.ShowDialog()!=true) return;
            Status="正在保存选中的 Steam 游戏…";
            var selected=dialog.SelectedGames;
            var imported=await Task.Run(()=>SaveSteamGames(selected));
            string? reloadError=null;
            try { _tracking.Reload(); }
            catch(Exception ex) { reloadError=ex.Message; }
            SettingsCategory=0;
            SelectedTab=1;
            await Refresh();
            var message=$"已导入 {imported} 款游戏，跳过 {selected.Count-imported} 款重复游戏。";
            if(reloadError is not null) message+="\n游戏已保存，但记录服务刷新失败；请重启应用："+reloadError;
            MessageBox.Show(message,"Steam 游戏库导入完成",MessageBoxButton.OK,
                reloadError is null?MessageBoxImage.Information:MessageBoxImage.Warning);
        }
        catch(Exception ex) { MessageBox.Show(ex.Message,"Steam 游戏库导入未完成",MessageBoxButton.OK,MessageBoxImage.Error); }
        finally { _importing=false;CommandManager.InvalidateRequerySuggested();UpdateLive(); }
    }
    private int SaveSteamGames(IReadOnlyList<SteamGameImportSelection> selected)
    {
        var existing=_db.GetGames();
        var appIds=existing.Where(g=>!string.IsNullOrWhiteSpace(g.SteamAppId)).Select(g=>g.SteamAppId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths=existing.Select(g=>g.ExecutablePath).Concat(_db.GetRules().Select(r=>r.ExecutablePath??""))
            .Where(path=>!string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batch=new List<(Game Game,IReadOnlyList<GameProcessRule> Rules)>();
        foreach(var item in selected)
        {
            if(appIds.Contains(item.AppId)||paths.Contains(item.ExecutablePath)) continue;
            if(!File.Exists(item.ExecutablePath)) throw new IOException($"{item.Name} 的可执行文件已不存在，本次导入未保存，请重新选择。");
            var game=new Game{Name=item.Name,SteamAppId=item.AppId,InstallDirectory=item.InstallDirectory,ExecutablePath=item.ExecutablePath,Executable=Path.GetFileName(item.ExecutablePath)};
            // The confirmed full path works even when this library isn't registered with Steam.
            batch.Add((game,new[]{new GameProcessRule{GameId=game.Id,ExecutableName=game.Executable,ExecutablePath=game.ExecutablePath}}));
            appIds.Add(item.AppId);paths.Add(item.ExecutablePath);
        }
        if(batch.Count>0) _db.SaveGames(batch);
        return batch.Count;
    }
    private async void Remove()
    {
        if(SelectedGame is null)return;
        if(MessageBox.Show($"删除 {SelectedGame.Name} 及其所有历史会话？此操作无法撤销。","删除游戏",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        Safe(()=>
        {
            _tracking.DeleteGame(SelectedGame.Game.Id);
        });
        await Refresh();
    }
    private void Save()=>Safe(()=>
    {
        if(!int.TryParse(IdleThresholdText,out var idle)||!int.TryParse(ScanIntervalText,out var scan)||!double.TryParse(DeadZoneText,out var deadZone))
            throw new ArgumentException("阈值和扫描间隔必须为整数，死区必须为有效数字。");
        Settings.IdleThresholdSeconds=idle; Settings.ProcessScanIntervalSeconds=scan; Settings.ControllerDeadZone=deadZone;
        Settings.Validate();var previous=_db.GetSettings();
        _applyStartup(Settings);
        try { _db.SaveSettings(Settings); }
        catch { _applyStartup(previous);throw; }
        _tracking.Reload();Status="设置已保存。";
    });
    private static void Safe(Action action)
    {try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"操作未完成",MessageBoxButton.OK,MessageBoxImage.Error);}}
}
