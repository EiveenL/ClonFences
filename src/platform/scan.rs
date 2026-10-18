//! Lectura del sistema de archivos: construir `FenceItem` a partir de rutas y carpetas.

use std::path::{Path, PathBuf};
use std::time::SystemTime;

use super::{desktop_entry, paths::is_hidden};
use crate::model::item::FenceItem;
use crate::model::rules::{extension_of, is_partial_download};

/// Información de un elemento, o `None` si ya no existe.
pub fn item_for(path: &Path) -> Option<FenceItem> {
    let meta = std::fs::metadata(path).ok()?;
    let is_dir = meta.is_dir();
    let extension = if is_dir { String::new() } else { extension_of(path) };
    let file_name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.display().to_string());
    let name = if extension == ".desktop" {
        desktop_entry::read(path).and_then(|e| e.name).unwrap_or(file_name)
    } else {
        file_name
    };
    Some(FenceItem {
        path: path.to_path_buf(),
        name,
        is_dir,
        modified: meta.modified().unwrap_or(SystemTime::UNIX_EPOCH),
        size: if is_dir { 0 } else { meta.len() },
        extension,
    })
}

/// Entradas visibles de una carpeta (sin ocultos ni descargas a medias).
pub fn list_dir(dir: &Path) -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Vec::new() };
    rd.flatten()
        .map(|e| e.path())
        .filter(|p| !is_hidden(p) && !is_partial_download(p))
        .collect()
}

/// Un elemento que falta se quita del fence sólo si su carpeta sigue existiendo
/// (así no se pierden elementos de discos desmontados).
pub fn should_prune(path: &Path) -> bool {
    path.parent().is_none_or(|d| d.is_dir())
}

/// Fecha de modificación de una carpeta: cambia cuando se crean, borran o renombran entradas.
pub fn dir_stamp(dir: &Path) -> Option<SystemTime> {
    std::fs::metadata(dir).and_then(|m| m.modified()).ok()
}
