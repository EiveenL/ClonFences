//! Lectura mínima de lanzadores `.desktop` (equivalente Linux de los `.lnk`).

use std::path::Path;

#[derive(Debug, Default, Clone, PartialEq)]
pub struct DesktopEntry {
    pub name: Option<String>,
    pub icon: Option<String>,
}

/// Idiomas preferidos a partir de `LANGUAGE`/`LC_ALL`/`LC_MESSAGES`/`LANG` (p. ej. "es_CL.UTF-8" → ["es_CL", "es"]).
fn locales() -> Vec<String> {
    let raw = ["LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"]
        .iter()
        .find_map(|v| std::env::var(v).ok().filter(|s| !s.is_empty()))
        .unwrap_or_default();
    let mut out = Vec::new();
    for part in raw.split(':') {
        let base = part.split(['.', '@']).next().unwrap_or("");
        if base.is_empty() || base == "C" || base == "POSIX" {
            continue;
        }
        out.push(base.to_string());
        if let Some((lang, _)) = base.split_once('_') {
            out.push(lang.to_string());
        }
    }
    out
}

pub fn parse(text: &str, locales: &[String]) -> DesktopEntry {
    let mut in_main = false;
    let mut name = None;
    let mut icon = None;
    // (prioridad, valor): menor índice = idioma preferido.
    let mut localized: Option<(usize, String)> = None;
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            in_main = line == "[Desktop Entry]";
            continue;
        }
        if !in_main || line.starts_with('#') {
            continue;
        }
        let Some((key, value)) = line.split_once('=') else { continue };
        let (key, value) = (key.trim(), value.trim().to_string());
        match key {
            "Name" => name = Some(value),
            "Icon" => icon = Some(value),
            _ => {
                if let Some(loc) = key.strip_prefix("Name[").and_then(|k| k.strip_suffix(']'))
                    && let Some(rank) = locales.iter().position(|l| l == loc)
                    && localized.as_ref().is_none_or(|(r, _)| rank < *r)
                {
                    localized = Some((rank, value));
                }
            }
        }
    }
    DesktopEntry {
        name: localized.map(|(_, v)| v).or(name).filter(|s| !s.is_empty()),
        icon: icon.filter(|s| !s.is_empty()),
    }
}

pub fn read(path: &Path) -> Option<DesktopEntry> {
    let text = std::fs::read_to_string(path).ok()?;
    Some(parse(&text, &locales()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_localized_name() {
        let text = "[Desktop Entry]\nName=Files\nName[es]=Archivos\nName[es_CL]=Archivos CL\nIcon=system-file-manager\n\
                    [Desktop Action new]\nName=New Window\n";
        let e = parse(text, &["es_CL".into(), "es".into()]);
        assert_eq!(e.name.as_deref(), Some("Archivos CL"));
        assert_eq!(e.icon.as_deref(), Some("system-file-manager"));
        let e = parse(text, &["fr".into()]);
        assert_eq!(e.name.as_deref(), Some("Files"));
    }
}
