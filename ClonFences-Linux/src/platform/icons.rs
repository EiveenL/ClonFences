//! Qué icono freedesktop corresponde a cada elemento (nombres del tema de iconos, de más a menos específico).

use std::path::{Path, PathBuf};

use crate::model::item::FenceItem;

#[derive(Debug, Clone, PartialEq)]
pub enum IconSpec {
    /// Archivo de imagen concreto (p. ej. `Icon=/opt/app/icon.png` en un `.desktop`).
    File(PathBuf),
    /// Nombres a buscar en el tema, en orden.
    Names(Vec<String>),
}

fn special_folder_icon(path: &Path) -> Option<&'static str> {
    let pairs = [
        (dirs::document_dir(), "folder-documents"),
        (dirs::download_dir(), "folder-download"),
        (dirs::picture_dir(), "folder-pictures"),
        (dirs::audio_dir(), "folder-music"),
        (dirs::video_dir(), "folder-videos"),
        (dirs::template_dir(), "folder-templates"),
        (dirs::public_dir(), "folder-publicshare"),
        (dirs::home_dir(), "user-home"),
    ];
    pairs.into_iter().find(|(d, _)| d.as_deref() == Some(path)).map(|(_, n)| n)
}

pub fn icon_for(item: &FenceItem) -> IconSpec {
    if item.is_dir {
        let mut names = Vec::new();
        if let Some(special) = special_folder_icon(&item.path) {
            names.push(special.to_string());
        }
        names.push("folder".into());
        return IconSpec::Names(names);
    }

    if item.extension == ".desktop"
        && let Some(icon) = super::desktop_entry::read(&item.path).and_then(|e| e.icon)
    {
        let p = Path::new(&icon);
        if p.is_absolute() {
            if p.is_file() {
                return IconSpec::File(p.to_path_buf());
            }
        } else {
            return IconSpec::Names(vec![icon, "application-x-executable".into()]);
        }
    }

    let mut names = Vec::new();
    if let Some(mime) = mime_guess::from_path(&item.path).first() {
        // "image/png" → "image-png", y como respaldo "image-x-generic".
        names.push(format!("{}-{}", mime.type_(), mime.subtype()));
        names.push(format!("{}-x-generic", mime.type_()));
    }
    match item.extension.as_str() {
        ".desktop" | ".appimage" | ".run" => names.push("application-x-executable".into()),
        ".sh" => names.push("text-x-script".into()),
        _ => {}
    }
    names.push("text-x-generic".into());
    IconSpec::Names(names)
}
