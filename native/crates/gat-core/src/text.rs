//! Case-insensitive comparison helpers matching .NET StringComparison.OrdinalIgnoreCase.

/// Compare two strings ignoring case (ASCII fast path, Unicode fallback).
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    a.eq_ignore_ascii_case(b)
        || ((!a.is_ascii() || !b.is_ascii()) && a.to_lowercase() == b.to_lowercase())
}

/// Does haystack contain needle, ignoring case?
pub fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    if needle.is_empty() {
        return true;
    }
    if haystack.len() < needle.len() {
        return false;
    }
    if haystack.is_ascii() && needle.is_ascii() {
        haystack
            .as_bytes()
            .windows(needle.len())
            .any(|window| window.eq_ignore_ascii_case(needle.as_bytes()))
    } else {
        haystack.to_lowercase().contains(&needle.to_lowercase())
    }
}

/// Trim trailing directory separators, mirroring Path.TrimEndingDirectorySeparator.
pub fn trim_end_dir_sep(value: &str) -> &str {
    let trimmed = value.trim_end_matches(['/', '\\']);
    if trimmed.is_empty() {
        value
    } else {
        trimmed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn equality_is_case_insensitive() {
        assert!(eq_ignore_case("GAME.EXE", "game.exe"));
        assert!(!eq_ignore_case("game.exe", "game2.exe"));
        assert!(eq_ignore_case("游戏.EXE", "游戏.exe"));
    }

    #[test]
    fn contains_is_case_insensitive() {
        assert!(contains_ignore_case("C:\\Games\\Apex", "games"));
        assert!(!contains_ignore_case("Apex", "games"));
        assert!(contains_ignore_case("anything", ""));
    }

    #[test]
    fn trims_trailing_separators() {
        assert_eq!(trim_end_dir_sep("C:\\Games\\"), "C:\\Games");
        assert_eq!(trim_end_dir_sep("/tmp/library/"), "/tmp/library");
        assert_eq!(trim_end_dir_sep("/"), "/");
    }
}
