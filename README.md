# Clon Fences para COSMIC

Versión para Linux (Pop!_OS con COSMIC, Wayland) de Clon Fences, el organizador de escritorio inspirado en Stardock Fences.
Es una reescritura en Rust con [libcosmic](https://github.com/pop-os/libcosmic); la versión de Windows (C#/WPF)
sigue en su propio repositorio.

## Ejecutar

```bash
cargo run --release          # o: just run
cargo run -- --reset         # empezar con una configuración vacía
cargo test                   # pruebas de la lógica (sin pantalla)
just install                 # instala en ~/.local/bin y activa el inicio automático
```

`clonfences --autostart` / `--no-autostart` crea o quita `~/.config/autostart/clonfences.desktop`, que apunta al
ejecutable desde el que se llama. Sin inicio automático, tras reiniciar no hay fences y lo que contienen sigue oculto.

La primera vez crea un fence «Escritorio» arriba a la derecha con los archivos de `~/Escritorio`.

## Cómo funciona

Wayland no permite lo que hace la versión de Windows (hooks globales del ratón, colgar ventanas del escritorio de
Explorer, posicionar ventanas en coordenadas absolutas). Aquí se usa el protocolo **wlr-layer-shell**, que COSMIC soporta:

- Una superficie transparente a pantalla completa por monitor, en la capa `Bottom`: encima del fondo de pantalla y debajo
  de las ventanas, sin aparecer en Alt+Tab ni en el panel.
- Los fences se dibujan dentro de esa superficie, así que moverlos o redimensionarlos no necesita al compositor.
- La **región de entrada** se limita a los rectángulos de los fences; los clics fuera de ellos llegan al escritorio de
  COSMIC (`cosmic-files`) que está debajo.
- El **desenfoque** se pide al compositor sólo para esas mismas zonas.

## Estado

| Paso | Contenido | Estado |
|---|---|---|
| 1 | Núcleo independiente de la plataforma (`src/model`) con pruebas | ✅ |
| 2 | MVP: fence en el escritorio, iconos, abrir con doble clic, mover, redimensionar, enrollar, guardar | ✅ |
| 3 | Varios fences, crear/eliminar, portales de carpeta, organizar automáticamente, reglas | ⏳ |
| 4 | Bandeja (StatusNotifierItem), inicio automático (✅ por línea de órdenes), atajo para ocultar, papelera, menú contextual | ⏳ |
| 5 | Modo «escritorio completo»: doble clic en el escritorio, clic derecho + arrastrar para crear | ⏳ |

## Datos

- Lo que está en un fence se oculta del escritorio de COSMIC añadiéndolo a `~/Escritorio/.hidden` (el estándar que
  respetan cosmic-files y Nautilus). Al salir se quitan esas líneas; las que haya puesto el usuario no se tocan.
  `clonfences --restore` las quita sin abrir la app (por si se cerró de golpe).
- Configuración: `~/.config/ClonFences/config.json` (registro en `log.txt`). Otra carpeta: variable `CLONFENCES_DATA`.
- El formato es el mismo que el de Windows (`%APPDATA%\ClonFences\config.json`). Si se copia una configuración de Windows,
  las rutas `C:\…\Escritorio\archivo` se reasignan a `~/Escritorio/archivo` cuando ese archivo existe.
- Las coordenadas son píxeles lógicos relativos a la zona útil de cada monitor; `Output` guarda el nombre del monitor.

## Estructura

```
src/
  main.rs                 arranque, instancia única, --reset
  app.rs                  aplicación COSMIC: superficies layer-shell, vista de los fences, arrastre
  model/                  lógica pura (se prueba con cargo test)
    config.rs             AppConfig/FenceModel (JSON compatible con Windows), migración de rutas
    geometry.rs           mover, redimensionar, imantar, buscar sitio libre
    item.rs               FenceItem y ordenación
    rules.rs              categorías y reglas por extensión
    color.rs              colores "#RRGGBB"
  platform/               Linux/freedesktop
    autostart.rs          inicio automático (XDG Autostart)
    paths.rs              carpetas XDG
    store.rs              carga/guardado atómico de config.json
    scan.rs               leer carpetas y archivos
    desktop_entry.rs      lanzadores .desktop (nombre e icono localizados)
    icons.rs              icono del tema según tipo MIME
    launch.rs             abrir con xdg-open / gio launch
    log.rs                registro
```
