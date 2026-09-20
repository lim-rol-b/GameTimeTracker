using System.Diagnostics;
using GameActivityTracker.Core.GamePresence;
using GameActivityTracker.Core.Tracking;
using GameActivityTracker.Data;
using GameActivityTracker.Windows.GamePresence;
using GameActivityTracker.Windows.Tracking;

namespace GameActivityTracker.Windows.Services;

public sealed record LiveGame(string GameId, ActivityState State, double ActiveSeconds, double RunningSeconds, double InputIdleSeconds);
public sealed class TrackingService : IDisposable
{
    private readonly object _gate = new();
    private readonly TrackerDatabase _database;
    private readonly ITrackerLog _log;
    private readonly SessionManager _sessions = new();
    private readonly IGamePresenceProvider _presence;
    private readonly IForegroundWindowDetector _foreground = new ForegroundWindowDetector();
    private readonly IKeyboardMouseActivityProvider _keyboard = new KeyboardMouseActivityProvider();
    private readonly IControllerActivityProvider _controller = new ControllerActivityProvider();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<GameSession> _pending = [];
    private readonly Dictionary<string,int> _persistedSegmentCounts=[];
    private Task? _worker;
    private TrackerSettings _settings;
    private IReadOnlyList<GameProcessRule> _rules;
    private IReadOnlyList<Game> _games;
    private DateTimeOffset _lastTick=DateTimeOffset.UtcNow;
    private DateTimeOffset _lastScan=DateTimeOffset.MinValue;
    private DateTimeOffset _lastSave=DateTimeOffset.MinValue;
    private long _lastMono=Stopwatch.GetTimestamp();
    private bool _suspended;
    private bool _locked;
    private bool _disposed;
    private string? _error;
    private DateTimeOffset? _lastInput;
    public string? Error { get { lock(_gate) return _error; } }
    public TrackingService(TrackerDatabase database,ITrackerLog log)
    {
        _database=database; _log=log; _settings=database.GetSettings(); _rules=database.GetRules(); _games=database.GetGames(); _gameNames=_games.ToDictionary(g=>g.Id,g=>g.Name);
        _sessions.IdleThreshold=TimeSpan.FromSeconds(_settings.IdleThresholdSeconds);
        _presence=new ProcessProvider(()=>_rules,()=>_games,log);
        _presence.GameStarted+=(_,e)=>
        {
            var observation=Observe(e.GameId,e.DetectedAt);
            _sessions.Start(e.GameId,e.DetectedAt,observation,"Observed; no pre-detection activity inferred");
            _log.Write($"Session started: {e.GameId}");
        };
        _presence.GameStopped+=(_,e)=>
        {
            var session=_sessions.Stop(e.GameId,e.DetectedAt);
            if(session is not null) _pending.Add(session);
        };
    }
    public void Start() => _worker=Task.Run(Run);
    public IReadOnlyList<LiveGame> Snapshot()
    {
        lock(_gate) return _sessions.Sessions.Select(s=>new LiveGame(s.GameId,_sessions.State(s.GameId),s.ActiveDuration,s.RunningDuration,
            Math.Max(0,_lastInput is {} input ? (_lastTick-input).TotalSeconds:0))).ToList();
    }
    public IReadOnlyDictionary<string,string> GameNames()
    {
        lock(_gate) return _gameNames;
    }
    private IReadOnlyDictionary<string,string> _gameNames=new Dictionary<string,string>();
    public void Reload()
    {
        lock(_gate)
        {
            var now=DateTimeOffset.UtcNow;
            foreach(var s in _sessions.Sessions) _sessions.Advance(s.GameId,now,Observe(s.GameId,now));
            Save(now);
            _settings=_database.GetSettings(); _rules=_database.GetRules();_games=_database.GetGames();_gameNames=_games.ToDictionary(g=>g.Id,g=>g.Name);
            _sessions.IdleThreshold=TimeSpan.FromSeconds(_settings.IdleThresholdSeconds);
            _controller.Reset(); _lastScan=DateTimeOffset.MinValue;
        }
    }
    public void Suspend(string reason)
    {
        lock(_gate)
        {
            _suspended=true;
            try
            {
                var now=DateTimeOffset.UtcNow;
                foreach(var s in _sessions.Sessions) _sessions.Advance(s.GameId,now,new(null,null));
                Save(now); _log.Write(reason+": observation paused; subsequent time is UNKNOWN");
            }
            catch(Exception ex) { _error=ex.Message; _log.Write("Suspend save failed",ex); }
        }
    }
    public void Resume()
    {
        lock(_gate)
        {
            var now=DateTimeOffset.UtcNow;
            foreach(var s in _sessions.Sessions) _sessions.Advance(s.GameId,now,new(null,null));
            _suspended=false; _lastTick=now; _lastMono=Stopwatch.GetTimestamp(); _lastScan=DateTimeOffset.MinValue; _controller.Reset();
        }
    }
    public void SetLocked(bool locked)
    {
        lock(_gate)
        {
            _locked=locked;
            var now=DateTimeOffset.UtcNow;
            foreach(var s in _sessions.Sessions) _sessions.Advance(s.GameId,now,new(locked?false:null,null));
            _controller.Reset();
        }
    }
    public void DeleteGame(string gameId)
    {
        lock(_gate)
        {
            var now=DateTimeOffset.UtcNow;
            var ended=_sessions.Stop(gameId,now,"GameDeleted");
            if(ended is not null) _pending.Add(ended);
            Save(now);_database.DeleteGame(gameId);_rules=_database.GetRules();_games=_database.GetGames();_gameNames=_games.ToDictionary(g=>g.Id,g=>g.Name);_lastScan=DateTimeOffset.MinValue;
        }
    }
    private async Task Run()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while(await timer.WaitForNextTickAsync(_stop.Token))
            {
                lock(_gate)
                {
                    if(_suspended) continue;
                    try { Tick(); _error=null; }
                    catch(Exception ex) { _error=ex.Message; _log.Write("Tracking tick failed; will retry",ex); }
                }
            }
        }
        catch(OperationCanceledException) { }
    }
    private void Tick()
    {
        var now=DateTimeOffset.UtcNow;
        var elapsed=Stopwatch.GetElapsedTime(_lastMono).TotalSeconds;
        var wall=(now-_lastTick).TotalSeconds;
        if(elapsed>10 || wall>10 || wall<0 || Math.Abs(wall-elapsed)>2)
        {
            if(wall<0) EndAll(_lastTick,"ClockMovedBackwards");
            else
            {
                // Preserve the running interval, but do not infer activity during unobserved time.
                foreach(var s in _sessions.Sessions)
                {
                    _sessions.Advance(s.GameId,_lastTick,new(null,null));
                    _sessions.Advance(s.GameId,now,new(null,null));
                }
                _controller.Reset();
            }
            _lastScan=DateTimeOffset.MinValue;
        }
        _lastTick=now; _lastMono=Stopwatch.GetTimestamp();
        var keyboard=_keyboard.GetLastInput(now);
        var controller=_settings.EnableControllerDetection ? _controller.Poll(now,_settings.ControllerDeadZone):null;
        _lastInput=keyboard is null ? controller : controller is null ? keyboard : keyboard>controller ? keyboard:controller;
        if((now-_lastScan).TotalSeconds>=_settings.ProcessScanIntervalSeconds) { _presence.Scan(now); _lastScan=now; }
        foreach(var s in _sessions.Sessions)
        {
            var old=_sessions.State(s.GameId);
            _sessions.Advance(s.GameId,now,Observe(s.GameId,now));
            var state=_sessions.State(s.GameId);
            if(old!=state) _log.Write($"{s.GameId}: {old} -> {state}");
        }
        if((now-_lastSave).TotalSeconds>=5 || _pending.Count>0) Save(now);
    }
    private ActivityObservation Observe(string game,DateTimeOffset now)
    {
        if(_locked) return new(false,null);
        if(_suspended) return new(null,null);
        var foreground=_foreground.GetForegroundProcessId();
        return new(foreground is null ? null : _presence.RunningGames.TryGetValue(game,out var pids) && pids.Contains(foreground.Value),_lastInput);
    }
    private void Save(DateTimeOffset now)
    {
        var sessions=_sessions.Sessions;
        if(_pending.Count>0 || sessions.Count>0)
        {
            _database.SaveSessions(_pending.Concat(sessions),_persistedSegmentCounts);
            foreach(var session in sessions) _persistedSegmentCounts[session.Id]=session.Segments.Count;
            foreach(var session in _pending) _persistedSegmentCounts.Remove(session.Id);
        }
        _pending.Clear(); _lastSave=now;
    }
    private void EndAll(DateTimeOffset now,string reason)
    {
        _pending.AddRange(_sessions.StopAll(now,reason)); _presence.Reset(); _controller.Reset();
        Save(now);
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
        lock(_gate) { try { EndAll(DateTimeOffset.UtcNow,"TrackerExit"); } catch(Exception ex) { _log.Write("Final checkpoint failed",ex); } }
        _stop.Dispose();
    }
}
