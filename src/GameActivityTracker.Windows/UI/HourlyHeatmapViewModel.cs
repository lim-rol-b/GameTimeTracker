using System.Windows;
using System.Windows.Media;
using GameActivityTracker.Core.Statistics;

namespace GameActivityTracker.Windows.UI;

public sealed record HourRecordRow(string Game, string TimeRange, string State, string Duration);
public sealed record HourGameRow(string Name, string Durations);
public sealed class HourTile
{
    public int Hour { get; }
    public string Label => $"{Hour:00}:00";
    public string Title => $"{Hour:00}:00 – {Hour + 1:00}:00";
    public string Date { get; }
    public Brush Color { get; }
    public string Active { get; }
    public string Running { get; }
    public string OtherStates { get; }
    public string Notice { get; }
    public IReadOnlyList<HourGameRow> Games { get; }
    public IReadOnlyList<HourRecordRow> Records { get; }
    public string AccessibleLabel => $"{Date} {Title}，活跃时长 {Active}，运行时长 {Running}";

    public HourTile(DateOnly date, HourlyStatistics stats, IReadOnlyList<Game> games, TimeZoneInfo zone)
    {
        Hour = stats.Hour;
        Date = date.ToString("yyyy-MM-dd");
        Color = (Brush)Application.Current.FindResource($"Heat{HourlyStatisticsService.HeatLevel(stats.ActiveSeconds)}");
        Active = DurationFormat.Short(stats.ActiveSeconds);
        Running = DurationFormat.Short(stats.RunningSeconds);
        OtherStates = $"空闲 {DurationFormat.Short(stats.IdleSeconds)}  ·  后台 {DurationFormat.Short(stats.BackgroundSeconds)}  ·  未知 {DurationFormat.Short(stats.UnknownSeconds)}";
        Notice = stats.AvailableSeconds == 0 ? "本地时钟在这一天跳过了此小时。"
            : stats.AvailableSeconds != 3600 ? $"时区变化：此小时实际包含 {stats.AvailableSeconds / 60:0} 分钟；重复小时合并展示。"
            : stats.Records.Count == 0 ? "此小时暂无游戏记录。" : "仅显示此小时内的记录；时间包含协调世界时偏移。";
        string Name(string id) => games.FirstOrDefault(g => g.Id == id)?.Name ?? "未知游戏";
        Games = stats.Records.GroupBy(r => r.GameId).Select(group => new HourGameRow(Name(group.Key),
            $"活跃 {DurationFormat.Short(group.Where(r => r.State == ActivityState.ACTIVE).Sum(r => r.Duration))} · 运行 {DurationFormat.Short(group.Sum(r => r.Duration))}")).ToList();
        Records = stats.Records.Select(r => new HourRecordRow(Name(r.GameId),
            $"{TimeZoneInfo.ConvertTime(r.StartTime, zone):HH:mm:ss zzz} → {TimeZoneInfo.ConvertTime(r.EndTime, zone):HH:mm:ss zzz}",
            UiText.State(r.State), DurationFormat.Short(r.Duration))).ToList();
    }
}

public sealed class HourlyHeatmapViewModel : ObservableObject
{
    public IReadOnlyList<HourTile> Hours { get; }
    private HourTile? _selectedHour;
    public HourTile? SelectedHour
    {
        get => _selectedHour;
        set { if (Set(ref _selectedHour, value)) Changed(nameof(DetailVisibility)); }
    }
    public Visibility DetailVisibility => SelectedHour is null ? Visibility.Collapsed : Visibility.Visible;
    public HourlyHeatmapViewModel(DateOnly date, IEnumerable<GameSession> sessions, IReadOnlyList<Game> games)
    {
        var zone = TimeZoneInfo.Local;
        Hours = new HourlyStatisticsService().ForDay(date, sessions, zone).Select(h => new HourTile(date, h, games, zone)).ToList();
    }
}
