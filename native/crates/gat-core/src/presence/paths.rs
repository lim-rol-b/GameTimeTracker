//! Path helpers shared by the presence layer.

use std::path::{Path, PathBuf};

use crate::text::trim_end_dir_sep;

/// Absolute path without resolving symlinks, mirroring Path.GetFullPath closely enough
/// for rule matching. Relative paths resolve against the process working directory.
pub fn full_path(path: impl AsRef<Path>) -> PathBuf {
    let path = path.as_ref();
    std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf())
}

/// Mirror of Path.TrimEndingDirectorySeparator for Path values.
pub fn trim_end_dir_sep_path(path: &Path) -> PathBuf {
    let text = path.to_string_lossy();
    PathBuf::from(trim_end_dir_sep(&text))
}

pub fn file_name(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

pub fn file_stem(path: &Path) -> String {
    path.file_stem()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}
