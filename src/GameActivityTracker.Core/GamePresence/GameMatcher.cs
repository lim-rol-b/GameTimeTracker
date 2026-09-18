namespace GameActivityTracker.Core.GamePresence;

public sealed record ProcessSnapshot(int Pid, string ExecutableName, string? FullPath,
    string? CommandLine, DateTimeOffset? StartedAt, string? SteamAppId = null);

public interface IGameRuleMatcher { string? Match(ProcessSnapshot process, IEnumerable<GameProcessRule> rules); }
public sealed class GameMatcher : IGameRuleMatcher
{
    public string? Match(ProcessSnapshot process, IEnumerable<GameProcessRule> rules) => rules
        .Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenByDescending(r => r.MinecraftRootDirectory?.Length ?? 0).ThenBy(r => r.Id, StringComparer.Ordinal)
        .FirstOrDefault(r => Matches(process, r))?.GameId;

    private static bool Matches(ProcessSnapshot p, GameProcessRule r)
    {
        // A rule is a conjunction, never a collection of independent guesses.
        if (string.IsNullOrWhiteSpace(r.ExecutableName)) return false;
        if (!string.Equals(p.ExecutableName, r.ExecutableName, StringComparison.OrdinalIgnoreCase)) return false;
        return EqualsIfSet(p.FullPath, r.ExecutablePath) && ContainsIfSet(p.FullPath, r.PathContains)
            && ContainsIfSet(p.CommandLine, r.CommandLineContains) && EqualsIfSet(p.SteamAppId, r.SteamAppId)
            && (string.IsNullOrWhiteSpace(r.MinecraftRootDirectory) || MinecraftProcessIdentity.Matches(p.CommandLine,r.MinecraftRootDirectory));
    }
    private static bool EqualsIfSet(string? value, string? filter) => string.IsNullOrWhiteSpace(filter) ||
        string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    private static bool ContainsIfSet(string? value, string? filter) => string.IsNullOrWhiteSpace(filter) ||
        value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
}
public sealed class GameStartedEventArgs(string gameId, IReadOnlySet<int> pids, DateTimeOffset detectedAt) : EventArgs
{
    public string GameId { get; } = gameId;
    public IReadOnlySet<int> ProcessIds { get; } = pids;
    public DateTimeOffset DetectedAt { get; } = detectedAt;
}
public sealed class GameStoppedEventArgs(string gameId, DateTimeOffset detectedAt) : EventArgs
{
    public string GameId { get; } = gameId;
    public DateTimeOffset DetectedAt { get; } = detectedAt;
}
public interface IGamePresenceProvider
{
    event EventHandler<GameStartedEventArgs>? GameStarted;
    event EventHandler<GameStoppedEventArgs>? GameStopped;
    IReadOnlyDictionary<string, HashSet<int>> RunningGames { get; }
    void Scan(DateTimeOffset now);
    void Reset();
}
// Steam is identity metadata only; local process observation owns session boundaries.
public interface ISteamMetadataProvider { IEnumerable<Game> GetInstalledGames(); }
