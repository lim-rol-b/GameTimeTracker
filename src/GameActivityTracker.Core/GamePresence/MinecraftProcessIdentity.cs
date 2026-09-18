using System.Text.RegularExpressions;
namespace GameActivityTracker.Core.GamePresence;
public static class MinecraftProcessIdentity
{
    private static readonly HashSet<string> EntryPoints=new(StringComparer.Ordinal)
    {
        "net.minecraft.client.main.Main", "net.minecraft.launchwrapper.Launch",
        "net.fabricmc.loader.impl.launch.knot.KnotClient", "net.fabricmc.loader.launch.knot.KnotClient",
        "org.quiltmc.loader.impl.launch.knot.KnotClient", "cpw.mods.bootstraplauncher.BootstrapLauncher",
        "cpw.mods.modlauncher.Launcher", "net.minecraft.client.Minecraft"
    };
    private static string? Normalize(string path)
    {
        path=path.Replace('/', '\\').TrimEnd('\\');
        if(!Regex.IsMatch(path,@"^[A-Za-z]:\\") && !path.StartsWith(@"\\")) return null;
        var parts=path.Split('\\');
        if(parts.Any(p=>p is "." or "..")) return null;
        return path;
    }
    public static bool Matches(string? command,string root)
    {
        if(string.IsNullOrWhiteSpace(command)) return false;
        var args=Regex.Matches(command,"(?:[^\\s\"]|\"[^\"]*\")+").Select(m=>m.Value.Replace("\"","")).ToArray();
        if(!args.Any(EntryPoints.Contains)) return false;
        string? gameDir=null;
        for(var i=0;i<args.Length;i++)
        {
            if(args[i]=="--gameDir" && i+1<args.Length) gameDir=args[++i];
            else if(args[i].StartsWith("--gameDir=",StringComparison.Ordinal)) gameDir=args[i][10..];
        }
        var directory=gameDir is null?null:Normalize(gameDir);var expected=Normalize(root);
        if(directory is null || expected is null) return false;
        if(directory.Equals(expected,StringComparison.OrdinalIgnoreCase)) return true;
        var versions=expected+@"\versions\";
        // PCL's isolated versions are immediate children of .minecraft/versions.
        return directory.StartsWith(versions,StringComparison.OrdinalIgnoreCase) && !directory[versions.Length..].Contains('\\');
    }
}
