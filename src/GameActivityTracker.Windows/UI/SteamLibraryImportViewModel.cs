using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GameActivityTracker.Core.GamePresence;
using Microsoft.Win32;

namespace GameActivityTracker.Windows.UI;

public sealed record SteamExecutableOption(string Path, string Label);

public sealed class SteamImportRow : ObservableObject
{
    private bool _selected;
    private SteamExecutableOption? _executable;
    public InstalledSteamGame Game { get; }
    public bool AlreadyImported { get; }
    public bool CanImport => !AlreadyImported;
    public ObservableCollection<SteamExecutableOption> Executables { get; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, CanImport && value); }
    public SteamExecutableOption? SelectedExecutable
    {
        get => _executable;
        set { if (Set(ref _executable, value)) Changed(nameof(Status)); }
    }
    public string Status => AlreadyImported ? "已添加，跳过"
        : SelectedExecutable is null ? Game.Warning ?? "请选择游戏的主 exe"
        : Game.Warning ?? "可导入";

    public SteamImportRow(InstalledSteamGame game, bool alreadyImported)
    {
        Game = game;
        AlreadyImported = alreadyImported;
        Executables = new(game.Executables.Select(path => new SteamExecutableOption(path, Path.GetRelativePath(game.InstallDirectory, path))));
        var recommended = SteamLibraryReader.RecommendedExecutable(game);
        if (recommended is not null) { SelectedExecutable = Executables.First(e => e.Path == recommended); IsSelected = true; }
    }

    public void UseExecutable(string path)
    {
        var option = Executables.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (option is null) { option = new(path, path); Executables.Add(option); }
        SelectedExecutable = option;
        IsSelected = true;
    }
}

public sealed record SteamGameImportSelection(string AppId, string Name, string ExecutablePath, string InstallDirectory);

public sealed class SteamLibraryImportViewModel
{
    public ObservableCollection<SteamImportRow> Games { get; }
    public string LibraryPath { get; }
    public string Summary { get; }
    public string Warnings { get; }
    public Visibility WarningsVisibility => Warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public ICommand SelectReady { get; }
    public ICommand ClearSelection { get; }
    public ICommand BrowseExecutable { get; }

    public SteamLibraryImportViewModel(SteamLibraryScan scan, IReadOnlyList<Game> existing)
    {
        LibraryPath = scan.SteamAppsDirectory;
        var ids = existing.Where(g => !string.IsNullOrWhiteSpace(g.SteamAppId)).Select(g => g.SteamAppId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Games = new(scan.Games.Select(game => new SteamImportRow(game, ids.Contains(game.AppId))));
        Summary = $"读取到 {Games.Count} 款游戏，其中 {Games.Count(g => g.AlreadyImported)} 款已添加。";
        Warnings = string.Join(Environment.NewLine, scan.Warnings.Take(20));
        if (scan.Warnings.Count > 20) Warnings += $"\n另有 {scan.Warnings.Count - 20} 个清单无法读取。";
        SelectReady = new RelayCommand(_ => { foreach (var row in Games) row.IsSelected = row.SelectedExecutable is not null; });
        ClearSelection = new RelayCommand(_ => { foreach (var row in Games) row.IsSelected = false; });
        BrowseExecutable = new RelayCommand(parameter =>
        {
            if (parameter is not SteamImportRow row || !row.CanImport) return;
            var dialog = new OpenFileDialog { Filter = "Windows executable (*.exe)|*.exe", CheckFileExists = true,
                Title = "选择 " + row.Game.Name + " 的游戏 exe" };
            if (Directory.Exists(row.Game.InstallDirectory)) dialog.InitialDirectory = row.Game.InstallDirectory;
            if (dialog.ShowDialog() == true) row.UseExecutable(dialog.FileName);
        });
    }

    public IReadOnlyList<SteamGameImportSelection> Selection()
    {
        var selected = Games.Where(g => g.IsSelected && g.CanImport).ToList();
        if (selected.Count == 0) throw new ArgumentException("请至少勾选一款尚未添加的游戏。");
        foreach (var row in selected)
        {
            if (row.SelectedExecutable is null) throw new ArgumentException($"请先为 {row.Game.Name} 选择主 exe。");
            if (!File.Exists(row.SelectedExecutable.Path)) throw new ArgumentException($"{row.Game.Name} 的 exe 已不存在，请重新选择。");
        }
        return selected.Select(row => new SteamGameImportSelection(row.Game.AppId, row.Game.Name, row.SelectedExecutable!.Path,row.Game.InstallDirectory)).ToList();
    }
}
