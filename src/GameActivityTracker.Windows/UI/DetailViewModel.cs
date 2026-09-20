using GameActivityTracker.Core.Statistics;
namespace GameActivityTracker.Windows.UI;
public sealed record DayGameRow(string Name,string Active,string Running);
public sealed class DetailViewModel
{
    public string Title { get; }
    public string Subtitle { get; }
    public string Summary { get; }
    public string Note { get; }
    public IReadOnlyList<SessionRow> Sessions { get; }
    public IReadOnlyList<DayGameRow> Games { get; }=[];
    public HeatmapViewModel? Heatmap { get; }
    public HourlyHeatmapViewModel? HourlyHeatmap { get; }
    public System.Windows.Visibility HourlyHeatmapVisibility=>HourlyHeatmap is null?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
    public System.Windows.Visibility HeatmapVisibility=>Heatmap is null?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
    public System.Windows.Visibility SessionsVisibility=>HourlyHeatmap is null?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility EmptyGamesVisibility=>Games.Count==0?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
    public DetailViewModel(DateOnly date,DailyStatistics? stats,IReadOnlyList<GameSession> sessions,IReadOnlyList<Game> games)
    {
        Title=date.ToString("yyyy-MM-dd");Subtitle="每日详情 · 本地日期";
        HourlyHeatmap=new(date,sessions,games);
        Summary=$"活跃时长  {DurationFormat.Short(stats?.ActiveSeconds??0)}     运行时长  {DurationFormat.Short(stats?.RunningSeconds??0)}";
        Games=stats?.GameRunningSeconds.Select(g=>new DayGameRow(games.FirstOrDefault(x=>x.Id==g.Key)?.Name??"未知游戏",DurationFormat.Short(stats.GameActiveSeconds.GetValueOrDefault(g.Key)),DurationFormat.Short(g.Value))).ToList()??[];
        Sessions=[];
        Note="时长仅统计此自然日，详情为打开时快照。";
    }
    public DetailViewModel(Game game,IReadOnlyList<GameSession> sessions,IReadOnlyList<Game> games,int year,Action<DateOnly> selectDay)
    {
        Title=game.Name;Subtitle=$"游戏详情 · {year}";
        var service=new StatisticsService();var stats=service.Game(sessions,TimeZoneInfo.Local);
        Summary=$"活跃时长  {DurationFormat.Short(stats.ActiveSeconds)}     运行时长  {DurationFormat.Short(stats.RunningSeconds)}\n\n游玩次数 {stats.Sessions}     活跃天数 {stats.ActiveDays}     最长单次游玩 {DurationFormat.Short(stats.LongestSession)}\n\n首次记录  {stats.FirstTracked?.ToLocalTime().ToString("yyyy-MM-dd")??"—"}";
        Sessions=sessions.Take(200).Select(s=>DashboardViewModel.Row(s,games)).ToList();
        Heatmap=new(selectDay);Heatmap.Update(year,service.Daily(sessions,TimeZoneInfo.Local),games);
        Note=$"统计为该游戏全部本地记录；热力图为 {year} 年。最多显示最近 200 个会话。Steam 应用编号： {game.SteamAppId??"未设置"}。详情为打开时快照。";
    }
}
