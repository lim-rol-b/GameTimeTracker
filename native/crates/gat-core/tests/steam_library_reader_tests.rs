mod common;

use std::path::{Path, PathBuf};

use common::TempDir;
use gat_core::presence::SteamLibraryReader;

fn library(root: &Path, name: &str) -> PathBuf {
    let path = root.join(name);
    std::fs::create_dir_all(path.join("steamapps").join("common")).unwrap();
    path
}

fn game(library: &Path, id: &str, folder: &str, files: &[&str]) -> PathBuf {
    let manifest = library
        .join("steamapps")
        .join(format!("appmanifest_{}.acf", id));
    let text =
        format!(r#""AppState" {{ "appid" "{id}" "name" "{folder}" "installdir" "{folder}" }}"#);
    std::fs::write(&manifest, text).unwrap();
    let directory = library.join("steamapps").join("common").join(folder);
    std::fs::create_dir_all(&directory).unwrap();
    for file in files {
        let path = directory.join(file);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).unwrap();
        }
        std::fs::write(path, "").unwrap();
    }
    directory
}

fn file_name(path: &str) -> String {
    Path::new(path)
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn escape_vdf(path: &str) -> String {
    path.chars()
        .flat_map(|character| {
            if character == '\\' {
                vec!['\\', '\\']
            } else {
                vec![character]
            }
        })
        .collect()
}

#[test]
fn recommends_folder_match_and_keeps_real_engine_game() {
    let temp = TempDir::new("steam-recommend");
    let lib = library(&temp.path, "Steam");
    game(
        &lib,
        "1",
        "My Game",
        &[
            "other.exe",
            "My-Game.exe",
            "unins000.exe",
            "UnityCrashHandler64.exe",
            "UE4PrereqSetup_x64.exe",
            "Engine/Binaries/Win64/UnrealEditor.exe",
            "Engine/Binaries/Win64/MyGame-Win64-Shipping.exe",
        ],
    );
    let scan = SteamLibraryReader
        .read(&lib.to_string_lossy(), true)
        .unwrap();
    assert_eq!(scan.games.len(), 1);
    let value = &scan.games[0];
    assert_eq!(value.executables.len(), 3);
    assert_eq!(file_name(&value.executables[0]), "My-Game.exe");
    assert_eq!(
        SteamLibraryReader::recommended_executable(value),
        Some(value.executables[0].clone())
    );
    assert!(value
        .executables
        .iter()
        .any(|path| path.ends_with("MyGame-Win64-Shipping.exe")));
}

#[test]
fn ambiguous_names_require_selection_and_single_candidate_is_recommended() {
    let temp = TempDir::new("steam-ambiguous");
    let lib = library(&temp.path, "Steam");
    game(&lib, "1", "Game", &["x64/Game.exe", "x86/Game.exe"]);
    game(&lib, "2", "Another", &["Another-Win64-Shipping.exe"]);
    let scan = SteamLibraryReader
        .read(&lib.to_string_lossy(), true)
        .unwrap();
    let one = scan.games.iter().find(|game| game.app_id == "1").unwrap();
    assert!(SteamLibraryReader::recommended_executable(one).is_none());
    let two = scan.games.iter().find(|game| game.app_id == "2").unwrap();
    assert!(SteamLibraryReader::recommended_executable(two).is_some());
}

#[test]
fn discovers_registered_libraries_deduplicates_and_survives_bad_manifest() {
    let temp = TempDir::new("steam-libraries");
    let first = library(&temp.path, "Steam");
    let second = library(&temp.path, "OtherLibrary");
    game(&first, "1", "First", &["First.exe"]);
    game(&second, "1", "Duplicate", &["Duplicate.exe"]);
    game(&second, "2", "Second", &["Second.exe"]);
    let escaped = escape_vdf(&second.to_string_lossy());
    let folders = first.join("steamapps").join("libraryfolders.vdf");
    std::fs::write(
        &folders,
        format!(r#""libraryfolders" {{ "0" {{ "path" "{escaped}" }} "1" "{escaped}" }}"#),
    )
    .unwrap();
    std::fs::write(
        second.join("steamapps").join("appmanifest_bad.acf"),
        "broken",
    )
    .unwrap();

    let missing = temp.path.join("missing").to_string_lossy().into_owned();
    let roots = vec![
        first.to_string_lossy().into_owned(),
        first.to_string_lossy().into_owned(),
        missing,
    ];
    let scan = SteamLibraryReader.read_libraries(&roots);
    assert_eq!(scan.games.len(), 2);
    assert_eq!(scan.warnings.len(), 1);
    assert!(scan
        .steam_apps_directory
        .contains(&second.to_string_lossy().into_owned()));
}
