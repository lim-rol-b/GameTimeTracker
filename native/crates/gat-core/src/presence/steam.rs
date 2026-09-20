//! Reads a user-selected local Steam library. Does not require Steam, an account,
//! or a network call, and never imports playtime.

use std::collections::{HashMap, HashSet, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};

use crate::presence::paths::{file_name, file_stem, full_path, trim_end_dir_sep_path};
use crate::text::eq_ignore_case;

const HELPER_DIRECTORIES: &[&str] = &[
    "_CommonRedist",
    "redist",
    "redistributables",
    "installers",
    "DirectX",
    "vcredist",
    "EasyAntiCheat",
    "BattlEye",
    "__Installer",
    "dotnet",
    "UnityCrashHandler",
    "CrashReportClient",
];

const IGNORED_PREFIXES: &[&str] = &[
    "easyanticheat",
    "beservice",
    "unins",
    "crashreport",
    "crashpad",
    "unitycrash",
    "vc_redist",
    "vcredist",
    "dxsetup",
    "dotnet",
    "setup",
    "installer",
    "steamwebhelper",
    "unrealcrash",
    "ue4prereq",
    "ue5prereq",
    "unityhub",
    "unrealcefsubprocess",
    "unrealversionselector",
    "unitybugreporter",
    "bugreporter",
];

const IGNORED_NAMES: &[&str] = &[
    "Unity",
    "UnityPlayer",
    "UnrealEditor",
    "UE4Editor",
    "ShaderCompileWorker",
    "UnrealLightmass",
    "UnrealPak",
    "UnityPackageManager",
    "UnityShaderCompiler",
    "UnityAutoQuit",
    "UnrealFrontend",
];

#[derive(Debug, Clone, PartialEq)]
pub struct InstalledSteamGame {
    pub app_id: String,
    pub name: String,
    pub install_directory: String,
    pub executables: Vec<String>,
    pub warning: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct SteamLibraryScan {
    pub steam_apps_directory: String,
    pub games: Vec<InstalledSteamGame>,
    pub warnings: Vec<String>,
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum SteamError {
    #[error(
        "所选目录中没有 steamapps 文件夹。请选择 Steam 游戏库、Steam 安装目录或 steamapps 文件夹。"
    )]
    MissingSteamApps,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct SteamLibraryReader;

impl SteamLibraryReader {
    /// Follow registered libraries from any mix of roots, deduplicating and surviving bad data.
    pub fn read_libraries(&self, roots: &[String]) -> SteamLibraryScan {
        let mut pending: VecDeque<String> = roots.iter().cloned().collect();
        let mut visited: HashSet<String> = HashSet::new();
        let mut libraries: Vec<String> = Vec::new();
        let mut games: Vec<InstalledSteamGame> = Vec::new();
        let mut warnings: Vec<String> = Vec::new();

        while let Some(root) = pending.pop_front() {
            let path = trim_end_dir_sep_path(&full_path(&root));
            if !visited.insert(path.to_string_lossy().to_lowercase()) {
                continue;
            }
            if !path.join("steamapps").is_dir() {
                continue;
            }
            libraries.push(path.to_string_lossy().into_owned());

            let folders = path.join("steamapps").join("libraryfolders.vdf");
            if folders.is_file() {
                let too_big = fs::metadata(&folders)
                    .map(|meta| meta.len() > 2 * 1024 * 1024)
                    .unwrap_or(false);
                if too_big {
                    warnings.push(format!("{}：库清单过大。", folders.display()));
                } else {
                    match fs::read_to_string(&folders) {
                        Ok(text) => {
                            for candidate in library_paths(&text) {
                                if Path::new(&candidate).is_absolute() {
                                    pending.push_back(candidate);
                                }
                            }
                        }
                        Err(error) => {
                            warnings.push(format!("{}：{}", folders.display(), error));
                        }
                    }
                }
            }

            match self.read(&path.to_string_lossy(), true) {
                Ok(scan) => {
                    for warning in &scan.warnings {
                        warnings.push(format!("{}：{}", path.display(), warning));
                    }
                    for game in scan.games {
                        match games
                            .iter_mut()
                            .find(|existing| eq_ignore_case(&existing.app_id, &game.app_id))
                        {
                            Some(existing)
                                if existing.executables.is_empty()
                                    && !game.executables.is_empty() =>
                            {
                                *existing = game;
                            }
                            Some(_) => {}
                            None => games.push(game),
                        }
                    }
                }
                Err(error) => warnings.push(format!("{}：{}", path.display(), error)),
            }
        }

        games.sort_by_key(|game| game.name.to_lowercase());
        SteamLibraryScan {
            steam_apps_directory: libraries.join("\n"),
            games,
            warnings,
        }
    }

    pub fn matches_install_directory(executable: &str, directory: &str) -> bool {
        let executable_name = match_name(&file_stem(Path::new(executable)));
        let directory_name = match_name(&file_name(&trim_end_dir_sep_path(Path::new(directory))));
        executable_name == directory_name
    }

    pub fn recommended_executable(game: &InstalledSteamGame) -> Option<String> {
        let matches: Vec<&String> = game
            .executables
            .iter()
            .filter(|path| Self::matches_install_directory(path, &game.install_directory))
            .collect();
        if matches.len() == 1 {
            return Some(matches[0].clone());
        }
        if game.executables.len() == 1 {
            return Some(game.executables[0].clone());
        }
        None
    }

    pub fn read(
        &self,
        selected_directory: &str,
        discover_executables: bool,
    ) -> Result<SteamLibraryScan, SteamError> {
        let selected = trim_end_dir_sep_path(&full_path(selected_directory));
        let mut steam_apps = selected.join("steamapps");
        let selected_name = file_name(&selected);
        if eq_ignore_case(&selected_name, "steamapps") {
            steam_apps = selected.clone();
        } else if eq_ignore_case(&selected_name, "common") {
            if let Some(parent) = selected.parent() {
                if eq_ignore_case(&file_name(parent), "steamapps") {
                    steam_apps = parent.to_path_buf();
                }
            }
        }
        if !steam_apps.is_dir() {
            return Err(SteamError::MissingSteamApps);
        }

        let common = full_path(steam_apps.join("common"));
        let mut warnings = Vec::new();
        let mut games = Vec::new();
        let mut app_ids: HashSet<String> = HashSet::new();

        let mut manifests: Vec<PathBuf> = Vec::new();
        if let Ok(entries) = fs::read_dir(&steam_apps) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with("appmanifest_") && name.ends_with(".acf") {
                    manifests.push(entry.path());
                }
            }
        }
        manifests.sort_by(|a, b| {
            file_name(a)
                .to_lowercase()
                .cmp(&file_name(b).to_lowercase())
        });

        for manifest in manifests {
            let label = file_name(&manifest);
            match self.parse_manifest(&manifest, &common, discover_executables) {
                Ok(game) => {
                    if app_ids.insert(game.app_id.to_lowercase()) {
                        games.push(game);
                    }
                }
                Err(message) => warnings.push(format!("{}：{}", label, message)),
            }
        }

        games.sort_by_key(|game| game.name.to_lowercase());
        Ok(SteamLibraryScan {
            steam_apps_directory: steam_apps.to_string_lossy().into_owned(),
            games,
            warnings,
        })
    }

    fn parse_manifest(
        &self,
        manifest: &Path,
        common: &Path,
        discover_executables: bool,
    ) -> Result<InstalledSteamGame, String> {
        let metadata = fs::metadata(manifest).map_err(|error| error.to_string())?;
        if metadata.len() > 2 * 1024 * 1024 {
            return Err("清单文件过大，已跳过。".to_string());
        }
        let text = fs::read_to_string(manifest).map_err(|error| error.to_string())?;
        let fields = read_app_state(&text)?;

        let raw_app_id = field(&fields, "appid").cloned().unwrap_or_default();
        let name = field(&fields, "name")
            .cloned()
            .unwrap_or_default()
            .trim()
            .to_string();
        let install = field(&fields, "installdir")
            .cloned()
            .unwrap_or_default()
            .trim()
            .to_string();
        let numeric: u32 = raw_app_id
            .parse()
            .map_err(|_| "缺少有效的 appid、name 或 installdir。".to_string())?;
        if numeric == 0 || name.is_empty() || install.is_empty() {
            return Err("缺少有效的 appid、name 或 installdir。".to_string());
        }
        let app_id = numeric.to_string();

        let directory = full_path(common.join(&install));
        let common_prefix = format!(
            "{}{}",
            trim_end_dir_sep_path(common).to_string_lossy(),
            std::path::MAIN_SEPARATOR
        );
        if !directory
            .to_string_lossy()
            .to_lowercase()
            .starts_with(&common_prefix.to_lowercase())
        {
            return Err("安装目录不在 steamapps/common 中。".to_string());
        }

        let mut warning = None;
        let mut executables = Vec::new();
        if !directory.is_dir() {
            warning = Some("安装目录不存在，可能尚未下载完成。".to_string());
        } else if discover_executables {
            let mut warning_slot = None;
            executables = self.find_executables(&directory, &mut warning_slot);
            warning = warning_slot;
        }

        Ok(InstalledSteamGame {
            app_id,
            name,
            install_directory: directory.to_string_lossy().into_owned(),
            executables,
            warning,
        })
    }

    pub fn find_executables(&self, directory: &Path, warning: &mut Option<String>) -> Vec<String> {
        let mut result: Vec<String> = Vec::new();
        let mut stack: Vec<(PathBuf, usize)> = vec![(directory.to_path_buf(), 0)];
        let mut visited = 0usize;

        while let Some((path, depth)) = stack.pop() {
            visited += 1;
            if visited > 10_000 {
                *warning = Some("目录较大，扫描已截断；可手动选择可执行文件。".to_string());
                break;
            }
            let entries: Vec<PathBuf> = match fs::read_dir(&path) {
                Ok(entries) => entries.flatten().map(|entry| entry.path()).collect(),
                Err(_) => {
                    *warning = Some("部分目录无法读取；可手动选择可执行文件。".to_string());
                    continue;
                }
            };
            for entry in entries {
                let Ok(metadata) = fs::symlink_metadata(&entry) else {
                    continue;
                };
                if metadata.file_type().is_symlink() {
                    continue;
                }
                if metadata.is_dir() {
                    let name = file_name(&entry);
                    if HELPER_DIRECTORIES
                        .iter()
                        .any(|helper| eq_ignore_case(&name, helper))
                    {
                        continue;
                    }
                    if depth >= 16 {
                        if warning.is_none() {
                            *warning =
                                Some("部分目录过深，未扫描；可手动选择可执行文件。".to_string());
                        }
                        continue;
                    }
                    stack.push((entry, depth + 1));
                    continue;
                }
                if !metadata.is_file() {
                    continue;
                }
                let name = file_name(&entry);
                if !name.to_lowercase().ends_with(".exe") {
                    continue;
                }
                let stem = file_stem(&entry);
                if is_ignored_executable(&stem) {
                    continue;
                }
                let full = entry.to_string_lossy().into_owned();
                if crate::presence::related::RelatedProcessRules::is_launcher(&full)
                    || crate::presence::related::RelatedProcessRules::is_shared_host(&full)
                {
                    continue;
                }
                result.push(full);
                if result.len() >= 1000 {
                    *warning =
                        Some("可执行文件较多，列表已截断；可手动选择可执行文件。".to_string());
                    return rank(result, directory);
                }
            }
        }

        if result.is_empty() && warning.is_none() {
            *warning = Some(
                "未找到游戏主程序，请手动选择；未完成下载或非 Windows 游戏可能没有可执行文件。"
                    .to_string(),
            );
        }
        rank(result, directory)
    }
}

fn is_ignored_executable(stem: &str) -> bool {
    let lower = stem.to_lowercase();
    IGNORED_PREFIXES
        .iter()
        .any(|prefix| lower.starts_with(prefix))
        || IGNORED_NAMES.iter().any(|name| eq_ignore_case(stem, name))
}

fn rank(mut paths: Vec<String>, directory: &Path) -> Vec<String> {
    let directory_text = directory.to_string_lossy().into_owned();
    paths.sort_by(|a, b| {
        let a_match = SteamLibraryReader::matches_install_directory(a, &directory_text);
        let b_match = SteamLibraryReader::matches_install_directory(b, &directory_text);
        b_match
            .cmp(&a_match)
            .then_with(|| relative(directory, a).cmp(&relative(directory, b)))
    });
    paths
}

fn relative(base: &Path, path: &str) -> String {
    Path::new(path)
        .strip_prefix(base)
        .map(|rest| rest.to_string_lossy().to_lowercase())
        .unwrap_or_else(|_| path.to_lowercase())
}

fn match_name(value: &str) -> String {
    value
        .chars()
        .filter(|character| character.is_alphanumeric())
        .flat_map(|character| character.to_uppercase())
        .collect()
}

/// Extract fully-qualified library roots from a libraryfolders.vdf text blob.
fn library_paths(text: &str) -> Vec<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut out = Vec::new();
    let mut index = 0;
    while index < chars.len() {
        if chars[index] != '"' {
            index += 1;
            continue;
        }
        let Some((key, after_key)) = parse_quoted(&chars, index) else {
            index += 1;
            continue;
        };
        let mut cursor = after_key;
        while cursor < chars.len() && chars[cursor].is_whitespace() {
            cursor += 1;
        }
        if cursor < chars.len() && chars[cursor] == '"' {
            if let Some((value, after_value)) = parse_quoted(&chars, cursor) {
                let is_key = key == "path"
                    || (!key.is_empty() && key.chars().all(|character| character.is_ascii_digit()));
                if is_key {
                    out.push(value.replace("\\\\", "\\"));
                }
                index = after_value;
                continue;
            }
        }
        index = after_key;
    }
    out
}

fn parse_quoted(chars: &[char], start: usize) -> Option<(String, usize)> {
    if chars.get(start) != Some(&'"') {
        return None;
    }
    let mut value = String::new();
    let mut index = start + 1;
    while index < chars.len() {
        if chars[index] == '"' {
            return Some((value, index + 1));
        }
        value.push(chars[index]);
        index += 1;
    }
    None
}

fn field<'a>(values: &'a HashMap<String, String>, key: &str) -> Option<&'a String> {
    values.get(key).or_else(|| {
        values
            .iter()
            .find(|(k, _)| eq_ignore_case(k, key))
            .map(|(_, v)| v)
    })
}

fn read_app_state(text: &str) -> Result<HashMap<String, String>, String> {
    let mut parser = VdfParser {
        tokens: tokenize_vdf(text),
        index: 0,
    };
    while parser.index < parser.tokens.len() {
        let key = decode(&parser.take()?);
        let value = parser.take()?;
        if value != "{" {
            continue;
        }
        let block = parser.block(0)?;
        if eq_ignore_case(&key, "AppState") {
            return Ok(block);
        }
    }
    Err("未找到 AppState 游戏信息。".to_string())
}

struct VdfParser {
    tokens: Vec<String>,
    index: usize,
}

impl VdfParser {
    fn take(&mut self) -> Result<String, String> {
        if self.index < self.tokens.len() {
            let token = self.tokens[self.index].clone();
            self.index += 1;
            Ok(token)
        } else {
            Err("清单内容不完整。".to_string())
        }
    }

    fn block(&mut self, depth: usize) -> Result<HashMap<String, String>, String> {
        if depth > 32 {
            return Err("清单嵌套层数过多。".to_string());
        }
        let mut values = HashMap::new();
        loop {
            let key = self.take()?;
            if key == "}" {
                return Ok(values);
            }
            if key == "{" {
                return Err("清单字段名称缺失。".to_string());
            }
            let value = self.take()?;
            if value == "{" {
                self.block(depth + 1)?;
                continue;
            }
            if value == "}" {
                return Err("清单字段值缺失。".to_string());
            }
            values.insert(decode(&key), decode(&value));
        }
    }
}

fn tokenize_vdf(text: &str) -> Vec<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < chars.len() {
        if index + 1 < chars.len() && chars[index] == '/' && chars[index + 1] == '/' {
            while index < chars.len() && chars[index] != '\n' && chars[index] != '\r' {
                index += 1;
            }
        } else if chars[index] == '"' {
            let mut token = String::from("\"");
            index += 1;
            while index < chars.len() {
                if chars[index] == '\\'
                    && index + 1 < chars.len()
                    && (chars[index + 1] == '\\' || chars[index + 1] == '"')
                {
                    token.push(chars[index]);
                    token.push(chars[index + 1]);
                    index += 2;
                } else if chars[index] == '"' {
                    token.push('"');
                    index += 1;
                    break;
                } else {
                    token.push(chars[index]);
                    index += 1;
                }
            }
            tokens.push(token);
        } else if chars[index] == '{' || chars[index] == '}' {
            tokens.push(chars[index].to_string());
            index += 1;
        } else if chars[index].is_whitespace() {
            index += 1;
        } else {
            let mut token = String::new();
            while index < chars.len()
                && !chars[index].is_whitespace()
                && chars[index] != '{'
                && chars[index] != '}'
                && chars[index] != '"'
            {
                token.push(chars[index]);
                index += 1;
            }
            tokens.push(token);
        }
    }
    tokens
}

fn decode(value: &str) -> String {
    if value.len() >= 2 && value.starts_with('"') && value.ends_with('"') {
        unescape(&value[1..value.len() - 1])
    } else {
        value.to_string()
    }
}

fn unescape(value: &str) -> String {
    let chars: Vec<char> = value.chars().collect();
    let mut out = String::new();
    let mut index = 0;
    while index < chars.len() {
        if chars[index] == '\\'
            && index + 1 < chars.len()
            && (chars[index + 1] == '\\' || chars[index + 1] == '"')
        {
            out.push(chars[index + 1]);
            index += 2;
        } else {
            out.push(chars[index]);
            index += 1;
        }
    }
    out
}
