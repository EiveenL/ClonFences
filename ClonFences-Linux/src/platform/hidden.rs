//! Ocultar del escritorio de COSMIC los elementos que ya están en un fence.
//!
//! `cosmic-files` (como Nautilus) oculta los nombres listados en el archivo `.hidden` de cada carpeta,
//! uno por línea. La app añade ahí los elementos "contenidos" en fences y los quita al salir,
//! sin tocar las líneas que haya puesto el usuario.

use std::collections::BTreeSet;
use std::io;
use std::path::Path;

use crate::model::config::AppConfig;

/// Nombres de los elementos del escritorio (hijos directos de `desktop`) que están en algún fence normal.
pub fn fenced_names(cfg: &AppConfig, desktop: &Path) -> BTreeSet<String> {
    cfg.fenced_paths()
        .iter()
        .filter(|p| p.parent() == Some(desktop))
        .filter_map(|p| p.file_name().map(|n| n.to_string_lossy().into_owned()))
        .collect()
}

/// Calcula el nuevo contenido de `.hidden`.
/// - `lines`: líneas actuales del archivo.
/// - `want`: nombres que deben quedar ocultos por la app.
/// - `managed_before`: nombres que la app había añadido antes.
///
/// Devuelve (líneas nuevas, nombres que ahora gestiona la app). Un nombre que el usuario ya tenía oculto
/// por su cuenta no pasa a ser de la app, así no se le "destapa" al salir.
pub fn merge(lines: &[String], want: &BTreeSet<String>, managed_before: &[String]) -> (Vec<String>, Vec<String>) {
    let user: Vec<String> = lines
        .iter()
        .map(|l| l.trim().to_string())
        .filter(|l| !l.is_empty() && !managed_before.contains(l))
        .collect();
    let managed: Vec<String> = want.iter().filter(|n| !user.contains(n)).cloned().collect();
    let mut out = user;
    out.extend(managed.iter().cloned());
    (out, managed)
}

fn read_lines(file: &Path) -> io::Result<Vec<String>> {
    match std::fs::read_to_string(file) {
        Ok(text) => Ok(text.lines().map(str::to_string).collect()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(Vec::new()),
        Err(e) => Err(e),
    }
}

/// Aplica `want` sobre `<dir>/.hidden` y devuelve los nombres gestionados por la app.
/// Si el archivo queda vacío, se borra.
pub fn sync(dir: &Path, want: &BTreeSet<String>, managed_before: &[String]) -> io::Result<Vec<String>> {
    let file = dir.join(".hidden");
    let lines = read_lines(&file)?;
    let (out, managed) = merge(&lines, want, managed_before);
    let current: Vec<String> = lines.iter().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect();
    if out == current {
        return Ok(managed);
    }
    if out.is_empty() {
        match std::fs::remove_file(&file) {
            Err(e) if e.kind() != io::ErrorKind::NotFound => return Err(e),
            _ => {}
        }
    } else {
        let tmp = dir.join(".hidden.clonfences.tmp");
        std::fs::write(&tmp, out.join("\n") + "\n")?;
        std::fs::rename(tmp, &file)?;
    }
    Ok(managed)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn set(v: &[&str]) -> BTreeSet<String> {
        v.iter().map(|s| s.to_string()).collect()
    }
    fn strings(v: &[&str]) -> Vec<String> {
        v.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn keeps_user_lines() {
        // El usuario ocultaba "privado" y "a"; la app había añadido "viejo".
        let (out, managed) = merge(&strings(&["privado", "viejo", "a"]), &set(&["a", "b"]), &strings(&["viejo"]));
        assert_eq!(out, strings(&["privado", "a", "b"]));
        assert_eq!(managed, strings(&["b"]), "\"a\" ya era del usuario");
    }

    #[test]
    fn restore_removes_only_ours() {
        let (out, managed) = merge(&strings(&["privado", "b"]), &BTreeSet::new(), &strings(&["b"]));
        assert_eq!(out, strings(&["privado"]));
        assert!(managed.is_empty());
    }

    #[test]
    fn writes_and_deletes_file() {
        let dir = std::env::temp_dir().join(format!("clonfences-hidden-{}", crate::model::config::new_id()));
        std::fs::create_dir_all(&dir).unwrap();
        let managed = sync(&dir, &set(&["x", "y"]), &[]).unwrap();
        assert_eq!(std::fs::read_to_string(dir.join(".hidden")).unwrap(), "x\ny\n");
        let managed = sync(&dir, &BTreeSet::new(), &managed).unwrap();
        assert!(managed.is_empty());
        assert!(!dir.join(".hidden").exists());
        std::fs::remove_dir_all(dir).unwrap();
    }
}
