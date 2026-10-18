//! Clon Fences para COSMIC (Wayland).

mod app;
mod model;
mod platform;

use std::fs::File;

use platform::{log::log, paths, store};

fn main() -> cosmic::iced::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "--help" || a == "-h") {
        println!("Uso: clonfences [--reset]\n  --reset  aparta la configuración actual (config.json.bak) y empieza de cero");
        return Ok(());
    }

    // Instancia única: un bloqueo sobre un archivo de la carpeta de datos.
    let _ = std::fs::create_dir_all(paths::data_dir());
    let lock = File::create(paths::data_dir().join("instance.lock")).expect("no se pudo crear instance.lock");
    if lock.try_lock().is_err() {
        eprintln!("Clon Fences ya se está ejecutando.");
        return Ok(());
    }

    if args.iter().any(|a| a == "--reset") {
        let cfg = paths::config_file();
        if cfg.exists() {
            let _ = std::fs::rename(&cfg, cfg.with_extension("json.bak"));
            log("--reset: configuración anterior guardada como config.json.bak");
        }
    }

    let config = store::load();
    let settings = cosmic::app::Settings::default()
        .no_main_window(true)
        .transparent(true)
        .exit_on_close(false);
    let result = cosmic::app::run::<app::App>(settings, app::Flags { config });
    drop(lock);
    result
}
