//! Clasificación por tipo: categorías de "Organizar escritorio" y reglas de cada fence.

use std::path::Path;

/// Regla especial que coincide con cualquier carpeta.
pub const FOLDER_RULE: &str = "carpeta";

pub struct Category {
    pub title: &'static str,
    pub rules: &'static [&'static str],
}

/// Categorías en orden; la última ("Otros") no tiene reglas y recoge el resto.
pub const CATEGORIES: &[Category] = &[
    Category {
        title: "Programas",
        rules: &[
            ".desktop", ".appimage", ".sh", ".run", ".flatpakref", ".py",
            // Accesos de Windows que pueden venir en una configuración migrada.
            ".lnk", ".url", ".exe",
        ],
    },
    Category { title: "Carpetas", rules: &[FOLDER_RULE] },
    Category {
        title: "Documentos",
        rules: &[
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".txt", ".rtf", ".odt",
            ".ods", ".odp", ".csv", ".md", ".json", ".xml", ".epub",
        ],
    },
    Category {
        title: "Imágenes y multimedia",
        rules: &[
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".heic", ".tif", ".tiff",
            ".psd", ".xcf", ".kra", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mp3", ".wav",
            ".flac", ".m4a", ".ogg", ".opus",
        ],
    },
    Category {
        title: "Comprimidos e instaladores",
        rules: &[
            ".zip", ".rar", ".7z", ".tar", ".gz", ".xz", ".bz2", ".zst", ".tgz", ".deb", ".rpm", ".iso",
            ".msi", ".msix",
        ],
    },
    Category { title: "Otros", rules: &[] },
];

/// Descargas a medio terminar: se ignoran hasta que se renombran.
pub const PARTIAL_EXTENSIONS: &[&str] =
    &[".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!qb"];

/// Extensión en minúsculas con punto (".pdf"), o "" si no tiene.
pub fn extension_of(path: &Path) -> String {
    path.extension()
        .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
        .unwrap_or_default()
}

pub fn is_partial_download(path: &Path) -> bool {
    PARTIAL_EXTENSIONS.contains(&extension_of(path).as_str())
}

/// ¿Alguna de `rules` acepta este elemento?
pub fn rules_match<S: AsRef<str>>(rules: &[S], extension: &str, is_dir: bool) -> bool {
    rules.iter().any(|r| {
        let r = r.as_ref();
        if is_dir { r == FOLDER_RULE } else { r.eq_ignore_ascii_case(extension) }
    })
}

/// Título de la categoría a la que pertenece un elemento.
pub fn categorize(extension: &str, is_dir: bool) -> &'static str {
    CATEGORIES
        .iter()
        .find(|c| c.rules.is_empty() || rules_match(c.rules, extension, is_dir))
        .map(|c| c.title)
        .unwrap_or("Otros")
}

/// Convierte el texto que escribe el usuario ("pdf, .DOCX carpeta") en reglas normalizadas.
pub fn parse_rules(text: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for raw in text.split(|c: char| c.is_whitespace() || c == ',' || c == ';') {
        let t = raw.trim().to_lowercase();
        if t.is_empty() {
            continue;
        }
        let rule = if t == FOLDER_RULE || t.starts_with('.') { t } else { format!(".{t}") };
        if !out.contains(&rule) {
            out.push(rule);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn categorizes() {
        assert_eq!(categorize(".pdf", false), "Documentos");
        assert_eq!(categorize(".desktop", false), "Programas");
        assert_eq!(categorize("", true), "Carpetas");
        assert_eq!(categorize(".xyz", false), "Otros");
        assert_eq!(categorize("", false), "Otros");
    }

    #[test]
    fn extensions_and_partials() {
        assert_eq!(extension_of(Path::new("/a/Foto.JPG")), ".jpg");
        assert_eq!(extension_of(Path::new("/a/sin")), "");
        assert!(is_partial_download(Path::new("/a/x.iso.part")));
    }

    #[test]
    fn parses_rules() {
        assert_eq!(parse_rules("pdf, .DOCX  carpeta;pdf"), vec![".pdf", ".docx", "carpeta"]);
        assert!(rules_match(&parse_rules("pdf"), ".PDF", false));
        assert!(!rules_match(&parse_rules("pdf"), "", true));
    }
}
