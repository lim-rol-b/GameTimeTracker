using GameActivityTracker.Core.GamePresence;
using Xunit;

namespace GameActivityTracker.Tests;

public sealed class SteamLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SteamScanTests-" + Guid.NewGuid());
    private string Library(string name) { var path=Path.Combine(_root,name); Directory.CreateDirectory(Path.Combine(path,"steamapps","common")); return path; }
    private static string Game(string library,string id,string folder,params string[] files)
    {
        File.WriteAllText(Path.Combine(library,"steamapps",$"appmanifest_{id}.acf"),$"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{folder}\" \"installdir\" \"{folder}\" }}");
        var directory=Path.Combine(library,"steamapps","common",folder);
        Directory.CreateDirectory(directory);
        foreach(var file in files) { var path=Path.Combine(directory,file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,""); }
        return directory;
    }
    [Fact] public void RecommendsFolderMatchAndKeepsRealEngineGame()
    {
        var library=Library("Steam");
        Game(library,"1","My Game","other.exe","My-Game.exe","unins000.exe","UnityCrashHandler64.exe","UE4PrereqSetup_x64.exe","Engine/Binaries/Win64/UnrealEditor.exe","Engine/Binaries/Win64/MyGame-Win64-Shipping.exe");
        var game=Assert.Single(new SteamLibraryReader().Read(library).Games);
        Assert.Equal(3,game.Executables.Count);
        Assert.Equal("My-Game.exe",Path.GetFileName(game.Executables[0]));
        Assert.Equal(game.Executables[0],SteamLibraryReader.RecommendedExecutable(game));
        Assert.Contains(game.Executables,p=>p.EndsWith("MyGame-Win64-Shipping.exe"));
    }
    [Fact] public void AmbiguousNamesRequireSelectionAndSingleCandidateIsRecommended()
    {
        var library=Library("Steam");
        Game(library,"1","Game","x64/Game.exe","x86/Game.exe");
        Game(library,"2","Another","Another-Win64-Shipping.exe");
        var games=new SteamLibraryReader().Read(library).Games;
        Assert.Null(SteamLibraryReader.RecommendedExecutable(games.Single(g=>g.AppId=="1")));
        Assert.NotNull(SteamLibraryReader.RecommendedExecutable(games.Single(g=>g.AppId=="2")));
    }
    [Fact] public void DiscoversRegisteredLibrariesDeduplicatesAndSurvivesBadManifest()
    {
        var first=Library("Steam");var second=Library("OtherLibrary");
        Game(first,"1","First","First.exe");Game(second,"1","Duplicate","Duplicate.exe");Game(second,"2","Second","Second.exe");
        var escaped=second.Replace("\\","\\\\");
        File.WriteAllText(Path.Combine(first,"steamapps","libraryfolders.vdf"),$"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{escaped}\" }} \"1\" \"{escaped}\" }}");
        File.WriteAllText(Path.Combine(second,"steamapps","appmanifest_bad.acf"),"broken");
        var scan=new SteamLibraryReader().ReadLibraries(new[]{first,first,Path.Combine(_root,"missing")});
        Assert.Equal(2,scan.Games.Count);Assert.Single(scan.Warnings);Assert.Contains(second,scan.SteamAppsDirectory);
    }
    public void Dispose() { if(Directory.Exists(_root))Directory.Delete(_root,true); }
}
