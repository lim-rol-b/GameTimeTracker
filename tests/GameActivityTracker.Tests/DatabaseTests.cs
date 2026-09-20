using GameActivityTracker.Core;
using GameActivityTracker.Core.Tracking;
using GameActivityTracker.Data;
using Microsoft.Data.Sqlite;
using Xunit;
namespace GameActivityTracker.Tests;
public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"tracker-tests-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"activity.db");
    [Fact] public void InitializeSaveReopenAndRecoverAtLastCheckpoint()
    {
        var db=new TrackerDatabase(DatabasePath);var game=new Game{Name="Test",Executable="game.exe"};
        db.SaveGame(game,[new(){GameId=game.Id,ExecutableName="game.exe"}]);
        var start=DateTimeOffset.Parse("2026-09-15T12:00Z");var manager=new SessionManager();manager.Start(game.Id,start,new(true,start));
        manager.Advance(game.Id,start.AddSeconds(70),new(true,start));db.SaveSessions(manager.Sessions);
        var reopened=new TrackerDatabase(DatabasePath);Assert.Equal(1,reopened.RecoverOpenSessions());Assert.Equal(0,reopened.RecoverOpenSessions());
        var stored=reopened.GetSessions().Single();Assert.Equal(start.AddSeconds(70),stored.EndTime);Assert.Equal("RecoveredAtCheckpoint",stored.EndReason);
        Assert.Equal(60,stored.ActiveDuration);Assert.Equal(10,stored.IdleDuration);Assert.Equal(70,stored.RunningDuration);Assert.Equal(2,stored.Segments.Count);
        Assert.Equal(game.Name,reopened.GetGames().Single().Name);Assert.Single(reopened.GetRules());
    }
    [Fact] public void CheckpointsUpsertWithoutDoubleCounting()
    {
        var db=new TrackerDatabase(DatabasePath);var game=new Game{Name="Test"};db.SaveGame(game,[new(){GameId=game.Id,ExecutableName="game.exe"}]);
        var t=DateTimeOffset.UtcNow;var m=new SessionManager();m.Start(game.Id,t,new(true,t));m.Advance(game.Id,t.AddSeconds(10),new(true,t));
        db.SaveSessions(m.Sessions);m.Advance(game.Id,t.AddSeconds(20),new(true,t));db.SaveSessions(m.Sessions);
        var ended=m.Stop(game.Id,t.AddSeconds(30))!;db.SaveSessions([ended]);db.SaveSessions([ended]);
        var session=db.GetSessions().Single();Assert.Single(session.Segments);Assert.Equal(30,session.RunningDuration);Assert.Equal(0,db.RecoverOpenSessions());
    }
    [Fact] public void DeleteGameCascadesAndSettingsPersist()
    {
        var db=new TrackerDatabase(DatabasePath);var game=new Game{Name="Test"};db.SaveGame(game,[new(){GameId=game.Id,ExecutableName="game.exe"}]);
        var m=new SessionManager();var t=DateTimeOffset.UtcNow;m.Start(game.Id,t,new(true,t));db.SaveSessions([m.Stop(game.Id,t.AddMinutes(1))!]);
        db.SaveSettings(new(){IdleThresholdSeconds=120,ControllerDeadZone=.3});db.DeleteGame(game.Id);
        var next=new TrackerDatabase(DatabasePath);Assert.Empty(next.GetGames());Assert.Empty(next.GetRules());Assert.Empty(next.GetSessions());Assert.Equal(120,next.GetSettings().IdleThresholdSeconds);
    }
    [Fact] public void FailedCheckpointRollsBackEntireTransaction()
    {
        var db=new TrackerDatabase(DatabasePath);var game=new Game{Name="Test"};db.SaveGame(game,[new(){GameId=game.Id,ExecutableName="game.exe"}]);
        var m=new SessionManager();var t=DateTimeOffset.UtcNow;var valid=m.Start(game.Id,t,new(true,t));
        var invalid=new GameSession{GameId="missing",StartTime=t,LastCheckpoint=t};
        Assert.Throws<SqliteException>(()=>db.SaveSessions([valid,invalid]));Assert.Empty(db.GetSessions());
    }
    [Fact] public void IncrementalCheckpointsPreserveTailTransitionsAndRetryAtomically()
    {
        var db=new TrackerDatabase(DatabasePath);var game=new Game{Name="Incremental"};
        db.SaveGame(game,[new(){GameId=game.Id,ExecutableName="game.exe"}]);
        var t=DateTimeOffset.UtcNow;var manager=new SessionManager();
        var session=manager.Start(game.Id,t,new(true,t));
        var counts=new Dictionary<string,int>();
        manager.Advance(game.Id,t.AddSeconds(30),new(true,t));
        db.SaveSessions(manager.Sessions,counts);counts[session.Id]=session.Segments.Count;
        manager.Advance(game.Id,t.AddSeconds(70),new(false,t));
        var invalid=new GameSession{GameId="missing",StartTime=t,LastCheckpoint=t};
        Assert.Throws<SqliteException>(()=>db.SaveSessions([session,invalid],counts));
        Assert.Equal(30,Assert.Single(db.GetSessions()).RunningDuration);
        db.SaveSessions(manager.Sessions,counts);counts[session.Id]=session.Segments.Count;
        manager.Advance(game.Id,t.AddSeconds(80),new(false,t));
        db.SaveSessions(manager.Sessions,counts);counts[session.Id]=session.Segments.Count;
        var ended=manager.Stop(game.Id,t.AddSeconds(90))!;
        db.SaveSessions([ended],counts);db.SaveSessions([ended],counts);
        var stored=Assert.Single(db.GetSessions());
        Assert.Equal(60,stored.ActiveDuration);Assert.Equal(10,stored.IdleDuration);
        Assert.Equal(20,stored.BackgroundDuration);Assert.Equal(90,stored.RunningDuration);
        Assert.Equal(3,stored.Segments.Count);Assert.Equal(ended.EndTime,stored.EndTime);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
