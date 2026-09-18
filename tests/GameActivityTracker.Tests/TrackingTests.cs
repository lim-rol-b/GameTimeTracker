using GameActivityTracker.Core;
using GameActivityTracker.Core.GamePresence;
using GameActivityTracker.Core.Tracking;
using Xunit;

namespace GameActivityTracker.Tests;
public sealed class TrackingTests
{
    private static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-09-15T12:00:00Z");
    [Theory]
    [InlineData(0,ActivityState.ACTIVE)][InlineData(59.999,ActivityState.ACTIVE)][InlineData(60,ActivityState.IDLE)][InlineData(120,ActivityState.IDLE)]
    public void IdleThreshold(double elapsed,ActivityState expected)=>Assert.Equal(expected,new ActivityDetector().Detect(T.AddSeconds(elapsed),new(true,T),TimeSpan.FromSeconds(60)));
    [Fact] public void ForegroundIsRequiredEvenWhenOtherAppsReceiveInput()
    {
        var detector=new ActivityDetector();
        Assert.Equal(ActivityState.BACKGROUND,detector.Detect(T,new(false,T),TimeSpan.FromSeconds(60)));
        Assert.Equal(ActivityState.UNKNOWN,detector.Detect(T,new(null,T),TimeSpan.FromSeconds(60)));
        Assert.Equal(ActivityState.UNKNOWN,detector.Detect(T,new(true,null),TimeSpan.FromSeconds(60)));
    }
    [Fact] public void IdleReturnsActiveImmediatelyAfterInput()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));
        m.Advance("a",T.AddSeconds(80),new(true,T));Assert.Equal(ActivityState.IDLE,m.State("a"));
        m.Advance("a",T.AddSeconds(81),new(true,T.AddSeconds(80.5)));Assert.Equal(ActivityState.ACTIVE,m.State("a"));
        var s=m.Stop("a",T.AddSeconds(90))!;
        Assert.Equal(69.5,s.ActiveDuration,5);Assert.Equal(20.5,s.IdleDuration,5);Assert.Equal(90,s.RunningDuration);
    }
    [Fact] public void ThresholdSplitsBetweenSamplingInstants()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));m.Advance("a",T.AddSeconds(59.7),new(true,T));
        m.Advance("a",T.AddSeconds(60.2),new(true,T));var s=m.Stop("a",T.AddSeconds(70))!;
        Assert.Equal(60,s.ActiveDuration);Assert.Equal(10,s.IdleDuration);Assert.Equal(2,s.Segments.Count);
    }
    [Fact] public void InputBeforeOldDeadlineExtendsActiveWindow()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));m.Advance("a",T.AddSeconds(59),new(true,T));
        m.Advance("a",T.AddSeconds(61),new(true,T.AddSeconds(59.5)));var s=m.Stop("a",T.AddSeconds(65))!;
        Assert.Equal(65,s.ActiveDuration);Assert.Equal(0,s.IdleDuration);
    }
    [Fact] public void ForegroundBackgroundForegroundTransitions()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));m.Advance("a",T.AddSeconds(10),new(false,T));
        Assert.Equal(ActivityState.BACKGROUND,m.State("a"));
        m.Advance("a",T.AddSeconds(30),new(true,T.AddSeconds(29)));Assert.Equal(ActivityState.ACTIVE,m.State("a"));
        var s=m.Stop("a",T.AddSeconds(40))!;Assert.Equal(20,s.ActiveDuration);Assert.Equal(20,s.BackgroundDuration);
    }
    [Fact] public void ReturningToForegroundWithoutRecentInputIsIdle()
    {
        var m=new SessionManager();m.Start("a",T,new(false,T));m.Advance("a",T.AddSeconds(90),new(true,T));
        Assert.Equal(ActivityState.IDLE,m.State("a"));var s=m.Stop("a",T.AddSeconds(100))!;
        Assert.Equal(90,s.BackgroundDuration);Assert.Equal(10,s.IdleDuration);
    }
    [Fact] public void SessionStartIsIdempotentAndEndRemovesIt()
    {
        var m=new SessionManager();var s=m.Start("a",T,new(true,T));Assert.Same(s,m.Start("a",T.AddSeconds(2),new(true,T)));
        Assert.Null(s.EndTime);Assert.Same(s,m.Stop("a",T.AddSeconds(10)));Assert.Equal(T.AddSeconds(10),s.EndTime);
        Assert.Empty(m.Sessions);Assert.Null(m.Stop("a",T.AddSeconds(20)));
    }
    [Fact] public void SuspendAndResumeHaveNoUnobservedGap()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));var first=m.StopAll(T.AddSeconds(10),"Suspend").Single();
        m.Start("a",T.AddHours(2),new(true,T.AddHours(2)));var second=m.Stop("a",T.AddHours(2).AddSeconds(10))!;
        Assert.Equal(20,first.RunningDuration+second.RunningDuration);
    }
    [Fact] public void BackwardsClockDoesNotMakeNegativeIntervals()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));m.Advance("a",T.AddSeconds(10),new(true,T));
        var s=m.Stop("a",T.AddSeconds(-10))!;Assert.Equal(T.AddSeconds(10),s.EndTime);Assert.Equal(10,s.RunningDuration);
    }
    [Fact] public void MultipleGamesCannotShareForegroundActivity()
    {
        var m=new SessionManager();m.Start("a",T,new(true,T));m.Start("b",T,new(false,T));
        var sessions=m.StopAll(T.AddSeconds(20),"Exit");Assert.Equal(20,sessions.Sum(s=>s.ActiveDuration));Assert.Equal(40,sessions.Sum(s=>s.RunningDuration));
    }
    [Fact] public void DeadZoneSuppressesStickAndTriggerNoise()
    {
        var a=new ControllerSample(0,1,-2,4,-3,1,2);var b=new ControllerSample(0,150,-120,50,80,15,10);
        Assert.False(ControllerFilter.HasMeaningfulChange(a,b,.2));
        Assert.True(ControllerFilter.HasMeaningfulChange(a,b with{LX=18000},.2));
        Assert.True(ControllerFilter.HasMeaningfulChange(a,b with{Buttons=0x1000},.2));
        Assert.True(ControllerFilter.HasMeaningfulChange(a,b with{RT=100},.2));
        Assert.False(ControllerFilter.HasMeaningfulChange(b with{LX=20000},b with{LX=20000},.2));
    }
    [Fact] public void StickReleaseCountsAsChangeAndNegativeAxisDoesNotOverflow()
    {
        Assert.True(ControllerFilter.HasMeaningfulChange(new(0,short.MinValue,0,0,0,0,0),default,.2));
        Assert.Equal(short.MinValue,ControllerFilter.Normalize(new(0,short.MinValue,0,0,0,0,0),.2).LX);
    }
    [Fact] public void MatcherUsesConjunctionAndNeverGuessesMissingMetadata()
    {
        var rule=new GameProcessRule{GameId="minecraft",ExecutableName="javaw.exe",CommandLineContains="minecraft",PathContains="java"};
        var p=new ProcessSnapshot(1,"javaw.exe",@"C:\Java\bin\javaw.exe","-cp minecraft",T);
        var matcher=new GameMatcher();Assert.Equal("minecraft",matcher.Match(p,[rule]));
        Assert.Null(matcher.Match(p with{CommandLine="other.jar"},[rule]));
        Assert.Null(matcher.Match(p with{CommandLine=null},[rule]));
        Assert.Null(matcher.Match(p with{FullPath=null},[rule]));
    }
    [Fact] public void ExactPathPriorityAndDisabledRules()
    {
        var p=new ProcessSnapshot(1,"GAME.EXE",@"C:\Games\game.exe",null,T);
        var low=new GameProcessRule{GameId="low",ExecutableName="game.exe"};
        var high=new GameProcessRule{GameId="high",ExecutableName="game.exe",ExecutablePath=@"c:\games\GAME.EXE",Priority=10};
        var matcher=new GameMatcher();Assert.Equal("high",matcher.Match(p,[low,high]));
        Assert.Equal("low",matcher.Match(p,[low,high with{Enabled=false}]));
        Assert.Equal("low",matcher.Match(p with{FullPath=@"C:\Other\game.exe"},[low,high]));
    }
    [Fact] public void SettingsRejectUnsafeValues()
    {
        Assert.Throws<ArgumentException>(()=>new TrackerSettings{IdleThresholdSeconds=0}.Validate());
        Assert.Throws<ArgumentException>(()=>new TrackerSettings{ControllerDeadZone=double.NaN}.Validate());
        Assert.Throws<ArgumentException>(()=>new TrackerSettings{ProcessScanIntervalSeconds=0}.Validate());
    }
}
