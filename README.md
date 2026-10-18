# Cercado

**Pon orden en tu escritorio, en Windows y en Linux.**

Cercado (antes *Clon Fences*) agrupa los iconos del escritorio en **cercos**: paneles semitransparentes que viven
sobre el fondo de pantalla, se pueden mover, redimensionar y enrollar, y se colocan solos en su sitio. Está inspirado
en Stardock Fences, pero es una implementación propia, sin código ni recursos de Stardock.

| | Windows | Linux |
|---|---|---|
| Sistemas | Windows 10 y 11 | Pop!_OS 24.04 con escritorio COSMIC (Wayland) |
| Tecnología | C# / WPF sobre .NET 10 | Rust + [libcosmic](https://github.com/pop-os/libcosmic), protocolo wlr-layer-shell |
| Carpeta | [`ClonFences/`](ClonFences) | [`ClonFences-Linux/`](ClonFences-Linux) |
| Estado | ✅ Versión 1.0 completa | 🚧 MVP usable: un cerco, arrastrar y soltar, inicio automático |
| Configuración | `%APPDATA%\ClonFences\config.json` | `~/.config/ClonFences/config.json` |

Las dos versiones guardan la configuración en el mismo formato JSON. Si copias el `config.json` de Windows a Linux,
las rutas `C:\…\Escritorio\archivo` se convierten en `~/Escritorio/archivo` cuando ese archivo existe.

> El nombre interno del programa sigue siendo `ClonFences` / `clonfences` (ejecutables, carpetas de datos, inicio
> automático), así que las configuraciones existentes siguen funcionando.

## Qué hace

| Función | Windows | Linux |
|---|:-:|:-:|
| Cercos con los archivos del escritorio, que se ocultan del escritorio mientras están en un cerco | ✅ | ✅ |
| Abrir con doble clic, mover, redimensionar, enrollar, imantar a bordes y a otros cercos | ✅ | ✅ |
| Meter y sacar elementos arrastrándolos desde o hacia el escritorio y el explorador de archivos | ✅ | ✅ |
| Renombrar y bloquear cercos, menú propio con clic derecho | ✅ | ✅ |
| Iniciar al encender el equipo | ✅ | ✅ |
| Varios monitores | ✅ | ✅ |
| Varios cercos, crear uno con clic derecho + arrastrar | ✅ | ⏳ |
| Portales (un cerco que muestra en vivo una carpeta) | ✅ | ⏳ |
| Organizar el escritorio automáticamente y reglas por extensión | ✅ | ⏳ |
| Doble clic en el escritorio para ocultar todo | ✅ | ⏳ |
| Icono en la bandeja, instantáneas del diseño, papelera, menú contextual del sistema | ✅ | ⏳ |

Detalle de cada versión: [README de Windows](ClonFences/README.md) · [README de Linux](ClonFences-Linux/README.md).

## Despliegue en Windows

**Requisitos:** Windows 10 u 11, el [SDK de .NET 10](https://dotnet.microsoft.com/download) para compilar y Git.
El ejecutable resultante necesita el *.NET 10 Desktop Runtime* en el equipo donde se use (lo incluye el SDK).

```powershell
git clone https://github.com/EiveenL/ClonFences.git
cd ClonFences
dotnet publish ClonFences\ClonFences.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=none -o dist
.\dist\ClonFences.exe
```

1. La primera vez pregunta si quieres organizar el escritorio. Si aceptas, crea cercos por tipo de archivo y oculta
   los iconos de Windows. Tus archivos **no se mueven** de la carpeta Escritorio.
2. Para que arranque con Windows: icono de la bandeja ▸ *Iniciar con Windows* (se guarda en
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
3. Para quitarlo: desactiva el inicio con Windows, sal desde la bandeja (los iconos del escritorio vuelven a verse) y
   borra la carpeta `dist` y `%APPDATA%\ClonFences`.

`ClonFences.exe --reset` arranca con una configuración vacía.

## Despliegue en Linux

**Requisitos:** Pop!_OS 24.04 con COSMIC en Wayland (otros compositores con wlr-layer-shell pueden funcionar, pero no
se han probado), Rust 1.85 o posterior (edición 2024) y las dependencias de libcosmic.

```bash
# 1. Rust (si no lo tienes) y dependencias de compilación
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
sudo apt install cmake just libexpat1-dev libfontconfig-dev libfreetype-dev libxkbcommon-dev pkgconf

# 2. Compilar, instalar en ~/.local/bin y activar el inicio automático
git clone https://github.com/EiveenL/ClonFences.git
cd ClonFences/ClonFences-Linux
just install

# 3. Abrirlo ahora (después arrancará solo al iniciar sesión)
setsid -f ~/.local/bin/clonfences
```

Sin `just`, el paso 2 equivale a:

```bash
cargo build --release
install -Dm755 target/release/clonfences ~/.local/bin/clonfences
~/.local/bin/clonfences --autostart
```

1. La primera vez crea un cerco «Escritorio» arriba a la derecha con los archivos de `~/Escritorio`.
2. Lo que entra en un cerco desaparece del escritorio de COSMIC (se anota en `~/Escritorio/.hidden`) y vuelve a
   verse al cerrar la app.
3. Reiniciarlo a mano: `pkill clonfences; setsid -f ~/.local/bin/clonfences`.
4. Quitarlo: `just uninstall` (desactiva el inicio automático, restaura el escritorio y borra el ejecutable).
   `clonfences --restore` devuelve los iconos al escritorio si la app se cerró de golpe.

## Retos y complicaciones

Lo que más costó, y cómo se resolvió.

### En Windows

- **Quedarse pegado al escritorio.** Una ventana normal sale en Alt+Tab, en la barra de tareas y se pone delante
  al hacer clic. Cada cerco es una ventana WPF transparente cuyo propietario es la ventana del escritorio de Explorer
  (`Progman` / `WorkerW`). Así se queda siempre al fondo, encima del fondo de pantalla.
- **Explorer se reinicia.** Cuando Explorer se cuelga o se reinicia, las ventanas que dependen de él mueren con él.
  La app lo detecta y vuelve a crear los cercos.
- **Doble clic en un hueco del escritorio.** Windows no avisa de eso. Hizo falta un hook global del ratón
  (`WH_MOUSE_LL`) y UI Automation para saber si bajo el cursor hay un icono o un hueco vacío.
- **Sentirse como Explorer.** El clic derecho muestra el menú contextual real de Windows (`IContextMenu` por COM),
  las operaciones de archivo usan `SHFileOperation` para que la papelera y el deshacer funcionen, y los iconos y
  miniaturas se cargan en un hilo STA aparte para no congelar la interfaz.
- **Ocultar los iconos sin tocar los archivos.** Se usa la misma opción que *Ver ▸ Mostrar iconos del escritorio*;
  los archivos nunca salen de la carpeta Escritorio.

### En Linux (COSMIC / Wayland)

- **Wayland no permite nada de lo anterior.** No hay hooks globales del ratón, no se puede colgar una ventana del
  escritorio de otro programa ni colocar ventanas en coordenadas absolutas. Se descartó Avalonia (en COSMIC habría
  ido por XWayland, sin acceso a layer-shell) y se reescribió en Rust con libcosmic, el kit del propio escritorio.
- **Dibujar sobre el fondo de pantalla.** Se usa el protocolo **wlr-layer-shell**: una superficie transparente a
  pantalla completa por monitor, en la capa *Bottom* (encima del fondo y debajo de las ventanas). Los cercos se
  dibujan dentro de ella, así que moverlos no necesita la ayuda del compositor.
- **Que los clics atraviesen lo transparente.** La región de entrada de la superficie se limita a los rectángulos de
  los cercos; fuera de ellos, los clics llegan al escritorio de COSMIC que está debajo. Hay que recalcularla cada vez
  que un cerco se mueve, se enrolla o abre un menú.
- **Ocultar iconos del escritorio de otro programa.** El escritorio lo dibuja `cosmic-files`. La solución fue el
  archivo estándar `~/Escritorio/.hidden`: la app añade y quita solo sus propias líneas al salir (también ante
  SIGTERM al cerrar sesión), y `--restore` arregla el caso de un cierre brusco.
- **Arrastrar y soltar que no hacía nada.** El widget que posiciona cada cerco no propagaba al compositor las zonas
  donde se puede soltar. Ahora hay una sola zona por monitor y el cerco de destino se calcula a partir del punto
  donde se suelta.
- **Apagar el monitor dejaba el cerco «muerto».** Al cambiar entre Windows y Linux con el mismo monitor, o al
  apagarlo, Linux ve que el monitor se desconecta y vuelve. La superficie que se crea de nuevo se dibuja pero no
  recibe clics. Volver a enviar la región de entrada no lo arregló y la causa exacta sigue sin aclararse, así que, como un
  proceso nuevo sí funciona, la app se reinicia sola (`exec`) dos segundos después de la reconexión.
- **Inicio automático con espacios en la ruta.** El lanzador de XDG Autostart necesita entrecomillar la ruta del
  ejecutable según la especificación de Desktop Entry (por ejemplo, una carpeta llamada `Proyectos Code`).
- **Una sola configuración para dos sistemas.** El JSON es compatible con el de Windows, incluida la conversión de
  rutas `C:\…` a rutas de Linux.

## Estructura del repositorio

```
ClonFences/          versión de Windows (C# / WPF, .NET 10)
ClonFences-Linux/    versión de Linux (Rust + libcosmic)
  src/model/         lógica independiente de la plataforma, con pruebas (cargo test)
  src/platform/      integración con Linux / freedesktop
  src/app.rs         aplicación COSMIC: superficies layer-shell, cercos, arrastre
```

## Aviso

Proyecto personal sin relación con Stardock Corporation. *Fences* es una marca de Stardock.
