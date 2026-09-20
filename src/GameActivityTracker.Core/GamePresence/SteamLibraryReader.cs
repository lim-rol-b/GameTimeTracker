using System.Text.RegularExpressions;

namespace GameActivityTracker.Core.GamePresence;

public sealed record InstalledSteamGame(string AppId, string Name, string InstallDirectory,
    IReadOnlyList<string> Executables, string? Warning);
public sealed record SteamLibraryScan(string SteamAppsDirectory, IReadOnlyList<InstalledSteamGame> Games,
    IReadOnlyList<string> Warnings);

/// <summary>Reads a user-selected local library. Does not require Steam, an account, or a network call.</summary>
public sealed class SteamLibraryReader
{
    private static readonly HashSet<string> HelperDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "redist", "redistributables", "installers", "DirectX", "vcredist",
        "EasyAntiCheat", "BattlEye", "__Installer", "dotnet", "UnityCrashHandler", "CrashReportClient"
    };

    public SteamLibraryScan ReadLibraries(IEnumerable<string> roots)
    {
        var pending = new Queue<string>(roots);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new List<string>();
        var games = new Dictionary<string, InstalledSteamGame>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        while (pending.TryDequeue(out var root))
        {
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (!visited.Add(path) || !Directory.Exists(Path.Combine(path, "steamapps"))) continue;
                libraries.Add(path);
                var folders = Path.Combine(path, "steamapps", "libraryfolders.vdf");
                if (File.Exists(folders))
                {
                    try
                    {
                        if (new FileInfo(folders).Length > 2 * 1024 * 1024) throw new IOException("库清单过大。");
                        foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"(?:path|[0-9]+)\"\\s+\"([^\"]+)\""))
                        {
                            var candidate = match.Groups[1].Value.Replace(@"\\", @"\");
                            if (Path.IsPathFullyQualified(candidate)) pending.Enqueue(candidate);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add($"{folders}：{ex.Message}"); }
                }
                var scan = Read(path);
                warnings.AddRange(scan.Warnings.Select(w => $"{path}：{w}"));
                foreach (var game in scan.Games)
                    if (!games.TryGetValue(game.AppId, out var existing) || existing.Executables.Count == 0 && game.Executables.Count > 0)
                        games[game.AppId] = game;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { warnings.Add($"{root}：{ex.Message}"); }
        }
        return new(string.Join(Environment.NewLine, libraries), games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }

    private static string MatchName(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    public static bool MatchesInstallDirectory(string executable, string directory) =>
        MatchName(Path.GetFileNameWithoutExtension(executable)) == MatchName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));

    public static string? RecommendedExecutable(InstalledSteamGame game)
    {
        var matches = game.Executables.Where(path => MatchesInstallDirectory(path, game.InstallDirectory)).ToList();
        if (matches.Count == 1) return matches[0];
        return game.Executables.Count == 1 ? game.Executables[0] : null;
    }

    public SteamLibraryScan Read(string selectedDirectory, bool discoverExecutables = true)
    {
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        var steamApps = Path.Combine(selected, "steamapps");
        if (Path.GetFileName(selected).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) steamApps = selected;
        else if (Path.GetFileName(selected).Equals("common", StringComparison.OrdinalIgnoreCase) &&
                 Directory.GetParent(selected)?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
            steamApps = Directory.GetParent(selected)!.FullName;
        if (!Directory.Exists(steamApps))
            throw new ArgumentException("所选目录中没有 steamapps 文件夹。请选择 Steam 游戏库、Steam 安装目录或 steamapps 文件夹。");

        var common = Path.GetFullPath(Path.Combine(steamApps, "common"));
        var warnings = new List<string>();
        var games = new List<InstalledSteamGame>();
        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(manifest).Length > 2 * 1024 * 1024)
                    throw new FormatException("清单文件过大，已跳过。");
                var fields = ReadAppState(File.ReadAllText(manifest));
                var appId = fields.GetValueOrDefault("appid", "");
                var name = fields.GetValueOrDefault("name", "").Trim();
                var install = fields.GetValueOrDefault("installdir", "").Trim();
                if (!uint.TryParse(appId, out var numericId) || numericId == 0 || name.Length == 0 || install.Length == 0)
                    throw new FormatException("缺少有效的 appid、name 或 installdir。");
                appId = numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var directory = Path.GetFullPath(Path.Combine(common, install));
                if (!directory.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("安装目录不在 steamapps/common 中。");
                if (!appIds.Add(appId)) continue;
                string? warning = null;
                IReadOnlyList<string> executables = [];
                if (!Directory.Exists(directory)) warning = "安装目录不存在，可能尚未下载完成。";
                else if (discoverExecutables) executables = FindExecutables(directory, out warning);
                games.Add(new(appId, name, directory, executables, warning));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                warnings.Add($"{Path.GetFileName(manifest)}：{ex.Message}");
            }
        }
        return new(steamApps, games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }

    public static IReadOnlyList<string> FindExecutables(string directory, out string? warning)
    {
        warning = null;
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        var visited = 0;
        while (pending.TryPop(out var item))
        {
            if (++visited > 10000) { warning = "目录较大，扫描已截断；可手动选择可执行文件。"; break; }
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(item.Path, "*", options))
                {
                    if (!Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (new[] { "easyanticheat", "beservice", "unins", "crashreport", "crashpad", "unitycrash", "vc_redist", "vcredist", "dxsetup", "dotnet", "setup", "installer", "steamwebhelper", "unrealcrash", "ue4prereq", "ue5prereq", "unityhub", "unrealcefsubprocess", "unrealversionselector", "unitybugreporter", "bugreporter" }
                        .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                    if (new[] { "Unity", "UnityPlayer", "UnrealEditor", "UE4Editor", "ShaderCompileWorker", "UnrealLightmass", "UnrealPak", "UnityPackageManager", "UnityShaderCompiler", "UnityAutoQuit", "UnrealFrontend" }
                        .Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (RelatedProcessRules.IsLauncher(file) || RelatedProcessRules.IsSharedHost(file)) continue;
                    result.Add(file);
                    if (result.Count >= 1000) { warning = "可执行文件较多，列表已截断；可手动选择可执行文件。"; return Rank(result, directory); }
                }
                foreach (var child in Directory.EnumerateDirectories(item.Path, "*", options))
                {
                    if (HelperDirectories.Contains(Path.GetFileName(child))) continue;
                    if (item.Depth >= 16) { warning ??= "部分目录过深，未扫描；可手动选择可执行文件。"; continue; }
                    pending.Push((child, item.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = "部分目录无法读取；可手动选择可执行文件。";
            }
        }
        if (result.Count == 0) warning ??= "未找到游戏主程序，请手动选择；未完成下载或非 Windows 游戏可能没有可执行文件。";
        return Rank(result, directory);
    }

    private static IReadOnlyList<string> Rank(IEnumerable<string> paths, string directory)
    {
        return paths.OrderByDescending(path => MatchesInstallDirectory(path, directory))
            .ThenBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Valve KeyValues: keep only scalar fields directly inside AppState; nested UserConfig fields
    // must not override the game's identity. Quoted strings, comments and escaped quotes are supported.
    private static Dictionary<string, string> ReadAppState(string text)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Select(m => m.Value).Where(token => !token.StartsWith("//", StringComparison.Ordinal)).ToArray();
        var index = 0;
        string Take() => index < tokens.Length ? tokens[index++] : throw new FormatException("清单内容不完整。");
        string Decode(string value) => value.StartsWith('"') && value.EndsWith('"')
            ? Regex.Replace(value[1..^1], "\\\\([\\\\\"])", "$1") : value;
        Dictionary<string, string> Block(int depth)
        {
            if (depth > 32) throw new FormatException("清单嵌套层数过多。");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Take();
                if (key == "}") return values;
                if (key == "{") throw new FormatException("清单字段名称缺失。");
                var value = Take();
                if (value == "{") { Block(depth + 1); continue; }
                if (value == "}") throw new FormatException("清单字段值缺失。");
                values[Decode(key)] = Decode(value);
            }
        }
        while (index < tokens.Length)
        {
            var key = Decode(Take());
            var value = Take();
            if (value != "{") continue;
            var block = Block(0);
            if (key.Equals("AppState", StringComparison.OrdinalIgnoreCase)) return block;
        }
        throw new FormatException("未找到 AppState 游戏信息。");
    }
}
