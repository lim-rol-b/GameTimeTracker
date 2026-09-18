using Microsoft.Win32;
using GameActivityTracker.Core.GamePresence;

namespace GameActivityTracker.Windows.GamePresence;

public static class SteamLibraryDiscovery
{
    public static SteamLibraryScan Scan()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var key = registry.OpenSubKey(@"Software\Valve\Steam");
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (key?.GetValue(name) is string path && !string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { warnings.Add("无法读取 Steam 安装位置：" + ex.Message); }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) roots.Add(Path.Combine(path, "Steam"));
        }
        var scan = new SteamLibraryReader().ReadLibraries(roots);
        return scan with { Warnings = warnings.Concat(scan.Warnings).ToList() };
    }
}
