using GameActivityTracker.Core;
using GameActivityTracker.Core.GamePresence;
using Xunit;
namespace GameActivityTracker.Tests;
public sealed class RelatedProcessTests
{
    private readonly GameMatcher _matcher=new();
    [Fact] public void ApexAlternativeExeMatchesOnlyWithinItsInstallDirectory()
    {
        var root=Path.Combine(Path.GetTempPath(),"Apex Legends");var original=Path.Combine(root,"r5apex_dx12.exe");var main=Path.Combine(root,"r5apex.exe");
        var game=new Game();var rules=RelatedProcessRules.Expand(game,[new(){GameId=game.Id,ExecutableName="r5apex_dx12.exe",ExecutablePath=original}],root,[original,main]);
        Assert.Equal(game.Id,_matcher.Match(new(1,"r5apex.exe",main,null,null),rules));
        Assert.Null(_matcher.Match(new(2,"r5apex.exe",Path.Combine(Path.GetTempPath(),"other","r5apex.exe"),null,null),rules));
        Assert.Null(_matcher.Match(new(3,"r5apex.exe",null,null,null),rules));
    }
    [Fact] public void CyberpunkLauncherIsExcludedAndRealGameSurvivesItsExit()
    {
        var root=Path.Combine(Path.GetTempPath(),"Cyberpunk 2077");var launcher=Path.Combine(root,"REDprelauncher.exe");var main=Path.Combine(root,"bin","x64","Cyberpunk2077.exe");
        var game=new Game();var rules=RelatedProcessRules.Expand(game,[new(){GameId=game.Id,ExecutableName="REDprelauncher.exe",ExecutablePath=launcher}],root,[launcher,main]);
        Assert.Null(_matcher.Match(new(1,"REDprelauncher.exe",launcher,null,null),rules));
        Assert.Equal(game.Id,_matcher.Match(new(2,"Cyberpunk2077.exe",main,null,null),rules));
    }
    [Fact] public void AdvancedAndDisabledRulesAreNotReplacedByBroaderRules()
    {
        var root=Path.GetTempPath();var path=Path.Combine(root,"Game.exe");var game=new Game();
        var advanced=new GameProcessRule{GameId=game.Id,ExecutableName="Game.exe",ExecutablePath=path,CommandLineContains="--profile special"};
        Assert.Single(RelatedProcessRules.Expand(game,[advanced],root,[path,Path.Combine(root,"other.exe")]));
        var disabled=advanced with {Enabled=false,CommandLineContains=null};
        Assert.Single(RelatedProcessRules.Expand(game,[disabled],root,[path]));
    }
    [Theory]
    [InlineData("net.minecraft.client.main.Main",@"D:\MC\.minecraft")]
    [InlineData("net.fabricmc.loader.impl.launch.knot.KnotClient",@"D:\MC\.minecraft\versions\1.21 Fabric")]
    [InlineData("cpw.mods.bootstraplauncher.BootstrapLauncher",@"D:\MC\.minecraft\versions\Forge")]
    public void MinecraftRecognizesPclVersionsAcrossJavaInstallations(string main,string directory)
    {
        var command=$"javaw.exe -Xmx4G {main} --gameDir \"{directory}\"";
        var rule=new GameProcessRule{GameId="mc",ExecutableName="javaw.exe",MinecraftRootDirectory=@"D:\MC\.minecraft"};
        Assert.Equal("mc",_matcher.Match(new(1,"javaw.exe",@"C:\different-java\bin\javaw.exe",command,null),[rule]));
    }
    [Theory]
    [InlineData("javaw.exe Other.Main --gameDir D:\\MC\\.minecraft")]
    [InlineData("javaw.exe net.minecraft.client.main.Main --gameDir D:\\MC\\.minecraft-other")]
    [InlineData("javaw.exe net.minecraft.client.main.Main --gameDir D:\\Other\\.minecraft")]
    [InlineData("javaw.exe net.minecraft.client.main.Main --gameDir ..\\.minecraft")]
    [InlineData("javaw.exe net.minecraft.client.main.Main")]
    [InlineData(null)]
    public void MinecraftRejectsOtherAppsAndUnknownOrUnrelatedDirectories(string? command)
    { Assert.False(MinecraftProcessIdentity.Matches(command,@"D:\MC\.minecraft")); }
    [Fact] public void VersionSpecificMinecraftRuleDoesNotMatchOtherVersions()
    {
        Assert.True(MinecraftProcessIdentity.Matches("javaw.exe net.minecraft.client.main.Main --gameDir=\"D:\\MC\\.minecraft\\versions\\One\"",@"D:\MC\.minecraft\versions\One"));
        Assert.False(MinecraftProcessIdentity.Matches("javaw.exe net.minecraft.client.main.Main --gameDir=\"D:\\MC\\.minecraft\\versions\\Two\"",@"D:\MC\.minecraft\versions\One"));
    }
}
