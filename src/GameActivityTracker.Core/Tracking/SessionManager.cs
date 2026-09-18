namespace GameActivityTracker.Core.Tracking;

/// <summary>Single-writer state machine. All boundaries are UTC observations, never inferred pre-launch activity.</summary>
public sealed class SessionManager
{
    private sealed class Tracked(GameSession session, ActivityObservation observation, DateTimeOffset time)
    {
        public GameSession Session { get; } = session;
        public ActivityObservation Observation { get; set; } = observation;
        public DateTimeOffset Time { get; set; } = time;
    }
    private readonly Dictionary<string, Tracked> _running = [];
    private readonly ActivityDetector _detector = new();
    public IReadOnlyList<GameSession> Sessions => _running.Values.Select(t => t.Session).ToList();
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(60);
    public GameSession Start(string gameId, DateTimeOffset now, ActivityObservation observation, string source = "Observed")
    {
        if (_running.TryGetValue(gameId, out var existing)) return existing.Session;
        var session = new GameSession { GameId = gameId, StartTime = now, LastCheckpoint = now, Source = source };
        _running.Add(gameId, new Tracked(session, observation, now));
        return session;
    }
    public ActivityState State(string gameId) => _running.TryGetValue(gameId, out var t)
        ? _detector.Detect(t.Time, t.Observation, IdleThreshold) : ActivityState.UNKNOWN;
    public void Advance(string gameId, DateTimeOffset now, ActivityObservation observation)
    {
        if (!_running.TryGetValue(gameId, out var t)) return;
        if (now < t.Time) return; // Wall-clock correction must not create negative or overlapping data.
        var state = _detector.Detect(t.Time, t.Observation, IdleThreshold);
        var cursor = t.Time;
        // Input timestamps can locate an idle->active transition inside this sampling interval.
        var inputAt = observation.LastInput;
        bool sameForeground = t.Observation.IsForeground == true && observation.IsForeground == true;
        if (state == ActivityState.ACTIVE && t.Observation.LastInput is { } last)
        {
            var expiry = last + IdleThreshold;
            if (sameForeground && inputAt > last && inputAt <= expiry && inputAt <= now)
                expiry = inputAt.Value + IdleThreshold;
            var activeEnd = expiry < now ? expiry : now;
            Append(t.Session, cursor, activeEnd, ActivityState.ACTIVE);
            cursor = activeEnd;
            state = ActivityState.IDLE;
        }
        if (cursor < now && state == ActivityState.IDLE && sameForeground && inputAt > cursor && inputAt <= now)
        {
            Append(t.Session, cursor, inputAt.Value, ActivityState.IDLE);
            var expiry = inputAt.Value + IdleThreshold;
            var end = expiry < now ? expiry : now;
            Append(t.Session, inputAt.Value, end, ActivityState.ACTIVE);
            cursor = end;
        }
        Append(t.Session, cursor, now, state);
        t.Time = now;
        t.Observation = observation;
        t.Session.LastCheckpoint = now;
    }
    public GameSession? Stop(string gameId, DateTimeOffset now, string reason = "ProcessExited")
    {
        if (!_running.TryGetValue(gameId, out var t)) return null;
        Advance(gameId, now, t.Observation);
        t.Session.EndTime = t.Time;
        t.Session.EndReason = reason;
        _running.Remove(gameId);
        return t.Session;
    }
    public IReadOnlyList<GameSession> StopAll(DateTimeOffset now, string reason) =>
        _running.Keys.ToArray().Select(id => Stop(id, now, reason)!).ToList();
    private static void Append(GameSession session, DateTimeOffset start, DateTimeOffset end, ActivityState state)
    {
        if (end <= start) return;
        var previous = session.Segments.LastOrDefault();
        if (previous?.State == state && previous.EndTime == start) previous.EndTime = end;
        else session.Segments.Add(new ActivitySegment { SessionId = session.Id, StartTime = start, EndTime = end, State = state });
    }
}
