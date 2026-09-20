using GameActivityTracker.Core;
using GameActivityTracker.Core.Tracking;
using Xunit;

namespace GameActivityTracker.Tests;

public sealed class SessionSnapshotTests
{
    [Fact]
    public void MembershipSnapshotsStayStableWhileDurationsAdvance()
    {
        var manager=new SessionManager();
        var now=DateTimeOffset.UtcNow;
        var empty=manager.Sessions;
        var first=manager.Start("first",now,new(true,now));
        var one=manager.Sessions;
        manager.Advance("first",now.AddSeconds(1),new(true,now.AddSeconds(1)));
        Assert.Same(one,manager.Sessions);
        Assert.Equal(1,one[0].RunningDuration);
        manager.Start("second",now,new(false,now));
        var two=manager.Sessions;
        manager.Stop("first",now.AddSeconds(2));
        Assert.Empty(empty);
        Assert.Single(one);
        Assert.Equal(2,two.Count);
        Assert.Same(first,one[0]);
        Assert.Equal("second",Assert.Single(manager.Sessions).GameId);
        Assert.Equal(3,Assert.Single(manager.StopAll(now.AddSeconds(3),"exit")).RunningDuration);
    }

    [Fact]
    public void StopAllDoesNotLeaveSessionsInCachedSnapshot()
    {
        var manager=new SessionManager();
        var now=DateTimeOffset.UtcNow;
        manager.Start("one",now,new(false,now));
        manager.Start("two",now,new(false,now));
        var before=manager.Sessions;
        Assert.Equal(2,manager.StopAll(now.AddSeconds(3),"exit").Count);
        Assert.Empty(manager.Sessions);
        Assert.All(before,s=>Assert.Equal(now.AddSeconds(3),s.EndTime));
        manager.Start("one",now.AddSeconds(4),new(false,now));
        Assert.NotSame(before[0],Assert.Single(manager.Sessions));
    }
}
