//! Elemento visible dentro de un fence y su ordenación.

use std::cmp::Ordering;
use std::path::PathBuf;
use std::time::SystemTime;

use super::config::SortMode;

#[derive(Debug, Clone, PartialEq)]
pub struct FenceItem {
    pub path: PathBuf,
    /// Nombre a mostrar (para un `.desktop`, su `Name=`).
    pub name: String,
    pub is_dir: bool,
    pub modified: SystemTime,
    pub size: u64,
    /// Extensión en minúsculas con punto, o "" (carpetas y archivos sin extensión).
    pub extension: String,
}

fn by_name(a: &FenceItem, b: &FenceItem) -> Ordering {
    a.name.to_lowercase().cmp(&b.name.to_lowercase()).then_with(|| a.name.cmp(&b.name))
}

/// Carpetas primero.
fn dirs_first(a: &FenceItem, b: &FenceItem) -> Ordering {
    b.is_dir.cmp(&a.is_dir)
}

/// Ordena según el modo del fence. En modo manual un fence normal respeta el orden guardado;
/// un portal no tiene orden manual y usa carpetas primero + nombre.
pub fn sort_items(items: &mut [FenceItem], mode: SortMode, is_portal: bool) {
    match mode {
        SortMode::Name => items.sort_by(|a, b| dirs_first(a, b).then_with(|| by_name(a, b))),
        SortMode::Type => items.sort_by(|a, b| {
            dirs_first(a, b).then_with(|| a.extension.cmp(&b.extension)).then_with(|| by_name(a, b))
        }),
        SortMode::Date => items.sort_by(|a, b| b.modified.cmp(&a.modified)),
        SortMode::Size => items.sort_by(|a, b| dirs_first(a, b).then_with(|| b.size.cmp(&a.size))),
        SortMode::Manual if is_portal => {
            items.sort_by(|a, b| dirs_first(a, b).then_with(|| by_name(a, b)))
        }
        SortMode::Manual => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn item(name: &str, is_dir: bool, secs: u64, size: u64) -> FenceItem {
        FenceItem {
            path: PathBuf::from(format!("/d/{name}")),
            name: name.into(),
            is_dir,
            modified: SystemTime::UNIX_EPOCH + Duration::from_secs(secs),
            size,
            extension: if is_dir { String::new() } else { super::super::rules::extension_of(name.as_ref()) },
        }
    }

    fn names(v: &[FenceItem]) -> Vec<&str> {
        v.iter().map(|i| i.name.as_str()).collect()
    }

    #[test]
    fn sorts() {
        let base = vec![item("b.txt", false, 3, 10), item("Zeta", true, 1, 0), item("a.pdf", false, 2, 99)];

        let mut v = base.clone();
        sort_items(&mut v, SortMode::Name, false);
        assert_eq!(names(&v), ["Zeta", "a.pdf", "b.txt"]);

        let mut v = base.clone();
        sort_items(&mut v, SortMode::Date, false);
        assert_eq!(names(&v), ["b.txt", "a.pdf", "Zeta"]);

        let mut v = base.clone();
        sort_items(&mut v, SortMode::Size, false);
        assert_eq!(names(&v), ["Zeta", "a.pdf", "b.txt"]);

        let mut v = base.clone();
        sort_items(&mut v, SortMode::Manual, false);
        assert_eq!(v, base);

        let mut v = base.clone();
        sort_items(&mut v, SortMode::Manual, true);
        assert_eq!(names(&v), ["Zeta", "a.pdf", "b.txt"]);
    }
}
