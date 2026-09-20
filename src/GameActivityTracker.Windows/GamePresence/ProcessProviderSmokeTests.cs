using System.Diagnostics;

namespace GameActivityTracker.Windows.GamePresence;

// Runs only under --smoke-test on Windows; does not modify user rules or sessions.
internal static class ProcessProviderSmokeTests
{
    public static void Run(ITrackerLog log)
    {
        using var process=Process.GetCurrentProcess();
        var path=Environment.ProcessPath!;
        var game=new Game { Id="smoke",Name="Smoke",ExecutablePath=path,DetectRelatedExecutables=false };
        var rule=new GameProcessRule { GameId=game.Id,ExecutableName=process.ProcessName+".exe",ExecutablePath=path };
        var provider=new ProcessProvider(()=>new[]{rule},()=>new[]{game},log);
        var started=0;var stopped=0;
        provider.GameStarted+=(_,_)=>started++;
        provider.GameStopped+=(_,_)=>stopped++;
        var now=DateTimeOffset.UtcNow;
        provider.Scan(now);
        provider.Scan(now.AddSeconds(1));
        if(started!=1 || !provider.RunningGames.TryGetValue(game.Id,out var ids) || !ids.Contains(process.Id))
            throw new InvalidOperationException("Repeated scan did not retain the matched process.");
        rule.Enabled=false;
        provider.Scan(now.AddSeconds(2));
        if(stopped!=1 || provider.RunningGames.Count!=0)
            throw new InvalidOperationException("Disabling a rule did not invalidate the cached match.");
        rule.Enabled=true;
        provider.Scan(now.AddSeconds(3));
        if(started!=2) throw new InvalidOperationException("Re-enabling a rule did not restore matching.");
        rule.ExecutablePath=Path.Combine(Path.GetDirectoryName(path)!,"missing-smoke-game.exe");
        provider.Scan(now.AddSeconds(4));
        if(stopped!=2 || provider.RunningGames.Count!=0)
            throw new InvalidOperationException("Changing a rule path retained a stale match.");
    }
}
