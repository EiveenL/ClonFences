//! Clon Fences para COSMIC (Wayland).

mod app;
mod model;
mod platform;

use std::fs::File;

use platform::{autostart, log::log, paths, store};

fn main() -> cosmic::iced::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "--help" || a == "-h") {
        println!(
            "Uso: clonfences [--reset | --restore | --autostart | --no-autostart]\n  \
             --reset         aparta la configuración actual (config.json.bak) y empieza de cero\n  \
             --restore       vuelve a mostrar en el escritorio de COSMIC lo que la app ocultó, y sale\n  \
             --autostart     inicia Clon Fences (este ejecutable) al iniciar sesión, y sale\n  \
             --no-autostart  deja de iniciarlo al iniciar sesión, y sale"
        );
        return Ok(());
    }

    for (flag, enabled) in [("--autostart", true), ("--no-autostart", false)] {
        if args.iter().any(|a| a == flag) {
            match autostart::set_enabled(enabled) {
                Ok(file) if enabled => println!("Inicio automático activado ({}).", file.display()),
                Ok(_) => println!("Inicio automático desactivado."),
                Err(e) => eprintln!("No se pudo cambiar el inicio automático: {e}"),
            }
            return Ok(());
        }
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

    if args.iter().any(|a| a == "--restore") {
        match restore_desktop(config) {
            Ok(()) => println!("Elementos del escritorio restaurados."),
            Err(e) => eprintln!("{e}"),
        }
        return Ok(());
    }

    if !autostart::is_enabled() {
        log("Aviso: el inicio automático está desactivado (clonfences --autostart lo activa)");
    }

    let settings = cosmic::app::Settings::default()
        .no_main_window(true)
        .transparent(true)
        .exit_on_close(false);
    let result = cosmic::app::run::<app::App>(settings, app::Flags { config });

    // Si la app terminó sin pasar por su salida normal (p. ej. el compositor se cerró al apagar),
    // lo que ocultó seguiría oculto en el escritorio aunque los fences ya no se vean.
    let config = store::load();
    if !config.settings.hidden_on_desktop.is_empty() {
        log(format!("Salida sin restaurar el escritorio ({result:?}); restaurando"));
        if let Err(e) = restore_desktop(config) {
            log(e);
        }
    }
    drop(lock);
    result
}

/// Quita de `~/Escritorio/.hidden` lo que añadió la app y guarda la configuración.
fn restore_desktop(mut config: model::config::AppConfig) -> Result<(), String> {
    let desktop = paths::desktop_dir();
    platform::hidden::sync(&desktop, &Default::default(), &config.settings.hidden_on_desktop)
        .map_err(|e| format!("No se pudo actualizar {}/.hidden: {e}", desktop.display()))?;
    config.settings.hidden_on_desktop.clear();
    store::save(&config).map_err(|e| format!("No se pudo guardar la configuración: {e}"))
}
