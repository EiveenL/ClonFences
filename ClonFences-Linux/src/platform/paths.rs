//! Carpetas XDG.

use std::path::PathBuf;

/// Carpeta del escritorio según `xdg-user-dirs` (p. ej. `~/Escritorio`).
pub fn desktop_dir() -> PathBuf {
    dirs::desktop_dir()
        .or_else(|| dirs::home_dir().map(|h| h.join("Desktop")))
        .unwrap_or_else(|| PathBuf::from("."))
}

/// Carpeta de datos: `$CLONFENCES_DATA` o `~/.config/ClonFences`.
pub fn data_dir() -> PathBuf {
    match std::env::var_os("CLONFENCES_DATA") {
        Some(custom) if !custom.is_empty() => PathBuf::from(custom),
        _ => dirs::config_dir().unwrap_or_else(|| PathBuf::from(".")).join("ClonFences"),
    }
}

pub fn config_file() -> PathBuf {
    data_dir().join("config.json")
}

/// ¿Archivo oculto al estilo Unix (nombre que empieza por punto)?
pub fn is_hidden(path: &std::path::Path) -> bool {
    path.file_name().is_some_and(|n| n.to_string_lossy().starts_with('.'))
}
