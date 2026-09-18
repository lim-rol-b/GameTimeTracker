using System.Diagnostics;
using System.Management;
using GameActivityTracker.Core.GamePresence;

namespace GameActivityTracker.Windows.GamePresence;

public sealed class ProcessProvider(Func<IReadOnlyList<GameProcessRule>> rules, Func<IReadOnlyList<Game>> games, ITrackerLog log) : IGamePresenceProvider
{
    private readonly GameMatcher _matcher = new();
    private readonly SteamProvider _steam = new(log);
    private Dictionary<string, HashSet<int>> _running = [];
    private readonly Dictionary<int,(DateTimeOffset Start,string Game)> _known = [];
    private string _ruleFingerprint="";
    private IReadOnlyList<GameProcessRule> _effectiveRules=[];
    private DateTimeOffset _lastExpansion=DateTimeOffset.MinValue;
    private readonly HashSet<int> _unreadable=[];
    private Task<IReadOnlyList<GameProcessRule>>? _expansionTask;
    private string _expansionFingerprint="";
    public event EventHandler<GameStartedEventArgs>? GameStarted;
    public event EventHandler<GameStoppedEventArgs>? GameStopped;
    public IReadOnlyDictionary<string, HashSet<int>> RunningGames => _running;
    public void Scan(DateTimeOffset now)
    {
        var currentRules=rules().Where(r=>r.Enabled).ToList();
        var currentGames=games().Select(g=>g with{}).ToList();
        var fingerprint=System.Text.Json.JsonSerializer.Serialize(new { Rules=currentRules, Games=currentGames });
        if(fingerprint!=_ruleFingerprint)
        {
            _known.Clear();_unreadable.Clear();_ruleFingerprint=fingerprint;_lastExpansion=DateTimeOffset.MinValue;
            _effectiveRules=currentRules.Where(r=>!RelatedProcessRules.IsSimple(r) || !RelatedProcessRules.IsLauncher(r.ExecutableName)
                || currentGames.FirstOrDefault(g=>g.Id==r.GameId)?.DetectRelatedExecutables!=true).ToList();
        }
        if(_expansionTask is { IsCompleted:true })
        {
            try
            {
                var expanded=_expansionTask.GetAwaiter().GetResult();
                if(_expansionFingerprint==fingerprint) _effectiveRules=expanded;
            }
            catch(Exception ex) { log.Write("Related executable discovery failed; retaining explicit rules",ex); }
            _expansionTask=null;
        }
        if(_expansionTask is null && now-_lastExpansion>TimeSpan.FromMinutes(2))
        {
            _lastExpansion=now;_expansionFingerprint=fingerprint;
            var copied=currentRules.Select(r=>r with{}).ToList();
            _expansionTask=Task.Run(()=>ExpandRules(copied,currentGames));
        }
        currentRules=_effectiveRules.ToList();
        var names=currentRules.Select(r=>Path.GetFileNameWithoutExtension(r.ExecutableName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string,HashSet<int>>();
        var seen = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                if (!names.Contains(process.ProcessName)) continue;
                int pid=process.Id; seen.Add(pid);
                var image=ProcessImageReader.Read(pid);
                DateTimeOffset? start=image.Started; string? path=image.Path; string? command=null;
                if(start is null) try { start=new DateTimeOffset(process.StartTime.ToUniversalTime()); } catch (System.ComponentModel.Win32Exception) { }
                var candidates=currentRules.Where(r=>string.Equals(Path.GetFileNameWithoutExtension(r.ExecutableName),process.ProcessName,StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Any(r=>!string.IsNullOrWhiteSpace(r.MinecraftRootDirectory) || !string.IsNullOrWhiteSpace(r.CommandLineContains) || !string.IsNullOrWhiteSpace(r.SteamAppId)) || path is null)
                {
                    try
                    {
                        using var search=new ManagementObjectSearcher(new ManagementScope(),new ObjectQuery($"SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId={pid}"),new System.Management.EnumerationOptions{Timeout=TimeSpan.FromSeconds(2)});
                        using var results=search.Get();
                        foreach (ManagementObject row in results) using(row) { path ??= row["ExecutablePath"] as string; command=row["CommandLine"] as string; }
                    }
                    catch (ManagementException) { }
                    catch (UnauthorizedAccessException) { }
                }
                var snapshot=new ProcessSnapshot(pid,process.ProcessName+".exe",path,command,start,_steam.AppIdForPath(path));
                var game=_matcher.Match(snapshot,candidates);
                // A previously verified process may temporarily become inaccessible. Never reuse a recycled PID.
                if (game is null && path is null && start is {} began && _known.TryGetValue(pid,out var known) && known.Start==began && candidates.Any(r=>r.GameId==known.Game)) game=known.Game;
                if (game is null)
                {
                    if(path is null && _unreadable.Add(pid)) log.Write($"Unable to verify process path: {process.ProcessName} ({pid}); bind the real game process or check process permissions.");
                    continue;
                }
                if (start is {} time) _known[pid]=(time,game);
                if (!next.TryGetValue(game,out var ids)) next[game]=ids=[];
                ids.Add(pid);
            }
            catch (InvalidOperationException) { } // Exited between enumeration and metadata read.
            catch (System.ComponentModel.Win32Exception) { }
        }
        _unreadable.RemoveWhere(pid=>!seen.Contains(pid));
        foreach(var pid in _known.Keys.Where(pid=>!seen.Contains(pid)).ToArray()) _known.Remove(pid);
        var previous=_running; _running=next;
        foreach(var game in previous.Keys.Except(next.Keys)) { log.Write($"Game stopped: {game}"); GameStopped?.Invoke(this,new(game,now)); }
        foreach(var game in next.Keys.Except(previous.Keys)) { log.Write($"Game detected: {game}"); GameStarted?.Invoke(this,new(game,next[game],now)); }
    }
    private IReadOnlyList<GameProcessRule> ExpandRules(IReadOnlyList<GameProcessRule> configured,IReadOnlyList<Game> currentGames)
    {
        var result=new List<GameProcessRule>();
        foreach(var group in configured.GroupBy(r=>r.GameId))
        {
            var own=group.ToList();var game=currentGames.FirstOrDefault(g=>g.Id==group.Key);
            // Java versions may live outside the launcher folder: identify Minecraft by
            // its client entry point plus the exact gameDir, not javaw.exe alone.
            var minecraft=game?.MinecraftDirectory;
            if(game is not null && game.DetectRelatedExecutables && string.IsNullOrWhiteSpace(minecraft))
            {
                var launcher=own.FirstOrDefault(r=>RelatedProcessRules.IsSimple(r) && RelatedProcessRules.IsLauncher(r.ExecutableName));
                var parent=launcher is null?null:Path.GetDirectoryName(launcher.ExecutablePath);
                if(parent is not null && Directory.Exists(Path.Combine(parent,".minecraft"))) minecraft=Path.Combine(parent,".minecraft");
            }
            if(game is not null && game.DetectRelatedExecutables && !string.IsNullOrWhiteSpace(minecraft))
            {
                result.AddRange(own.Where(r=>!RelatedProcessRules.IsSimple(r)||!RelatedProcessRules.IsLauncher(r.ExecutableName)));
                foreach(var name in new[]{"javaw.exe","java.exe"})
                    result.Add(new(){Id="minecraft:"+game.Id+":"+name,GameId=game.Id,ExecutableName=name,MinecraftRootDirectory=minecraft,Priority=int.MinValue});
                continue;
            }
            if(game is null || !game.DetectRelatedExecutables || !own.Any(RelatedProcessRules.IsSimple)) { result.AddRange(own);continue; }
            try
            {
                var primary=own.First(RelatedProcessRules.IsSimple).ExecutablePath!;
                var root=game.InstallDirectory ?? _steam.InstallDirectoryForAppId(game.SteamAppId) ?? _steam.InstallDirectoryForPath(primary);
                // Never scan a shared platform/runtime directory to guess which game it launched.
                if(root is null && RelatedProcessRules.IsSharedHost(primary))
                {
                    result.AddRange(own.Where(r=>!RelatedProcessRules.IsSimple(r)||!RelatedProcessRules.IsLauncher(r.ExecutableName)));
                    log.Write($"{game.Name}: shared launcher requires a game EXE or Minecraft directory; launcher runtime is excluded.");continue;
                }
                root ??= Path.GetDirectoryName(primary);
                if(root is null || !Directory.Exists(root)) { result.AddRange(own);continue; }
                var paths=SteamLibraryReader.FindExecutables(root,out var warning);
                result.AddRange(RelatedProcessRules.Expand(game,own,root,paths));
                if(warning is not null) log.Write($"Related executables for {game.Name}: {warning}");
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            { log.Write($"Related executable discovery failed for {game.Name}",ex);result.AddRange(own); }
        }
        return result;
    }
    public void Reset() { _running=[]; _known.Clear(); }
}
