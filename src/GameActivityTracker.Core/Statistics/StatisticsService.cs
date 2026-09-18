namespace GameActivityTracker.Core.Statistics;

public sealed class DailyStatistics
{
    public DateOnly Date { get; init; }
    public double ActiveSeconds { get; set; }
    public double RunningSeconds { get; set; }
    public Dictionary<string, double> GameActiveSeconds { get; } = [];
    public Dictionary<string, double> GameRunningSeconds { get; } = [];
}
public sealed record YearlyStatistics(double ActiveSeconds, double RunningSeconds, int ActiveDays,
    int SessionCount, double LongestSession, int CurrentStreak, int LongestStreak);
public sealed record GameStatistics(double ActiveSeconds, double RunningSeconds, int Sessions, int ActiveDays,
    double LongestSession, DateTimeOffset? FirstTracked);
public sealed class StatisticsService
{
    public SortedDictionary<DateOnly, DailyStatistics> Daily(IEnumerable<GameSession> sessions, TimeZoneInfo zone)
    {
        var days = new SortedDictionary<DateOnly, DailyStatistics>();
        foreach (var session in sessions)
        foreach (var segment in session.Segments)
        foreach (var (day, seconds) in Split(segment.StartTime, segment.EndTime, zone))
        {
            if (!days.TryGetValue(day, out var item)) days[day] = item = new DailyStatistics { Date = day };
            item.RunningSeconds += seconds;
            item.GameRunningSeconds[session.GameId] = item.GameRunningSeconds.GetValueOrDefault(session.GameId) + seconds;
            if (segment.State != ActivityState.ACTIVE) continue;
            item.ActiveSeconds += seconds;
            item.GameActiveSeconds[session.GameId] = item.GameActiveSeconds.GetValueOrDefault(session.GameId) + seconds;
        }
        return days;
    }
    public IEnumerable<(DateOnly Day, double Seconds)> Split(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        while (start < end)
        {
            var local = TimeZoneInfo.ConvertTime(start, zone);
            var day = DateOnly.FromDateTime(local.DateTime);
            var nextLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // Some time zones advance clocks at midnight or skip a calendar date.
            while (zone.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddMinutes(1);
            var offsets = zone.IsAmbiguousTime(nextLocal) ? zone.GetAmbiguousTimeOffsets(nextLocal) : [zone.GetUtcOffset(nextLocal)];
            var next = offsets.Select(o => new DateTimeOffset(nextLocal, o).ToUniversalTime()).Where(d => d > start).Min();
            var stop = end < next ? end : next;
            yield return (day, (stop - start).TotalSeconds);
            start = stop;
        }
    }
    public YearlyStatistics Year(IEnumerable<GameSession> source, TimeZoneInfo zone, int year, DateOnly today)
    {
        var sessions = source.ToList();
        var allDays = Daily(sessions, zone);
        var days = allDays.Values.Where(d => d.Date.Year == year).ToList();
        var activeDays = days.Where(d => d.ActiveSeconds > 0).Select(d => d.Date).ToHashSet();
        var longest = 0;
        var current = 0;
        foreach (var day in activeDays.Order())
        {
            var length = 1;
            for (var prior = day.AddDays(-1); activeDays.Contains(prior); prior = prior.AddDays(-1)) length++;
            longest = Math.Max(longest, length);
        }
        if (today.Year == year)
        {
            var cursor = allDays.GetValueOrDefault(today)?.ActiveSeconds > 0 ? today : today.AddDays(-1);
            while (allDays.GetValueOrDefault(cursor)?.ActiveSeconds > 0) { current++; cursor = cursor.AddDays(-1); }
        }
        var intersecting = sessions.Where(s => s.Segments.Any(seg => Split(seg.StartTime, seg.EndTime, zone).Any(d => d.Day.Year == year))).ToList();
        return new(days.Sum(d => d.ActiveSeconds), days.Sum(d => d.RunningSeconds), activeDays.Count,
            intersecting.Count, intersecting.Select(s => s.RunningDuration).DefaultIfEmpty().Max(), current, longest);
    }
    public GameStatistics Game(IEnumerable<GameSession> source, TimeZoneInfo zone)
    {
        var sessions = source.ToList();
        return new(sessions.Sum(s => s.ActiveDuration), sessions.Sum(s => s.RunningDuration), sessions.Count,
            Daily(sessions, zone).Count(d => d.Value.ActiveSeconds > 0), sessions.Select(s => s.RunningDuration).DefaultIfEmpty().Max(),
            sessions.Select(s => (DateTimeOffset?)s.StartTime).Min());
    }
    public static int HeatLevel(double seconds) => seconds <= 0 ? 0 : seconds <= 1800 ? 1 : seconds <= 3600 ? 2 : seconds <= 7200 ? 3 : seconds <= 14400 ? 4 : 5;
}
