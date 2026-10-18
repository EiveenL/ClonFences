//! Registro sencillo en `<datos>/log.txt` (y en stderr).

use std::fmt::Display;
use std::io::Write;

pub fn log(msg: impl Display) {
    let line = format!("{} {msg}\n", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"));
    eprint!("{line}");
    let dir = super::paths::data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("log.txt");
    // Evitar que crezca sin límite.
    if std::fs::metadata(&path).is_ok_and(|m| m.len() > 1_000_000) {
        let _ = std::fs::rename(&path, dir.join("log.old.txt"));
    }
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
        let _ = f.write_all(line.as_bytes());
    }
}
