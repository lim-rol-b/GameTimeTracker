using GameActivityTracker.Core;
using GameActivityTracker.Core.Statistics;
using Xunit;
namespace GameActivityTracker.Tests;
public sealed class StatisticsTests
{
    private readonly StatisticsService _statistics=new();
    private static GameSession Session(string start,string end,ActivityState state=ActivityState.ACTIVE)
    {
        var s=new GameSession{GameId="game",StartTime=DateTimeOffset.Parse(start),EndTime=DateTimeOffset.Parse(end)};
        s.Segments.Add(new(){SessionId=s.Id,StartTime=s.StartTime,EndTime=s.EndTime.Value,State=state});return s;
    }
    [Fact] public void CrossMidnightUsesLocalDates()
    {
        var zone=TimeZoneInfo.CreateCustomTimeZone("UTC+8",TimeSpan.FromHours(8),"UTC+8","UTC+8");
        var days=_statistics.Daily([Session("2026-09-15T15:30Z","2026-09-15T17:30Z")],zone);
        Assert.Equal(2,days.Count);Assert.Equal(1800,days[new(2026,9,15)].ActiveSeconds);Assert.Equal(5400,days[new(2026,9,16)].ActiveSeconds);
    }
    [Fact] public void NegativeOffsetUsesPreviousCalendarDay()
    {
        var zone=TimeZoneInfo.CreateCustomTimeZone("UTC-7",TimeSpan.FromHours(-7),"UTC-7","UTC-7");
        var days=_statistics.Daily([Session("2026-01-01T01:00Z","2026-01-01T02:00Z")],zone);
        Assert.Equal(new DateOnly(2025,12,31),days.Single().Key);
    }
    [Fact] public void DaylightSavingDayHas23Hours()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var days=_statistics.Daily([Session("2026-03-08T05:00Z","2026-03-09T04:00Z")],zone);
        Assert.Single(days);Assert.Equal(23*3600,days.Single().Value.ActiveSeconds);
    }
    [Fact] public void FallBackDayHas25Hours()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var days=_statistics.Daily([Session("2026-11-01T04:00Z","2026-11-02T05:00Z")],zone);
        Assert.Single(days);Assert.Equal(25*3600,days.Single().Value.ActiveSeconds);
    }
    [Fact] public void HeatmapAggregatesOnlyActiveButRunningIncludesAllStates()
    {
        var active=Session("2026-09-15T12:00Z","2026-09-15T13:00Z");
        var idle=Session("2026-09-15T13:00Z","2026-09-15T13:30Z",ActivityState.IDLE);
        var background=Session("2026-09-15T13:30Z","2026-09-15T14:00Z",ActivityState.BACKGROUND);
        var day=_statistics.Daily([active,idle,background],TimeZoneInfo.Utc).Single().Value;
        Assert.Equal(3600,day.ActiveSeconds);Assert.Equal(7200,day.RunningSeconds);Assert.Equal(3600,day.GameActiveSeconds["game"]);
    }
    [Theory]
    [InlineData(0,0)][InlineData(1,1)][InlineData(1800,1)][InlineData(1801,2)][InlineData(3600,2)][InlineData(3601,3)][InlineData(7201,4)][InlineData(14401,5)]
    public void HeatmapLevels(double seconds,int level)=>Assert.Equal(level,StatisticsService.HeatLevel(seconds));
    [Fact] public void StreakAllowsYesterdayWhenTodayNotPlayed()
    {
        var sessions=new[]{Session("2026-09-12T12:00Z","2026-09-12T13:00Z"),Session("2026-09-13T12:00Z","2026-09-13T13:00Z"),Session("2026-09-14T12:00Z","2026-09-14T13:00Z")};
        var year=_statistics.Year(sessions,TimeZoneInfo.Utc,2026,new(2026,9,15));Assert.Equal(3,year.CurrentStreak);Assert.Equal(3,year.LongestStreak);
        Assert.Equal(0,_statistics.Year(sessions,TimeZoneInfo.Utc,2026,new(2026,9,16)).CurrentStreak);
    }
    [Fact] public void YearBoundaryClipsAnnualTotals()
    {
        var session=Session("2025-12-31T23:30Z","2026-01-01T00:30Z");
        var year=_statistics.Year([session],TimeZoneInfo.Utc,2026,new(2026,1,1));
        Assert.Equal(1800,year.ActiveSeconds);Assert.Equal(1,year.SessionCount);Assert.Equal(1,year.ActiveDays);
    }
    [Fact] public void EmptyDataIsValid()
    {
        var year=_statistics.Year([],TimeZoneInfo.Utc,2026,new(2026,9,15));Assert.Equal(0,year.ActiveDays);Assert.Equal(0,year.LongestSession);
        Assert.Null(_statistics.Game([],TimeZoneInfo.Utc).FirstTracked);
    }
}
