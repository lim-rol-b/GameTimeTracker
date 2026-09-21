using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameActivityTracker.Core;
using GameActivityTracker.Data;

namespace GameActivityTracker.Windows.Services;

public sealed record LiveGame(string GameId, ActivityState State, double ActiveSeconds, double RunningSeconds, double InputIdleSeconds);

/// <summary>Live games plus the engine-reported error from a single status round trip.</summary>
public sealed record LiveSnapshot(IReadOnlyList<LiveGame> Games, string? Error);

/// <summary>
/// Viewer facade over the Rust lightweight engine. Tracking is owned by the native
/// engine; this client reads the shared SQLite database and the engine's loopback
/// control channel so the WPF interface can show live state and forward commands.
/// The public surface is unchanged so the existing UI needs no edits.
/// </summary>
public sealed class TrackingService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly TrackerDatabase _database;
    private readonly ITrackerLog _log;
    private readonly string _dataDirectory;
    private readonly object _gate = new();
    private (int Port, string Token)? _handshake;
    private DateTime _handshakeStamp;
    private long _handshakeLength = -1;
    private bool _disposed;

    public TrackingService(TrackerDatabase database, ITrackerLog log)
    {
        _database = database;
        _log = log;
        _dataDirectory = Path.GetDirectoryName(database.DatabasePath) ?? AppContext.BaseDirectory;
    }

    public string? Error => QueryStatus()?.Error ?? "后台记录引擎不可用";

    /// <summary>Start the native engine when it is not already running (best effort).</summary>
    public void Start()
    {
        if (_disposed) return;
        if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)) return;
        if (IsEngineReachable()) return;
        // A leftover handshake from a crashed engine must not block a fresh start.
        try
        {
            var control = Path.Combine(_dataDirectory, "control.json");
            if (File.Exists(control)) File.Delete(control);
        }
        catch (Exception ex) { _log.Write("Unable to clear a stale control handshake", ex); }
        try
        {
            var candidate = FindEngine();
            if (candidate is null)
            {
                _log.Write("Native engine gat.exe not found next to the viewer or under native/target; running in viewer mode");
                return;
            }
            var startInfo = new ProcessStartInfo(candidate, "--background")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(candidate) ?? ViewerDirectory(),
            };
            // Tell the engine which viewer to open from its tray and which icon to use.
            if (ViewerExecutable() is { } viewer) startInfo.Environment["GAT_UI_COMMAND"] = viewer;
            var icon = FindViewerIcon();
            if (icon is not null) startInfo.Environment["GAT_ICON_PATH"] = icon;
            var process = Process.Start(startInfo);
            _log.Write(process is null ? "Native engine start returned no process" : $"Started native engine (pid {process.Id}) from {candidate}");
        }
        catch (Exception ex) { _log.Write("Unable to start native engine", ex); }
    }

    /// <summary>Directory of the real executable (package root), not the managed runtime/ folder.</summary>
    private static string ViewerDirectory()
    {
        if (ViewerExecutable() is { } process && Path.GetDirectoryName(process) is { Length: > 0 } directory) return directory;
        return AppContext.BaseDirectory;
    }

    private static string? ViewerExecutable()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process)) return null;
        return Path.GetFileName(process).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? null : process;
    }

    private static string? FindEngine()
    {
        foreach (var directory in new[] { ViewerDirectory(), AppContext.BaseDirectory })
        {
            var direct = Path.Combine(directory, "gat.exe");
            if (File.Exists(direct)) return direct;
        }
        var configured = Environment.GetEnvironmentVariable("GAT_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var relative in new[]
        {
            "native/target/release/gat.exe",
            "native/target/debug/gat.exe",
            "artifacts/native/gat.exe",
        })
        {
            var found = FindUpward(relative);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindViewerIcon()
    {
        foreach (var directory in new[] { ViewerDirectory(), AppContext.BaseDirectory })
        {
            var direct = Path.Combine(directory, "App.ico");
            if (File.Exists(direct)) return direct;
        }
        return FindUpward("src/GameActivityTracker.Windows/Assets/App.ico");
    }

    private static string? FindUpward(string relativePath)
    {
        foreach (var start in new[] { ViewerDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private bool IsEngineReachable()
    {
        var request = BuildRequest("ping");
        if (request is null) return false;
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", request.Value.Port).Wait(TimeSpan.FromSeconds(1))) return false;
            using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes(request.Value.Json + "\n");
            stream.Write(payload, 0, payload.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return false;
            using var response = JsonDocument.Parse(line);
            return response.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    public IReadOnlyList<LiveGame> Snapshot()
    {
        var status = QueryStatus();
        if (status?.Games is null) return Array.Empty<LiveGame>();
        var result = new List<LiveGame>(status.Games.Length);
        foreach (var game in status.Games)
            result.Add(new LiveGame(game.GameId, ParseState(game.State), game.ActiveSeconds, game.RunningSeconds, 0));
        return result;
    }

    /// <summary>Fetch live state off the UI thread so periodic refreshes never block rendering.</summary>
    public async Task<LiveSnapshot> PollAsync()
    {
        var status = await QueryStatusAsync().ConfigureAwait(false);
        if (status?.Games is null) return new LiveSnapshot(Array.Empty<LiveGame>(), status?.Error ?? "后台记录引擎不可用");
        var result = new List<LiveGame>(status.Games.Length);
        foreach (var game in status.Games)
            result.Add(new LiveGame(game.GameId, ParseState(game.State), game.ActiveSeconds, game.RunningSeconds, 0));
        return new LiveSnapshot(result, status.Error);
    }

    public void Reload() => SendCommand("reload");
    public void Suspend(string reason) => SendCommand("suspend");
    public void Resume() => SendCommand("resume");
    // The native engine receives session lock/unlock notifications itself.
    public void SetLocked(bool locked) { }
    public void DeleteGame(string gameId)
    {
        _database.DeleteGame(gameId);
        SendCommand("reload");
    }
    public void Dispose() => _disposed = true;

    private static ActivityState ParseState(string? value) => value?.ToUpperInvariant() switch
    {
        "ACTIVE" => ActivityState.ACTIVE,
        "IDLE" => ActivityState.IDLE,
        "BACKGROUND" => ActivityState.BACKGROUND,
        _ => ActivityState.UNKNOWN,
    };

    private void SendCommand(string command)
    {
        var request = BuildRequest(command);
        if (request is null) return;
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", request.Value.Port).Wait(TimeSpan.FromSeconds(1))) return;
            using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes(request.Value.Json + "\n");
            stream.Write(payload, 0, payload.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _ = reader.ReadLine();
        }
        catch (Exception ex) { _log.Write("Engine command failed: " + command, ex); InvalidateHandshake(); }
    }

    private EngineStatus? QueryStatus()
    {
        var request = BuildRequest("status");
        if (request is null) return null;
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", request.Value.Port).Wait(TimeSpan.FromSeconds(1))) { InvalidateHandshake(); return null; }
            using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes(request.Value.Json + "\n");
            stream.Write(payload, 0, payload.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return null;
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            if (!response.RootElement.TryGetProperty("data", out var data)) return null;
            return data.Deserialize<EngineStatus>(JsonOptions);
        }
        catch (Exception ex) { _log.Write("Engine status query failed", ex); InvalidateHandshake(); return null; }
    }

    /// <summary>Same as <see cref="QueryStatus"/> but awaits the socket so the UI thread never blocks.</summary>
    private async Task<EngineStatus?> QueryStatusAsync()
    {
        var request = BuildRequest("status");
        if (request is null) return null;
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync("127.0.0.1", request.Value.Port, timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes(request.Value.Json + "\n");
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return null;
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            if (!response.RootElement.TryGetProperty("data", out var data)) return null;
            return data.Deserialize<EngineStatus>(JsonOptions);
        }
        catch (Exception ex) { _log.Write("Engine status query failed", ex); InvalidateHandshake(); return null; }
    }

    /// <summary>Drop the cached handshake so a restarted engine's new port/token are picked up.</summary>
    private void InvalidateHandshake()
    {
        lock (_gate) { _handshake = null; _handshakeStamp = default; _handshakeLength = -1; }
    }

    private (int Port, string Json)? BuildRequest(string command)
    {
        try
        {
            var path = Path.Combine(_dataDirectory, "control.json");
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            int port;
            string token;
            lock (_gate)
            {
                if (_handshake is null || info.LastWriteTimeUtc != _handshakeStamp || info.Length != _handshakeLength)
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    port = document.RootElement.GetProperty("port").GetInt32();
                    token = document.RootElement.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() ?? "" : "";
                    _handshake = (port, token);
                    _handshakeStamp = info.LastWriteTimeUtc;
                    _handshakeLength = info.Length;
                }
                else
                {
                    port = _handshake.Value.Port;
                    token = _handshake.Value.Token;
                }
            }
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = token, ["command"] = command });
            return (port, json);
        }
        catch (Exception ex) { _log.Write("Engine handshake unavailable", ex); return null; }
    }

    private sealed class EngineStatus
    {
        [JsonPropertyName("running")] public bool Running { get; set; }
        [JsonPropertyName("suspended")] public bool Suspended { get; set; }
        [JsonPropertyName("locked")] public bool Locked { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("games")] public EngineGame[]? Games { get; set; }
    }

    private sealed class EngineGame
    {
        [JsonPropertyName("gameId")] public string GameId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("activeSeconds")] public double ActiveSeconds { get; set; }
        [JsonPropertyName("runningSeconds")] public double RunningSeconds { get; set; }
    }
}
