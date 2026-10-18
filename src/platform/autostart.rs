//! Inicio automático al iniciar sesión: `~/.config/autostart/clonfences.desktop` (XDG Autostart, que
//! cosmic-session respeta).

use std::io;
use std::path::{Path, PathBuf};

fn entry_file() -> PathBuf {
    dirs::config_dir().unwrap_or_else(|| PathBuf::from(".")).join("autostart").join("clonfences.desktop")
}

pub fn is_enabled() -> bool {
    entry_file().exists()
}

/// Contenido del lanzador que arranca `exe`.
fn entry(exe: &Path) -> String {
    format!(
        "[Desktop Entry]\n\
         Type=Application\n\
         Name=Clon Fences\n\
         Comment=Organizador de escritorio inspirado en Stardock Fences\n\
         Exec={}\n\
         Icon=view-grid-symbolic\n\
         Terminal=false\n\
         NoDisplay=true\n\
         X-GNOME-Autostart-enabled=true\n",
        quote_exec(&exe.to_string_lossy())
    )
}

/// Entrecomilla un argumento de `Exec=` según la especificación de Desktop Entry.
fn quote_exec(arg: &str) -> String {
    if !arg.contains(|c: char| c.is_whitespace() || "\"'\\><~|&;$*?#()`%".contains(c)) {
        return arg.to_string();
    }
    let mut out = String::from("\"");
    for c in arg.chars() {
        match c {
            '"' | '`' | '$' | '\\' => {
                out.push('\\');
                out.push(c);
            }
            '%' => out.push_str("%%"),
            _ => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Crea (o quita) el lanzador de inicio automático apuntando al ejecutable actual.
pub fn set_enabled(enabled: bool) -> io::Result<PathBuf> {
    let file = entry_file();
    if enabled {
        let exe = std::env::current_exe()?;
        std::fs::create_dir_all(file.parent().unwrap())?;
        std::fs::write(&file, entry(&exe))?;
    } else {
        match std::fs::remove_file(&file) {
            Err(e) if e.kind() != io::ErrorKind::NotFound => return Err(e),
            _ => {}
        }
    }
    Ok(file)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quotes_paths_with_spaces() {
        assert_eq!(quote_exec("/usr/bin/clonfences"), "/usr/bin/clonfences");
        assert_eq!(quote_exec("/home/a/Proyectos Code/clonfences"), "\"/home/a/Proyectos Code/clonfences\"");
        assert_eq!(quote_exec("/x/50%$"), "\"/x/50%%\\$\"");
    }

    #[test]
    fn entry_has_exec_line() {
        let text = entry(Path::new("/home/a/.local/bin/clonfences"));
        assert!(text.contains("\nExec=/home/a/.local/bin/clonfences\n"));
    }
}
