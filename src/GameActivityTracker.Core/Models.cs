namespace GameActivityTracker.Core;

public enum ActivityState { ACTIVE, IDLE, BACKGROUND, UNKNOWN }
public sealed record Game
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string? SteamAppId { get; set; }
    public bool DetectRelatedExecutables { get; set; } = true;
    public string? MinecraftDirectory { get; set; }
    public string? InstallDirectory { get; set; }
    public string? IconPath { get; set; }
    public string? CoverPath { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record GameProcessRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? PathContains { get; set; }
    public string? MinecraftRootDirectory { get; set; }
    public string? CommandLineContains { get; set; }
    public string? SteamAppId { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}
public sealed class GameSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GameId { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public DateTimeOffset LastCheckpoint { get; set; }
    public string Source { get; init; } = "Observed";
    public string? EndReason { get; set; }
    public List<ActivitySegment> Segments { get; } = [];
    public double RunningDuration => Segments.Sum(s => s.Duration);
    public double ActiveDuration => Duration(ActivityState.ACTIVE);
    public double IdleDuration => Duration(ActivityState.IDLE);
    public double BackgroundDuration => Duration(ActivityState.BACKGROUND);
    public double UnknownDuration => Duration(ActivityState.UNKNOWN);
    private double Duration(ActivityState state) => Segments.Where(s => s.State == state).Sum(s => s.Duration);
}
public sealed class ActivitySegment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public ActivityState State { get; init; }
    public double Duration => Math.Max(0, (EndTime - StartTime).TotalSeconds);
}
public sealed record TrackerSettings
{
    public string ThemeMode { get; set; } = "System";
    public int FirstTrackingYear { get; set; } = DateTime.Now.Year;
    public int IdleThresholdSeconds { get; set; } = 60;
    public int ProcessScanIntervalSeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; }
    public bool EnableControllerDetection { get; set; } = true;
    public double ControllerDeadZone { get; set; } = 0.20;
    public void Validate()
    {
        if (ThemeMode is not ("System" or "Light" or "Dark")) throw new ArgumentException("无效的主题模式。");
        if (FirstTrackingYear is < 1 or > 9999) throw new ArgumentException("无效的起始年份。");
        if (IdleThresholdSeconds is < 1 or > 3600) throw new ArgumentException("空闲阈值必须为 1–3600 秒。");
        if (ProcessScanIntervalSeconds is < 1 or > 60) throw new ArgumentException("扫描间隔必须为 1–60 秒。");
        if (!double.IsFinite(ControllerDeadZone) || ControllerDeadZone is < .05 or > .95) throw new ArgumentException("手柄死区必须为 0.05–0.95。");
    }
}
public interface IGameRepository
{
    IReadOnlyList<Game> GetGames();
    IReadOnlyList<GameProcessRule> GetRules();
    void SaveGame(Game game, IReadOnlyList<GameProcessRule> rules);
    void DeleteGame(string id);
}
public interface ISessionRepository
{
    void SaveSessions(IEnumerable<GameSession> sessions);
    IReadOnlyList<GameSession> GetSessions();
    int RecoverOpenSessions();
}
public interface ISettingsRepository
{
    TrackerSettings GetSettings();
    void SaveSettings(TrackerSettings settings);
}
public interface ITrackerLog { void Write(string message, Exception? error = null); }
