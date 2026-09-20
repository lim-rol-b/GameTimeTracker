mod common;

use common::temp_path;
use gat_core::models::{Game, GameProcessRule};
use gat_core::presence::{
    GameMatcher, MinecraftProcessIdentity, ProcessSnapshot, RelatedProcessRules,
};
use std::path::Path;

fn as_string(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

#[test]
fn apex_alternative_exe_matches_only_within_its_install_directory() {
    let root = temp_path("Apex Legends");
    let original = as_string(&root.join("r5apex_dx12.exe"));
    let main = as_string(&root.join("r5apex.exe"));
    let game = Game::default();
    let rule = GameProcessRule {
        game_id: game.id.clone(),
        executable_name: "r5apex_dx12.exe".to_string(),
        executable_path: Some(original.clone()),
        ..Default::default()
    };
    let rules =
        RelatedProcessRules::expand(&game, &[rule], &as_string(&root), &[original, main.clone()]);
    let matcher = GameMatcher;
    assert_eq!(
        matcher.match_process(
            &ProcessSnapshot::new(1, "r5apex.exe", Some(main), None, None),
            &rules
        ),
        Some(game.id.clone())
    );
    let other = as_string(&temp_path("other").join("r5apex.exe"));
    assert_eq!(
        matcher.match_process(
            &ProcessSnapshot::new(2, "r5apex.exe", Some(other), None, None),
            &rules
        ),
        None
    );
    assert_eq!(
        matcher.match_process(
            &ProcessSnapshot::new(3, "r5apex.exe", None, None, None),
            &rules
        ),
        None
    );
}

#[test]
fn cyberpunk_launcher_is_excluded_and_real_game_survives_its_exit() {
    let root = temp_path("Cyberpunk 2077");
    let launcher = as_string(&root.join("REDprelauncher.exe"));
    let main = as_string(&root.join("bin").join("x64").join("Cyberpunk2077.exe"));
    let game = Game::default();
    let rule = GameProcessRule {
        game_id: game.id.clone(),
        executable_name: "REDprelauncher.exe".to_string(),
        executable_path: Some(launcher.clone()),
        ..Default::default()
    };
    let rules = RelatedProcessRules::expand(
        &game,
        &[rule],
        &as_string(&root),
        &[launcher.clone(), main.clone()],
    );
    let matcher = GameMatcher;
    assert_eq!(
        matcher.match_process(
            &ProcessSnapshot::new(1, "REDprelauncher.exe", Some(launcher), None, None),
            &rules
        ),
        None
    );
    assert_eq!(
        matcher.match_process(
            &ProcessSnapshot::new(2, "Cyberpunk2077.exe", Some(main), None, None),
            &rules
        ),
        Some(game.id.clone())
    );
}

#[test]
fn advanced_and_disabled_rules_are_not_replaced_by_broader_rules() {
    let root = temp_path("advanced");
    let path = as_string(&root.join("Game.exe"));
    let game = Game::default();
    let advanced = GameProcessRule {
        game_id: game.id.clone(),
        executable_name: "Game.exe".to_string(),
        executable_path: Some(path.clone()),
        command_line_contains: Some("--profile special".to_string()),
        ..Default::default()
    };
    let other = as_string(&root.join("other.exe"));
    assert_eq!(
        RelatedProcessRules::expand(
            &game,
            std::slice::from_ref(&advanced),
            &as_string(&root),
            &[path.clone(), other]
        )
        .len(),
        1
    );
    let disabled = GameProcessRule {
        enabled: false,
        command_line_contains: None,
        ..advanced
    };
    assert_eq!(
        RelatedProcessRules::expand(&game, &[disabled], &as_string(&root), &[path]).len(),
        1
    );
}

#[test]
fn minecraft_recognizes_pcl_versions_across_java_installations() {
    let cases = [
        ("net.minecraft.client.main.Main", r"D:\MC\.minecraft"),
        (
            "net.fabricmc.loader.impl.launch.knot.KnotClient",
            r"D:\MC\.minecraft\versions\1.21 Fabric",
        ),
        (
            "cpw.mods.bootstraplauncher.BootstrapLauncher",
            r"D:\MC\.minecraft\versions\Forge",
        ),
    ];
    for (main, directory) in cases {
        let command = format!(r#"javaw.exe -Xmx4G {} --gameDir "{}""#, main, directory);
        let rule = GameProcessRule {
            game_id: "mc".to_string(),
            executable_name: "javaw.exe".to_string(),
            minecraft_root_directory: Some(r"D:\MC\.minecraft".to_string()),
            ..Default::default()
        };
        assert_eq!(
            GameMatcher.match_process(
                &ProcessSnapshot::new(
                    1,
                    "javaw.exe",
                    Some(r"C:\different-java\bin\javaw.exe".to_string()),
                    Some(command),
                    None
                ),
                &[rule]
            ),
            Some("mc".to_string())
        );
    }
}

#[test]
fn minecraft_rejects_other_apps_and_unknown_or_unrelated_directories() {
    let cases: [Option<&str>; 6] = [
        Some(r"javaw.exe Other.Main --gameDir D:\MC\.minecraft"),
        Some(r"javaw.exe net.minecraft.client.main.Main --gameDir D:\MC\.minecraft-other"),
        Some(r"javaw.exe net.minecraft.client.main.Main --gameDir D:\Other\.minecraft"),
        Some(r"javaw.exe net.minecraft.client.main.Main --gameDir ..\.minecraft"),
        Some(r"javaw.exe net.minecraft.client.main.Main"),
        None,
    ];
    for command in cases {
        assert!(!MinecraftProcessIdentity::matches(
            command,
            r"D:\MC\.minecraft"
        ));
    }
}

#[test]
fn version_specific_minecraft_rule_does_not_match_other_versions() {
    assert!(MinecraftProcessIdentity::matches(
        Some(
            r#"javaw.exe net.minecraft.client.main.Main --gameDir="D:\MC\.minecraft\versions\One""#
        ),
        r"D:\MC\.minecraft\versions\One"
    ));
    assert!(!MinecraftProcessIdentity::matches(
        Some(
            r#"javaw.exe net.minecraft.client.main.Main --gameDir="D:\MC\.minecraft\versions\Two""#
        ),
        r"D:\MC\.minecraft\versions\One"
    ));
}
