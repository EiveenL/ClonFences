//! Carga y guardado de `config.json`.

use std::io;
use std::path::Path;

use super::{log::log, paths};
use crate::model::config::AppConfig;

pub fn load() -> AppConfig {
    let path = paths::config_file();
    let mut cfg = match std::fs::read_to_string(&path) {
        Ok(text) => match serde_json::from_str::<AppConfig>(&text) {
            Ok(cfg) => cfg,
            Err(e) => {
                // No perder el archivo dañado: apartarlo y empezar de cero.
                let bad = path.with_extension("json.bad");
                let _ = std::fs::rename(&path, &bad);
                log(format!("config.json no válido ({e}); copiado a {}", bad.display()));
                AppConfig::default()
            }
        },
        Err(e) if e.kind() == io::ErrorKind::NotFound => AppConfig::default(),
        Err(e) => {
            log(format!("No se pudo leer {}: {e}", path.display()));
            AppConfig::default()
        }
    };
    cfg.normalize();
    let migrated = cfg.migrate_foreign_paths(&paths::desktop_dir());
    if migrated > 0 {
        log(format!("{migrated} rutas de Windows reasignadas al escritorio de Linux"));
    }
    cfg
}

/// Escritura atómica: archivo temporal + rename, para no dejar un JSON a medias si algo falla.
pub fn save(cfg: &AppConfig) -> io::Result<()> {
    save_to(cfg, &paths::config_file())
}

fn save_to(cfg: &AppConfig, path: &Path) -> io::Result<()> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let json = serde_json::to_string_pretty(cfg).map_err(io::Error::other)?;
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, json)?;
    std::fs::rename(tmp, path)
}
