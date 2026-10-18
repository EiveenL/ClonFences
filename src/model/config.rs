//! Modelo de configuración. Usa el mismo esquema JSON que la versión de Windows
//! (`%APPDATA%\ClonFences\config.json`), así un archivo puede pasar de un sistema a otro.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum SortMode {
    #[default]
    Manual,
    Name,
    Type,
    Date,
    Size,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct FenceModel {
    pub id: String,
    pub title: String,

    /// Posición y tamaño en píxeles lógicos, relativos a la zona útil del monitor
    /// (Height = altura desplegada).
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,

    pub rolled_up: bool,
    pub locked: bool,

    /// `None` = usar el valor predeterminado de la configuración.
    pub color: Option<String>,
    pub opacity: Option<f64>,
    pub icon_size: Option<i32>,

    /// Si tiene valor, el fence es un "portal" que muestra el contenido de esta carpeta.
    pub folder_path: Option<String>,

    /// Rutas de los elementos (fences normales), en orden manual.
    pub items: Vec<String>,

    pub sort: SortMode,

    /// Reglas de clasificación automática: extensiones (".pdf") o "carpeta".
    pub rules: Vec<String>,

    /// Sólo Linux: nombre del monitor (`wl_output`) donde vive el fence. La versión de Windows lo ignora.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output: Option<String>,
}

impl Default for FenceModel {
    fn default() -> Self {
        Self {
            id: new_id(),
            title: "Nuevo fence".into(),
            x: 40,
            y: 40,
            width: 360,
            height: 240,
            rolled_up: false,
            locked: false,
            color: None,
            opacity: None,
            icon_size: None,
            folder_path: None,
            items: Vec::new(),
            sort: SortMode::Manual,
            rules: Vec::new(),
            output: None,
        }
    }
}

impl FenceModel {
    pub fn is_portal(&self) -> bool {
        self.folder_path.as_deref().is_some_and(|p| !p.trim().is_empty())
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppSettings {
    pub quick_hide: bool,
    pub right_drag_create: bool,
    pub hide_desktop_icons: bool,
    pub auto_add_new_items: bool,
    pub default_fence_id: Option<String>,
    pub snap_enabled: bool,
    pub blur_behind: bool,
    pub peek_rolled_up: bool,
    pub lock_all: bool,
    pub default_icon_size: i32,
    pub default_color: String,
    pub default_opacity: f64,
    pub first_run: bool,

    /// Sólo Linux: ocultar del escritorio de COSMIC (vía `~/Escritorio/.hidden`) lo que ya está en un fence.
    pub hide_fenced_items: bool,
    /// Sólo Linux: nombres que la app añadió a `.hidden` (para quitarlos al salir sin tocar los del usuario).
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub hidden_on_desktop: Vec<String>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            quick_hide: true,
            right_drag_create: true,
            hide_desktop_icons: false,
            auto_add_new_items: true,
            default_fence_id: None,
            snap_enabled: true,
            blur_behind: true,
            peek_rolled_up: true,
            lock_all: false,
            default_icon_size: 48,
            default_color: "#1E1E1E".into(),
            default_opacity: 0.45,
            first_run: true,
            hide_fenced_items: true,
            hidden_on_desktop: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct LayoutSnapshot {
    pub name: String,
    /// Fecha ISO 8601; se guarda como texto para aceptar el formato de .NET sin conversiones.
    pub created: String,
    pub fences: Vec<FenceModel>,
}

impl Default for LayoutSnapshot {
    fn default() -> Self {
        Self {
            name: String::new(),
            created: chrono::Local::now().to_rfc3339(),
            fences: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppConfig {
    pub version: i32,
    pub settings: AppSettings,
    pub fences: Vec<FenceModel>,
    pub snapshots: Vec<LayoutSnapshot>,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            version: 1,
            settings: AppSettings::default(),
            fences: Vec::new(),
            snapshots: Vec::new(),
        }
    }
}

pub fn new_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()
}

/// ¿Es una ruta de Windows (`C:\…`, `\\servidor\…`) o un elemento virtual del shell (`::{GUID}`)?
pub fn is_foreign_path(path: &str) -> bool {
    let b = path.as_bytes();
    path.starts_with("::")
        || path.starts_with("\\\\")
        || (b.len() >= 2 && b[0].is_ascii_alphabetic() && b[1] == b':')
}

/// Último componente de una ruta de Windows.
fn windows_file_name(path: &str) -> Option<&str> {
    path.trim_end_matches(['\\', '/'])
        .rsplit(['\\', '/'])
        .next()
        .filter(|n| !n.is_empty())
}

impl AppConfig {
    pub fn normalize(&mut self) {
        let mut ids = HashSet::new();
        for f in &mut self.fences {
            if f.id.is_empty() || !ids.insert(f.id.clone()) {
                f.id = new_id();
                ids.insert(f.id.clone());
            }
            let mut seen = HashSet::new();
            f.items.retain(|p| !p.trim().is_empty() && seen.insert(p.clone()));
            f.width = f.width.max(120);
            f.height = f.height.max(80);
        }
        let s = &mut self.settings;
        s.default_icon_size = s.default_icon_size.clamp(16, 256);
        s.default_opacity = s.default_opacity.clamp(0.0, 1.0);
        if s.default_color.trim().is_empty() {
            s.default_color = AppSettings::default().default_color;
        }
    }

    /// Adapta una configuración traída de Windows: cada elemento `C:\…\Escritorio\archivo` pasa a
    /// `<desktop>/archivo` si ese archivo existe aquí; el resto de rutas de Windows se descarta.
    /// Devuelve cuántas rutas se reasignaron.
    pub fn migrate_foreign_paths(&mut self, desktop: &Path) -> usize {
        let mut migrated = 0;
        for f in &mut self.fences {
            let mut seen = HashSet::new();
            let mut out = Vec::with_capacity(f.items.len());
            for p in f.items.drain(..) {
                let mapped = if is_foreign_path(&p) {
                    windows_file_name(&p)
                        .filter(|_| !p.starts_with("::"))
                        .map(|n| desktop.join(n))
                        .filter(|c| c.exists())
                        .map(|c| {
                            migrated += 1;
                            c.to_string_lossy().into_owned()
                        })
                } else {
                    Some(p)
                };
                if let Some(m) = mapped
                    && seen.insert(m.clone())
                {
                    out.push(m);
                }
            }
            f.items = out;
            if f.folder_path.as_deref().is_some_and(is_foreign_path) {
                f.folder_path = None;
            }
        }
        migrated
    }

    pub fn fence(&self, id: &str) -> Option<&FenceModel> {
        self.fences.iter().find(|f| f.id == id)
    }

    pub fn fence_mut(&mut self, id: &str) -> Option<&mut FenceModel> {
        self.fences.iter_mut().find(|f| f.id == id)
    }

    /// Todas las rutas que ya están en algún fence normal.
    pub fn fenced_paths(&self) -> HashSet<PathBuf> {
        self.fences
            .iter()
            .filter(|f| !f.is_portal())
            .flat_map(|f| f.items.iter().map(PathBuf::from))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const WINDOWS_JSON: &str = r##"{
      "Version": 1,
      "Settings": { "QuickHide": true, "HideDesktopIcons": true, "DefaultIconSize": 999,
                    "DefaultColor": "#1F4E8C", "DefaultOpacity": 0.6, "FirstRun": false },
      "Fences": [
        { "Id": "abc", "Title": "Documentos", "X": 10, "Y": 20, "Width": 50, "Height": 300,
          "RolledUp": true, "Color": null, "Items": ["C:\\Users\\ana\\Desktop\\informe.pdf", "::{645FF040-5081-101B-9F08-00AA002F954E}"],
          "Sort": "Name", "Rules": [".pdf"] },
        { "Id": "abc", "Title": "Portal", "FolderPath": "D:\\Fotos", "Items": [] }
      ],
      "Snapshots": [ { "Name": "Antes", "Created": "2026-10-17T12:00:00.1234567-05:00", "Fences": [] } ]
    }"##;

    #[test]
    fn reads_windows_config() {
        let mut cfg: AppConfig = serde_json::from_str(WINDOWS_JSON).unwrap();
        cfg.normalize();
        assert_eq!(cfg.settings.default_icon_size, 256);
        assert!(!cfg.settings.first_run);
        assert!(cfg.settings.blur_behind, "un campo ausente toma el valor predeterminado");
        let f = &cfg.fences[0];
        assert_eq!((f.x, f.y, f.width, f.height), (10, 20, 120, 300));
        assert!(f.rolled_up);
        assert_eq!(f.sort, SortMode::Name);
        assert_ne!(cfg.fences[0].id, cfg.fences[1].id, "ids duplicados se regeneran");
        assert!(cfg.fences[1].is_portal());
        assert_eq!(cfg.snapshots[0].name, "Antes");
    }

    #[test]
    fn migrates_windows_paths_to_desktop() {
        let dir = std::env::temp_dir().join(format!("clonfences-test-{}", new_id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("informe.pdf"), b"x").unwrap();

        let mut cfg: AppConfig = serde_json::from_str(WINDOWS_JSON).unwrap();
        assert_eq!(cfg.migrate_foreign_paths(&dir), 1);
        assert_eq!(cfg.fences[0].items, vec![dir.join("informe.pdf").to_string_lossy().into_owned()]);
        assert!(!cfg.fences[1].is_portal(), "un portal a D:\\ no existe en Linux");
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn round_trip_uses_pascal_case() {
        let cfg = AppConfig::default();
        let json = serde_json::to_string(&cfg).unwrap();
        assert!(json.contains("\"DefaultOpacity\""));
        assert!(!json.contains("\"Output\""));
        let back: AppConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(back, cfg);
    }

    #[test]
    fn foreign_paths() {
        assert!(is_foreign_path("C:\\x"));
        assert!(is_foreign_path("::{GUID}"));
        assert!(is_foreign_path("\\\\srv\\share"));
        assert!(!is_foreign_path("/home/x/C:algo"));
    }
}
