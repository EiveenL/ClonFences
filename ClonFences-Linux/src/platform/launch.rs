//! Abrir archivos y lanzadores.

use std::path::Path;
use std::process::{Command, Stdio};

use super::log::log;

/// Lanza un proceso sin esperarlo; un hilo recoge su código de salida para no dejar zombis.
fn spawn_detached(mut cmd: Command) -> std::io::Result<()> {
    let mut child = cmd.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn()?;
    std::thread::spawn(move || {
        let _ = child.wait();
    });
    Ok(())
}

/// Abre un elemento: los `.desktop` se ejecutan con `gio launch`; lo demás, con la aplicación predeterminada.
pub fn open(path: &Path) {
    let is_launcher = path.extension().is_some_and(|e| e == "desktop") && path.is_file();
    let cmd = if is_launcher {
        let mut c = Command::new("gio");
        c.arg("launch").arg(path);
        c
    } else {
        let mut c = Command::new("xdg-open");
        c.arg(path);
        c
    };
    if let Err(e) = spawn_detached(cmd) {
        log(format!("No se pudo abrir {}: {e}", path.display()));
    }
}
