# Cercado para Windows (Clon Fences)

Organizador de escritorio para Windows 10/11 inspirado en Stardock Fences. Es una implementación propia
en C# / WPF (.NET 10), sin código ni recursos de Stardock.

## Ejecutar

Los comandos se ejecutan desde la raíz del repositorio. Guía completa de despliegue en el [README principal](../README.md).

```powershell
.\dist\ClonFences.exe
```

La primera vez pregunta si quieres organizar el escritorio. Si aceptas, crea fences por tipo y oculta los
iconos de Windows. Tus archivos no se mueven de la carpeta Escritorio. Al salir de la app, los iconos vuelven a aparecer.

Compilar y publicar de nuevo:

```powershell
dotnet build ClonFences\ClonFences.csproj
dotnet publish ClonFences\ClonFences.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

## Funciones

| Función | Cómo se usa |
|---|---|
| Crear un fence | Clic derecho + arrastrar sobre el escritorio → «Crear fence aquí». También desde la bandeja o el menú de un fence |
| Portal de carpeta | Muestra en vivo cualquier carpeta; doble clic en una subcarpeta para entrar y ◀ para volver |
| Ocultación rápida | Doble clic en un hueco vacío del escritorio oculta o muestra todos los fences (y los iconos) |
| Enrollar | Doble clic en el título. Si pasas el ratón sobre un fence enrollado, se asoma |
| Mover / redimensionar | Arrastra el título o los bordes; se imanta a los bordes de la pantalla y a otros fences |
| Organizar escritorio | Bandeja → «Organizar el escritorio automáticamente» (Programas, Carpetas, Documentos…) |
| Reglas | Menú del fence → «Reglas de clasificación…» (p. ej. `.pdf .docx`); los archivos nuevos del escritorio van a su fence |
| Menú de Windows | Clic derecho sobre un elemento: el menú contextual real de Explorer (Abrir con, Enviar a, Propiedades…) |
| Arrastrar y soltar | Entre fences, desde/hacia el Explorador, sobre carpetas, reordenar a mano. Ctrl = copiar, Mayús = mover |
| Teclado | Intro abrir · Supr papelera · Mayús+Supr borrar · F2 renombrar · Ctrl+A/C/X/V · Retroceso (portal) · F5 |
| Apariencia | Color, opacidad, desenfoque, tamaño de iconos, orden (manual, nombre, tipo, fecha, tamaño) |
| Instantáneas | Bandeja → «Instantáneas del diseño» para guardar o restaurar la disposición |
| Elementos del sistema | Papelera, Este equipo, Panel de control, Red, Carpeta de usuario |
| Otros | Bloquear fences, iniciar con Windows, multimonitor y DPI por monitor, sobrevive a reinicios de Explorer |

## Datos

- Configuración: `%APPDATA%\ClonFences\config.json` (registro en `log.txt`).
- Para usar otra carpeta de datos, define la variable `CLONFENCES_DATA`.
- `ClonFences.exe --reset` arranca con una configuración vacía.

## Arquitectura

```
ClonFences/
  App.xaml(.cs)              arranque, instancia única, errores
  Models/                    AppConfig, FenceModel, FenceItem
  Services/
    FenceManager.cs          núcleo: ventanas, escritorio, organizar, instantáneas
    DesktopMouseHook.cs      WH_MOUSE_LL: doble clic / clic derecho + arrastrar
    DesktopShell.cs          Progman/WorkerW, mostrar/ocultar iconos, detección de huecos (UIA)
    IconLoader.cs            iconos y miniaturas del shell en un hilo STA
    ShellContextMenu.cs      IContextMenu de Explorer
    FileOps.cs               SHFileOperation (copiar, mover, papelera)
    TrayIcon.cs              icono y menú de la bandeja
  Views/
    FenceWindow              ventana del fence (en capas, propiedad del escritorio, al fondo del orden Z)
    FenceMenu                menú del fence
    SelectionOverlay         rectángulo al dibujar un fence
    SettingsWindow, InputDialog
```

Cada fence es una ventana WPF transparente cuyo propietario es la ventana del escritorio (`Progman`/`WorkerW`).
Así se queda siempre al fondo del orden Z, encima del fondo de pantalla, y no sale en Alt+Tab ni en la barra de tareas.
Los fences normales guardan referencias a archivos (normalmente del Escritorio). Los portales leen una carpeta
y la vigilan con `FileSystemWatcher`.
