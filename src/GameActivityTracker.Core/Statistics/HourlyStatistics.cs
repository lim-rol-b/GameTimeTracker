namespace GameActivityTracker.Core.Statistics;

public sealed record HourlyActivityRecord(string SessionId, string GameId, DateTimeOffset StartTime,
    DateTimeOffset EndTime, ActivityState State)
{
    public double Duration => (EndTime - StartTime).TotalSeconds;
}

public sealed class HourlyStatistics
{
    public int Hour { get; init; }
    public double AvailableSeconds { get; set; }
    public List<HourlyActivityRecord> Records { get; } = [];
    public double RunningSeconds => Records.Sum(r => r.Duration);
    public double ActiveSeconds => Duration(ActivityState.ACTIVE);
    public double IdleSeconds => Duration(ActivityState.IDLE);
    public double BackgroundSeconds => Duration(ActivityState.BACKGROUND);
    public double UnknownSeconds => Duration(ActivityState.UNKNOWN);
    private double Duration(ActivityState state) => Records.Where(r => r.State == state).Sum(r => r.Duration);
}

/// <summary>Projects UTC segments into 24 local clock-hour bins without changing persisted data.</summary>
public sealed class HourlyStatisticsService
{
    public IReadOnlyList<HourlyStatistics> ForDay(DateOnly date, IEnumerable<GameSession> sessions, TimeZoneInfo zone)
    {
        var hours = Enumerable.Range(0, 24).Select(h => new HourlyStatistics { Hour = h }).ToArray();
        var windows = new List<(int Hour, DateTimeOffset Start, DateTimeOffset End)>();
        // Enumerate real UTC minutes, not a fictitious 24-hour UTC day. This also handles
        // repeated/skipped clock hours and half-hour DST transitions in current Windows zones.
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var limit = midnight.AddDays(1).AddHours(14);
        for (var cursor = midnight.AddHours(-14); cursor < limit; cursor = cursor.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTime(cursor, zone);
            if (DateOnly.FromDateTime(local.DateTime) != date) continue;
            var end = cursor.AddMinutes(1);
            hours[local.Hour].AvailableSeconds += 60;
            if (windows.Count > 0 && windows[^1].Hour == local.Hour && windows[^1].End == cursor)
                windows[^1] = (local.Hour, windows[^1].Start, end);
            else windows.Add((local.Hour, cursor, end));
        }
        foreach (var session in sessions)
        foreach (var segment in session.Segments)
        foreach (var window in windows)
        {
            var start = segment.StartTime > window.Start ? segment.StartTime : window.Start;
            var end = segment.EndTime < window.End ? segment.EndTime : window.End;
            if (end <= start) continue;
            hours[window.Hour].Records.Add(new(session.Id, session.GameId, start, end, segment.State));
        }
        foreach (var hour in hours) hour.Records.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        return hours;
    }

    public static int HeatLevel(double seconds) => seconds <= 0 ? 0 : seconds <= 300 ? 1
        : seconds <= 900 ? 2 : seconds <= 1800 ? 3 : seconds <= 2700 ? 4 : 5;
}
