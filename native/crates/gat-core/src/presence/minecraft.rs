//! Identifies Minecraft Java clients by their entry point plus exact game directory.

const ENTRY_POINTS: &[&str] = &[
    "net.minecraft.client.main.Main",
    "net.minecraft.launchwrapper.Launch",
    "net.fabricmc.loader.impl.launch.knot.KnotClient",
    "net.fabricmc.loader.launch.knot.KnotClient",
    "org.quiltmc.loader.impl.launch.knot.KnotClient",
    "cpw.mods.bootstraplauncher.BootstrapLauncher",
    "cpw.mods.modlauncher.Launcher",
    "net.minecraft.client.Minecraft",
];

pub struct MinecraftProcessIdentity;

impl MinecraftProcessIdentity {
    pub fn matches(command: Option<&str>, root: &str) -> bool {
        let Some(command) = command else {
            return false;
        };
        if command.trim().is_empty() {
            return false;
        }
        let args = tokenize(command);
        if !args.iter().any(|arg| ENTRY_POINTS.contains(&arg.as_str())) {
            return false;
        }
        let mut game_dir: Option<&str> = None;
        let mut index = 0;
        while index < args.len() {
            if args[index] == "--gameDir" && index + 1 < args.len() {
                game_dir = Some(&args[index + 1]);
                index += 1;
            } else if let Some(rest) = args[index].strip_prefix("--gameDir=") {
                game_dir = Some(rest);
            }
            index += 1;
        }
        let Some(directory) = game_dir.and_then(normalize) else {
            return false;
        };
        let Some(expected) = normalize(root) else {
            return false;
        };
        if directory.eq_ignore_ascii_case(&expected) {
            return true;
        }
        let versions = format!("{}\\versions\\", expected);
        if directory.len() >= versions.len()
            && directory[..versions.len()].eq_ignore_ascii_case(&versions)
        {
            return !directory[versions.len()..].contains('\\');
        }
        false
    }
}

/// Reject anything that is not an absolute drive or UNC path and defend against traversal.
fn normalize(path: &str) -> Option<String> {
    let replaced = path.replace('/', "\\");
    let trimmed = replaced.trim_end_matches('\\');
    let bytes = trimmed.as_bytes();
    let drive =
        bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && bytes[2] == b'\\';
    let unc = trimmed.starts_with("\\\\");
    if !drive && !unc {
        return None;
    }
    if trimmed.split('\\').any(|part| part == "." || part == "..") {
        return None;
    }
    Some(trimmed.to_string())
}

/// Tokenize a command line the same way the original regex did, then strip quotes.
fn tokenize(command: &str) -> Vec<String> {
    let chars: Vec<char> = command.chars().collect();
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < chars.len() {
        if chars[index].is_whitespace() {
            index += 1;
            continue;
        }
        let mut token = String::new();
        loop {
            if index >= chars.len() || chars[index].is_whitespace() {
                break;
            }
            if chars[index] == '"' {
                index += 1;
                while index < chars.len() && chars[index] != '"' {
                    token.push(chars[index]);
                    index += 1;
                }
                if index < chars.len() {
                    index += 1;
                }
            } else {
                token.push(chars[index]);
                index += 1;
            }
        }
        tokens.push(token);
    }
    tokens
}
