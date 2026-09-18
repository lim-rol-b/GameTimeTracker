using System.Text.RegularExpressions;
using Microsoft.Win32;
using GameActivityTracker.Core.GamePresence;

namespace GameActivityTracker.Windows.GamePresence;

/// <summary>Optional local identity lookup. Never imports playtime, never drives session start/stop.</summary>
public sealed class SteamProvider : ISteamMetadataProvider
{
    private readonly List<(string Directory,Game Game)> _installed=[];
    public SteamProvider(ITrackerLog log)
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var root=key?.GetValue("SteamPath") as string;
            if(string.IsNullOrWhiteSpace(root))return;
            var libraries=new HashSet<string>(StringComparer.OrdinalIgnoreCase){root};
            var folders=Path.Combine(root,"steamapps","libraryfolders.vdf");
            if(File.Exists(folders))
                foreach(Match m in Regex.Matches(File.ReadAllText(folders),"\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\",@"\"));
            foreach(var library in libraries)
            {
                var steamapps=Path.Combine(library,"steamapps");if(!Directory.Exists(steamapps))continue;
                var scan=new SteamLibraryReader().Read(library,discoverExecutables:false);
                foreach(var warning in scan.Warnings) log.Write("Steam manifest: "+warning);
                foreach(var game in scan.Games)
                    _installed.Add((game.InstallDirectory,new Game{Name=game.Name,SteamAppId=game.AppId}));
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { log.Write("Optional Steam metadata unavailable; local process tracking remains available",ex); }
    }
    public string? InstallDirectoryForAppId(string? appId)=>string.IsNullOrWhiteSpace(appId)?null:_installed.FirstOrDefault(x=>x.Game.SteamAppId==appId).Directory;
    public string? InstallDirectoryForPath(string path)=>_installed.Where(x=>path.StartsWith(x.Directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.Directory.Length).FirstOrDefault().Directory;
    public IEnumerable<Game> GetInstalledGames()=>_installed.Select(g=>g.Game);
    public string? AppIdForPath(string? path)
    {
        if(path is null)return null;
        return _installed.Where(x=>path.StartsWith(x.Directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x=>x.Directory.Length).FirstOrDefault().Game?.SteamAppId;
    }
}
