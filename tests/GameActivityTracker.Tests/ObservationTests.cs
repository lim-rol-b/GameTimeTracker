using GameActivityTracker.Core;
using GameActivityTracker.Core.Statistics;
using GameActivityTracker.Core.Tracking;
using Xunit;
namespace GameActivityTracker.Tests;
public sealed class ObservationTests
{
    [Fact] public void SleepRetainsRunningButNeverCreatesActiveTime()
    {
        var t=DateTimeOffset.Parse("2026-09-15T12:00Z");var manager=new SessionManager();
        manager.Start("game",t,new(true,t));manager.Advance("game",t.AddSeconds(10),new(null,null));
        manager.Advance("game",t.AddHours(2),new(true,t.AddHours(2)));var s=manager.Stop("game",t.AddHours(2).AddSeconds(10))!;
        Assert.Equal(20,s.ActiveDuration);Assert.Equal(7190,s.UnknownDuration);Assert.Equal(7210,s.RunningDuration);Assert.Equal(3,s.Segments.Count);
    }
    [Fact] public void CurrentStreakContinuesAcrossNewYear()
    {
        var manager=new SessionManager();var sessions=new List<GameSession>();
        foreach(var day in new[]{"2025-12-30","2025-12-31","2026-01-01"})
        {
            var t=DateTimeOffset.Parse(day+"T12:00Z");manager.Start("game",t,new(true,t));sessions.Add(manager.Stop("game",t.AddSeconds(30))!);
        }
        var stats=new StatisticsService().Year(sessions,TimeZoneInfo.Utc,2026,new(2026,1,1));
        Assert.Equal(3,stats.CurrentStreak);Assert.Equal(1,stats.ActiveDays);Assert.Equal(1,stats.LongestStreak);
    }
    [Fact] public void ManyTransitionsPreserveContiguousNonnegativeIntervals()
    {
        var random=new Random(871);var manager=new SessionManager();var start=DateTimeOffset.Parse("2026-09-15T12:00Z");
        manager.Start("game",start,new(true,start));var now=start;var input=start;
        for(var i=0;i<2000;i++)
        {
            now=now.AddMilliseconds(random.Next(100,2000));if(random.Next(10)==0)input=now.AddMilliseconds(-50);
            bool? foreground=random.Next(5) switch{0=>false,1=>null,_=>true};
            manager.Advance("game",now,new(foreground,input));
        }
        var session=manager.Stop("game",now)!;Assert.Equal((now-start).TotalSeconds,session.RunningDuration,6);
        Assert.Equal(session.RunningDuration,session.ActiveDuration+session.IdleDuration+session.BackgroundDuration+session.UnknownDuration,6);
        for(var i=0;i<session.Segments.Count;i++)
        {
            Assert.True(session.Segments[i].Duration>0);
            if(i>0)Assert.Equal(session.Segments[i-1].EndTime,session.Segments[i].StartTime);
        }
    }
}
