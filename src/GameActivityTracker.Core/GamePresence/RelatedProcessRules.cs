namespace GameActivityTracker.Core.GamePresence;
public static class RelatedProcessRules
{
    public static bool IsLauncher(string path)
    {
        var name=Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("PCL",StringComparison.OrdinalIgnoreCase) || name.Contains("launcher",StringComparison.OrdinalIgnoreCase) ||
            new[]{"steam","EADesktop","EALauncher","Origin","EpicGamesLauncher","Battle.net","UbisoftConnect","upc","start_protected_game","start_game","start"}.Contains(name,StringComparer.OrdinalIgnoreCase);
    }
    public static bool IsSharedHost(string path) => new[]{"steam","EADesktop","EALauncher","Origin","EpicGamesLauncher","Battle.net","UbisoftConnect","upc","java","javaw","dotnet","python","pythonw"}.Contains(Path.GetFileNameWithoutExtension(path),StringComparer.OrdinalIgnoreCase);
    public static bool IsSimple(GameProcessRule rule) => rule.Enabled && !string.IsNullOrWhiteSpace(rule.ExecutablePath)
        && string.IsNullOrWhiteSpace(rule.MinecraftRootDirectory) && string.IsNullOrWhiteSpace(rule.PathContains) && string.IsNullOrWhiteSpace(rule.CommandLineContains) && string.IsNullOrWhiteSpace(rule.SteamAppId);
    public static IReadOnlyList<GameProcessRule> Expand(Game game,IReadOnlyList<GameProcessRule> rules,string root,IEnumerable<string> executables)
    {
        if(!game.DetectRelatedExecutables || !rules.Any(IsSimple)) return rules;
        var prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;
        var candidates=executables.Where(p=>Path.GetFullPath(p).StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && !IsLauncher(p) && !IsSharedHost(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        // Only simple generated rules are expanded. Advanced conjunctions retain their meaning.
        var result=rules.Where(r=>!IsSimple(r)||!IsLauncher(r.ExecutableName)).ToList();
        foreach(var path in candidates)
        {
            if(rules.Any(r=>string.Equals(r.ExecutablePath,path,StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(){Id="related:"+game.Id+":"+path,GameId=game.Id,ExecutableName=Path.GetFileName(path),ExecutablePath=path,Priority=int.MinValue});
        }
        return result;
    }
}
