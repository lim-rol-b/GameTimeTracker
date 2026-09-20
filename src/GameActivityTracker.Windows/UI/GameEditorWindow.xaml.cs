using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace GameActivityTracker.Windows.UI;
public partial class GameEditorWindow : Window
{
    public Game Game { get; }
    public ObservableCollection<GameProcessRule> Rules { get; }
    public GameEditorWindow(Game? game,IReadOnlyList<GameProcessRule> rules)
    {
        Game=game is null?new():game with{}; Rules=new(rules.Select(r=>r with{}));
        InitializeComponent();DataContext=this;
    }
    private void Browse(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="可执行文件 (*.exe)|*.exe",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        Game.ExecutablePath=dialog.FileName;Game.Executable=Path.GetFileName(dialog.FileName);
        if(string.IsNullOrWhiteSpace(Game.Name))Game.Name=Path.GetFileNameWithoutExtension(dialog.FileName);
        if(Rules.Count==0)Rules.Add(new(){GameId=Game.Id,ExecutableName=Game.Executable,ExecutablePath=Game.ExecutablePath});
        else { Rules[0]=Rules[0] with { ExecutableName=Game.Executable,ExecutablePath=Game.ExecutablePath }; }
        DataContext=null;DataContext=this;
    }
    private void BrowseMinecraft(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog{Title="选择 .minecraft 或某个版本的游戏目录"};
        if(dialog.ShowDialog(this)!=true) return;
        Game.MinecraftDirectory=dialog.FolderName;Game.DetectRelatedExecutables=true;
        if(string.IsNullOrWhiteSpace(Game.Name)) Game.Name="Minecraft";
        foreach(var rule in Rules.Where(r=>!string.IsNullOrWhiteSpace(r.MinecraftRootDirectory)).ToList()) Rules.Remove(rule);
        foreach(var name in new[]{"javaw.exe","java.exe"})
            if(!Rules.Any(r=>r.ExecutableName==name && r.MinecraftRootDirectory==dialog.FolderName))
                Rules.Add(new(){GameId=Game.Id,ExecutableName=name,MinecraftRootDirectory=dialog.FolderName});
        DataContext=null;DataContext=this;
    }
    private void AddRule(object sender,RoutedEventArgs e)=>Rules.Add(new(){GameId=Game.Id,ExecutableName=Game.Executable});
    private void RemoveRule(object sender,RoutedEventArgs e){if(RulesGrid.SelectedItem is GameProcessRule rule)Rules.Remove(rule);}
    private void Save(object sender,RoutedEventArgs e)
    {
        if(!RulesGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!RulesGrid.CommitEdit(DataGridEditingUnit.Row,true))return;
        Game.MinecraftDirectory=Game.MinecraftDirectory?.Trim();
        if(!string.IsNullOrWhiteSpace(Game.MinecraftDirectory) && !Directory.Exists(Game.MinecraftDirectory))
        { MessageBox.Show(this,"Minecraft 游戏目录不存在，请重新选择。");return; }
        Game.Name=Game.Name.Trim();Game.ExecutablePath=Game.ExecutablePath.Trim();Game.Executable=Path.GetFileName(Game.ExecutablePath);
        if(string.IsNullOrWhiteSpace(Game.Name)||Rules.Count==0||Rules.Any(r=>string.IsNullOrWhiteSpace(r.ExecutableName)))
        {MessageBox.Show(this,"请输入名称，并至少添加一条包含可执行文件名的规则。");return;}
        if(!string.IsNullOrWhiteSpace(Game.SteamAppId)&&(!long.TryParse(Game.SteamAppId,out var appId)||appId<=0))
        {MessageBox.Show(this,"Steam 应用编号应为正整数或留空。");return;}
        foreach(var r in Rules)
        {
            r.ExecutableName=Path.GetFileName(r.ExecutableName.Trim());
            if(!r.ExecutableName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))r.ExecutableName+=".exe";
            r.ExecutablePath=r.ExecutablePath?.Trim();r.PathContains=r.PathContains?.Trim();r.CommandLineContains=r.CommandLineContains?.Trim();r.MinecraftRootDirectory=r.MinecraftRootDirectory?.Trim();
        }
        DialogResult=true;
    }
}
