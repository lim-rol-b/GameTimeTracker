using System.Globalization;
using System.Text.Json;
using GameActivityTracker.Core;
using Microsoft.Data.Sqlite;

namespace GameActivityTracker.Data;

/// <summary>Short-lived connections, WAL, foreign keys, atomic checkpoints. Durations remain derived from segments.</summary>
public sealed class TrackerDatabase : IGameRepository, ISessionRepository, ISettingsRepository
{
    private readonly string _connectionString;
    public TrackerDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS Games(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Rules(Id TEXT PRIMARY KEY, GameId TEXT NOT NULL REFERENCES Games(Id) ON DELETE CASCADE, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sessions(Id TEXT PRIMARY KEY, GameId TEXT NOT NULL REFERENCES Games(Id) ON DELETE CASCADE,
                StartTime TEXT NOT NULL, EndTime TEXT, LastCheckpoint TEXT NOT NULL, Source TEXT NOT NULL, EndReason TEXT);
            CREATE TABLE IF NOT EXISTS Segments(Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(Id) ON DELETE CASCADE,
                StartTime TEXT NOT NULL, EndTime TEXT NOT NULL, State TEXT NOT NULL CHECK(State IN ('ACTIVE','IDLE','BACKGROUND','UNKNOWN')));
            CREATE INDEX IF NOT EXISTS IX_Sessions_Game ON Sessions(GameId, StartTime);
            CREATE INDEX IF NOT EXISTS IX_Segments_Session ON Segments(SessionId, StartTime);
            CREATE TABLE IF NOT EXISTS Settings(Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static string Time(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] parameters)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<Game> GetGames() => ReadJson<Game>("SELECT Json FROM Games ORDER BY Name");
    public IReadOnlyList<GameProcessRule> GetRules() => ReadJson<GameProcessRule>("SELECT Json FROM Rules");
    private List<T> ReadJson<T>(string sql)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return result;
    }
    public void SaveGame(Game game, IReadOnlyList<GameProcessRule> rules)
        => SaveGames([(game, rules)]);

    public void SaveGames(IReadOnlyList<(Game Game, IReadOnlyList<GameProcessRule> Rules)> games)
    {
        foreach (var (game, rules) in games)
        {
        if (string.IsNullOrWhiteSpace(game.Name)) throw new ArgumentException("游戏名称不能为空。");
        if (rules.Count == 0 || rules.Any(r => r.GameId != game.Id || string.IsNullOrWhiteSpace(r.ExecutableName)))
            throw new ArgumentException("至少需要一条包含可执行文件名的识别规则。");
        game.UpdatedAt = DateTimeOffset.UtcNow;
        }
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var (game, rules) in games)
        {
        Execute(c, tx, "INSERT INTO Games VALUES($id,$name,$json) ON CONFLICT(Id) DO UPDATE SET Name=$name,Json=$json",
            ("$id",game.Id),("$name",game.Name),("$json",JsonSerializer.Serialize(game)));
        Execute(c,tx,"DELETE FROM Rules WHERE GameId=$id",("$id",game.Id));
        foreach (var r in rules) Execute(c,tx,"INSERT INTO Rules VALUES($id,$game,$json)",("$id",r.Id),("$game",r.GameId),("$json",JsonSerializer.Serialize(r)));
        }
        tx.Commit();
    }
    public void DeleteGame(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Execute(c,tx,"DELETE FROM Games WHERE Id=$id",("$id",id)); tx.Commit();
    }
    public void SaveSessions(IEnumerable<GameSession> sessions) => SaveSessions(sessions,null);
    // Tracking segments are append-only except for the end of the final segment.
    // Callers advance these counts only after the entire transaction succeeds.
    public void SaveSessions(IEnumerable<GameSession> sessions,IReadOnlyDictionary<string,int>? persistedSegmentCounts)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var s in sessions)
        {
            Execute(c,tx,"""
                INSERT INTO Sessions VALUES($id,$game,$start,$end,$checkpoint,$source,$reason)
                ON CONFLICT(Id) DO UPDATE SET EndTime=$end,LastCheckpoint=$checkpoint,EndReason=$reason
                """,("$id",s.Id),("$game",s.GameId),("$start",Time(s.StartTime)),("$end",s.EndTime is {} end ? Time(end):null),
                ("$checkpoint",Time(s.LastCheckpoint)),("$source",s.Source),("$reason",s.EndReason));
            var start=persistedSegmentCounts is not null && persistedSegmentCounts.TryGetValue(s.Id,out var count)
                && count<=s.Segments.Count ? Math.Max(0,count-1) : 0;
            for(var i=start;i<s.Segments.Count;i++)
            {
                var seg=s.Segments[i];
                Execute(c,tx,"""
                    INSERT INTO Segments VALUES($id,$session,$start,$end,$state)
                    ON CONFLICT(Id) DO UPDATE SET EndTime=$end
                    """,("$id",seg.Id),("$session",s.Id),("$start",Time(seg.StartTime)),("$end",Time(seg.EndTime)),("$state",seg.State.ToString()));
            }
        }
        tx.Commit();
    }
    public IReadOnlyList<GameSession> GetSessions()
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var sessions = new Dictionary<string,GameSession>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "SELECT Id,GameId,StartTime,EndTime,LastCheckpoint,Source,EndReason FROM Sessions ORDER BY StartTime DESC";
            using var r = cmd.ExecuteReader();
            while(r.Read()) { var s = new GameSession { Id=r.GetString(0),GameId=r.GetString(1),StartTime=Parse(r.GetString(2)),
                EndTime=r.IsDBNull(3)?null:Parse(r.GetString(3)),LastCheckpoint=Parse(r.GetString(4)),Source=r.GetString(5),EndReason=r.IsDBNull(6)?null:r.GetString(6) }; sessions.Add(s.Id,s); }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "SELECT Id,SessionId,StartTime,EndTime,State FROM Segments ORDER BY StartTime";
            using var r = cmd.ExecuteReader();
            while(r.Read()) sessions[r.GetString(1)].Segments.Add(new ActivitySegment { Id=r.GetString(0),SessionId=r.GetString(1),StartTime=Parse(r.GetString(2)),EndTime=Parse(r.GetString(3)),State=Enum.Parse<ActivityState>(r.GetString(4)) });
        }
        tx.Commit(); return sessions.Values.ToList();
    }
    public int RecoverOpenSessions()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Sessions SET EndTime=LastCheckpoint,EndReason='RecoveredAtCheckpoint' WHERE EndTime IS NULL";
        return cmd.ExecuteNonQuery();
    }
    public TrackerSettings GetSettings()
    {
        var settings = ReadJson<TrackerSettings>("SELECT Json FROM Settings WHERE Id=1").FirstOrDefault() ?? new();
        settings.Validate(); return settings;
    }
    public void SaveSettings(TrackerSettings settings)
    {
        settings.Validate(); using var c = Open(); using var tx = c.BeginTransaction();
        Execute(c,tx,"INSERT INTO Settings VALUES(1,$json) ON CONFLICT(Id) DO UPDATE SET Json=$json",("$json",JsonSerializer.Serialize(settings))); tx.Commit();
    }
}
