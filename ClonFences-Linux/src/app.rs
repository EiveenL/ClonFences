//! Aplicación COSMIC: una superficie layer-shell transparente a pantalla completa por monitor,
//! en la capa `Bottom` (encima del fondo, debajo de las ventanas). Los fences se dibujan dentro de ella
//! y la región de entrada se limita a sus rectángulos, así el resto de clics llegan al escritorio de abajo.
//! Mientras hay un menú abierto o se arrastra un elemento, la superficie acepta toda la entrada.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::LazyLock;
use std::time::{Duration, Instant, SystemTime};

use cosmic::app::{Core, Task};
use cosmic::cctk::wayland_client::Proxy;
use cosmic::cctk::wayland_client::protocol::wl_output::WlOutput;
use cosmic::iced::clipboard::dnd::DndAction;
use cosmic::iced::core::text::{Ellipsize, EllipsizeHeightLimit};
use cosmic::iced::event::wayland::{Event as WaylandEvent, OutputEvent};
use cosmic::iced::platform_specific::runtime::wayland::layer_surface::{
    IcedMargin, IcedOutput, SctkLayerSurfaceSettings,
};
use cosmic::iced::platform_specific::shell::wayland::commands::blur::blur;
use cosmic::iced::platform_specific::shell::wayland::commands::layer_surface::{
    Anchor, KeyboardInteractivity, Layer, destroy_layer_surface, set_input_zone,
};
use cosmic::iced::widget::{pin, stack};
use cosmic::iced::{self, Alignment, Background, Border, Color, Event, Length, Limits, Point, Rectangle, Subscription};
use cosmic::iced::{event, keyboard, mouse, window};
use cosmic::widget::icon::{self, IconFallback};
use cosmic::widget::{self, container};
use cosmic::{Application, Element};

use crate::model::color::{parse_hex, wants_dark_text};
use crate::model::config::{AppConfig, FenceModel, new_id};
use crate::model::geometry::{self, Edges, HEADER_HEIGHT, Rect};
use crate::model::item::{FenceItem, sort_items};
use crate::platform::icons::{IconSpec, icon_for};
use crate::platform::{hidden, launch, log::log, paths, scan, store};

/// Grosor de la zona sensible de los bordes para redimensionar.
const GRIP: f32 = 6.0;
const SAVE_DELAY: Duration = Duration::from_millis(800);
/// Distancia que hay que mover el ratón con el botón pulsado para empezar a arrastrar un elemento.
const DRAG_THRESHOLD: f32 = 6.0;
const MENU_WIDTH: f32 = 230.0;
const MENU_ITEM_HEIGHT: f32 = 36.0;

static RENAME_INPUT: LazyLock<widget::Id> = LazyLock::new(|| widget::Id::new("clonfences-rename"));

pub struct Flags {
    pub config: AppConfig,
}

/// Un monitor y la superficie que lo cubre.
struct Screen {
    surface: window::Id,
    output: WlOutput,
    name: String,
    /// Tamaño lógico de la superficie (zona útil, sin el panel); 0 hasta el primer configure.
    width: i32,
    height: i32,
}

impl Screen {
    fn area(&self) -> Rect {
        Rect::new(0, 0, self.width, self.height)
    }
}

/// Elemento ya preparado para dibujar (con su icono resuelto).
struct Entry {
    item: FenceItem,
    icon: icon::Handle,
}

#[derive(Debug, Clone, Copy, PartialEq)]
enum Grab {
    Move,
    Resize(Edges),
}

/// Mover o redimensionar un fence.
struct Drag {
    fence: String,
    grab: Grab,
    surface: window::Id,
    origin: Point,
    start: Rect,
}

/// Arrastrar un elemento fuera del fence o a otro fence.
struct ItemDrag {
    from: String,
    path: PathBuf,
    surface: window::Id,
    origin: Point,
    pos: Point,
    /// Pasa a true al superar `DRAG_THRESHOLD` (antes es un clic normal).
    active: bool,
}

#[derive(Debug, Clone)]
enum MenuTarget {
    Fence(String),
    Item(String, PathBuf),
}

struct Menu {
    surface: window::Id,
    at: Point,
    target: MenuTarget,
}

#[derive(Debug, Clone)]
pub enum MenuAction {
    Rename(String),
    ToggleRolled(String),
    ToggleLocked(String),
    Open(PathBuf),
    RemoveFromFence(String, PathBuf),
}

struct Rename {
    fence: String,
    text: String,
}

#[derive(Debug, Clone)]
pub enum Message {
    Output(OutputEvent, WlOutput),
    SurfaceSize(window::Id, f32, f32),
    CursorMoved(window::Id, Point),
    LeftReleased,
    HeaderPressed(String),
    HeaderRightPressed(String),
    ResizePressed(String, Edges),
    ToggleRolled(String),
    ItemPressed(String, PathBuf),
    ItemRightPressed(String, PathBuf),
    Open(PathBuf),
    Menu(MenuAction),
    CloseMenu,
    RenameInput(String),
    RenameSubmit,
    /// Un arrastre de otra aplicación se mueve sobre la superficie.
    DndMotion(window::Id, Point),
    DndLeave,
    /// Se soltaron archivos de otra aplicación en este punto de la superficie.
    DropFiles(window::Id, Point, Vec<PathBuf>),
    Escape,
    Tick,
    /// SIGINT/SIGTERM/SIGHUP: restaurar el escritorio, guardar y salir.
    Quit,
}

pub struct App {
    core: Core,
    config: AppConfig,
    desktop: PathBuf,
    screens: Vec<Screen>,
    entries: HashMap<String, Vec<Entry>>,
    drag: Option<Drag>,
    item_drag: Option<ItemDrag>,
    menu: Option<Menu>,
    renaming: Option<Rename>,
    /// Fence sobre el que hay un arrastre externo en curso.
    dnd_hover: Option<String>,
    cursor: HashMap<window::Id, Point>,
    selected: Option<PathBuf>,
    /// Entradas del escritorio ya vistas: sólo las nuevas se añaden solas a un fence.
    desktop_known: HashSet<PathBuf>,
    /// Última fecha de modificación vista de cada carpeta vigilada (escritorio y portales).
    dir_stamps: HashMap<PathBuf, Option<SystemTime>>,
    save_due: Option<Instant>,
    /// Último conjunto de nombres aplicado a `~/Escritorio/.hidden` (None = volver a comprobar).
    hidden_synced: Option<BTreeSet<String>>,
    /// Se desconectó algún monitor desde el arranque.
    output_lost: bool,
    /// Cuándo reiniciar el proceso. Si un monitor se desconecta y vuelve (al apagarlo o al cambiar la
    /// entrada a otro equipo), la superficie que se crea para él se dibuja pero no recibe clics; un
    /// proceso nuevo sí funciona.
    restart_at: Option<Instant>,
}

impl Application for App {
    type Executor = cosmic::executor::Default;
    type Flags = Flags;
    type Message = Message;
    const APP_ID: &'static str = "io.github.clonfences.ClonFences";

    fn core(&self) -> &Core {
        &self.core
    }

    fn core_mut(&mut self) -> &mut Core {
        &mut self.core
    }

    fn init(core: Core, flags: Flags) -> (Self, Task<Message>) {
        let desktop = paths::desktop_dir();
        let mut app = App {
            core,
            config: flags.config,
            desktop_known: scan::list_dir(&desktop).into_iter().collect(),
            desktop,
            screens: Vec::new(),
            entries: HashMap::new(),
            drag: None,
            item_drag: None,
            menu: None,
            renaming: None,
            dnd_hover: None,
            cursor: HashMap::new(),
            selected: None,
            dir_stamps: HashMap::new(),
            save_due: None,
            hidden_synced: None,
            output_lost: false,
            restart_at: None,
        };
        app.dir_stamps.insert(app.desktop.clone(), scan::dir_stamp(&app.desktop));
        let ids: Vec<String> = app.config.fences.iter().map(|f| f.id.clone()).collect();
        for id in ids {
            app.reload_fence(&id);
        }
        log(format!("Inicio: {} fences, escritorio en {}", app.config.fences.len(), app.desktop.display()));
        // Las superficies se crean al recibir los eventos de los monitores (Message::Output).
        (app, Task::none())
    }

    fn update(&mut self, message: Message) -> Task<Message> {
        match message {
            Message::Output(event, output) => return self.on_output(event, output),

            Message::SurfaceSize(id, w, h) => {
                if let Some(s) = self.screens.iter_mut().find(|s| s.surface == id) {
                    s.width = w.round() as i32;
                    s.height = h.round() as i32;
                    if self.config.settings.first_run {
                        self.first_run();
                    }
                    self.clamp_fences();
                    return self.sync_regions();
                }
                log(format!("Tamaño {w}x{h} de una superficie desconocida ({id:?})"));
            }

            Message::CursorMoved(id, p) => {
                self.cursor.insert(id, p);
                if let Some(d) = self.item_drag.as_mut().filter(|d| d.surface == id) {
                    d.pos = p;
                    if !d.active && p.distance(d.origin) > DRAG_THRESHOLD {
                        d.active = true;
                        // Aceptar toda la entrada para seguir el arrastre fuera de los fences.
                        return self.sync_regions();
                    }
                } else if self.drag.as_ref().is_some_and(|d| d.surface == id) {
                    self.drag_to(p);
                }
            }

            Message::LeftReleased => {
                if let Some(d) = self.item_drag.take() {
                    if d.active {
                        self.drop_item(d);
                    }
                    return self.sync_regions();
                }
                if self.drag.take().is_some() {
                    self.save_soon();
                    return self.sync_regions();
                }
            }

            Message::HeaderPressed(fid) => {
                self.commit_rename();
                self.begin_drag(&fid, Grab::Move);
            }
            Message::HeaderRightPressed(fid) => {
                self.commit_rename();
                return self.open_menu(&fid, MenuTarget::Fence(fid.clone()));
            }
            Message::ResizePressed(fid, edges) => {
                self.commit_rename();
                self.begin_drag(&fid, Grab::Resize(edges));
            }

            Message::ToggleRolled(fid) => {
                self.drag = None;
                return self.toggle_rolled(&fid);
            }

            Message::ItemPressed(fid, path) => {
                self.commit_rename();
                self.selected = Some(path.clone());
                self.arm_item_drag(&fid, path);
            }
            Message::ItemRightPressed(fid, path) => {
                self.commit_rename();
                self.selected = Some(path.clone());
                return self.open_menu(&fid, MenuTarget::Item(fid.clone(), path));
            }

            Message::Open(path) => {
                self.item_drag = None;
                self.selected = Some(path.clone());
                launch::open(&path);
            }

            Message::Menu(action) => {
                self.menu = None;
                let task = self.run_menu_action(action);
                return Task::batch([task, self.sync_regions()]);
            }
            Message::CloseMenu => {
                if self.menu.take().is_some() {
                    return self.sync_regions();
                }
            }

            Message::RenameInput(text) => {
                if let Some(r) = &mut self.renaming {
                    r.text = text;
                }
            }
            Message::RenameSubmit => self.commit_rename(),

            Message::DndMotion(surface, p) => {
                let hover = self.fence_at(surface, p).filter(|f| !f.is_portal()).map(|f| f.id.clone());
                if hover.is_some() && hover != self.dnd_hover {
                    log(format!("Arrastre externo sobre un fence en ({:.0}, {:.0})", p.x, p.y));
                }
                self.dnd_hover = hover;
            }
            Message::DndLeave => self.dnd_hover = None,
            Message::DropFiles(surface, p, paths) => {
                self.dnd_hover = None;
                let target = self.fence_at(surface, p).map(|f| f.id.clone());
                log(format!("Soltados {} elementos en ({:.0}, {:.0}) → {target:?}", paths.len(), p.x, p.y));
                if let Some(fid) = target {
                    self.add_to_fence(&fid, paths);
                }
            }

            Message::Escape => {
                if self.renaming.take().is_none() && self.menu.take().is_none() {
                    self.selected = None;
                }
                return self.sync_regions();
            }

            Message::Tick => {
                if self.restart_at.is_some_and(|t| Instant::now() >= t) {
                    self.restart_at = None;
                    self.save_now();
                    restart_process();
                }
                let mut task = Task::none();
                if self.refresh_changed_dirs() {
                    self.hidden_synced = None;
                    task = self.sync_regions();
                }
                self.sync_hidden(true);
                if self.save_due.is_some_and(|t| Instant::now() >= t) {
                    self.save_now();
                }
                return task;
            }

            Message::Quit => {
                log("Saliendo");
                self.sync_hidden(false);
                self.save_now();
                return iced::exit();
            }
        }
        Task::none()
    }

    fn view(&self) -> Element<'_, Message> {
        // No hay ventana principal (no_main_window); cada monitor se dibuja en view_window.
        widget::space::horizontal().into()
    }

    fn view_window(&self, id: window::Id) -> Element<'_, Message> {
        let Some(screen) = self.screens.iter().find(|s| s.surface == id) else {
            return widget::space::horizontal().into();
        };
        let mut layers: Vec<Element<'_, Message>> = self
            .fences_on(screen)
            .map(|f| pin(self.fence_view(f)).x(f.x as f32).y(f.y as f32).into())
            .collect();
        if let Some(ghost) = self.drag_ghost(id) {
            layers.push(ghost);
        }
        if let Some(menu) = self.menu.as_ref().filter(|m| m.surface == id) {
            layers.extend(self.menu_layers(menu, screen));
        }
        let content = stack(layers).width(Length::Fill).height(Length::Fill);

        // Una sola zona de soltar para toda la superficie: `pin` no propaga las zonas de soltar de sus
        // hijos, así que el fence de destino se calcula a partir del punto. La región de entrada ya
        // limita los arrastres externos a los rectángulos de los fences.
        widget::dnd_destination(content, vec!["text/uri-list".into()])
            // Sólo "copiar": el fence guarda una referencia y el origen no debe mover ni borrar nada.
            .action(DndAction::Copy)
            .preferred_action(DndAction::Copy)
            .on_enter(move |x, y, _| Message::DndMotion(id, Point::new(x as f32, y as f32)))
            .on_motion(move |x, y| Message::DndMotion(id, Point::new(x as f32, y as f32)))
            .on_leave(|| Message::DndLeave)
            .on_finish(move |_mime, data, _action, x, y| {
                let paths = std::str::from_utf8(&data).map(crate::model::uri::parse_uri_list).unwrap_or_default();
                Message::DropFiles(id, Point::new(x as f32, y as f32), paths)
            })
            .into()
    }

    fn subscription(&self) -> Subscription<Message> {
        Subscription::batch([
            event::listen_with(|event, _status, id| match event {
                Event::Mouse(mouse::Event::CursorMoved { position }) => Some(Message::CursorMoved(id, position)),
                Event::Mouse(mouse::Event::ButtonReleased(mouse::Button::Left)) => Some(Message::LeftReleased),
                Event::Window(window::Event::Resized(size)) => Some(Message::SurfaceSize(id, size.width, size.height)),
                Event::Window(window::Event::Opened { size, .. }) => {
                    Some(Message::SurfaceSize(id, size.width, size.height))
                }
                Event::Keyboard(keyboard::Event::KeyPressed {
                    key: keyboard::Key::Named(keyboard::key::Named::Escape),
                    ..
                }) => Some(Message::Escape),
                Event::PlatformSpecific(event::PlatformSpecific::Wayland(WaylandEvent::Output(ev, output))) => {
                    Some(Message::Output(ev, output))
                }
                _ => None,
            }),
            iced::time::every(Duration::from_secs(1)).map(|_| Message::Tick),
            Subscription::run(quit_signals),
        ])
    }

    fn on_app_exit(&mut self) -> Option<Message> {
        self.sync_hidden(false);
        self.save_now();
        None
    }
}

/// Emite `Message::Quit` al recibir SIGINT (Ctrl+C), SIGTERM (cierre de sesión) o SIGHUP.
fn quit_signals() -> impl cosmic::iced::futures::Stream<Item = Message> {
    cosmic::iced::stream::channel(1, |mut tx: cosmic::iced::futures::channel::mpsc::Sender<Message>| async move {
        use cosmic::iced::futures::SinkExt;
        use tokio::signal::unix::{SignalKind, signal};
        let (Ok(mut int), Ok(mut term), Ok(mut hup)) =
            (signal(SignalKind::interrupt()), signal(SignalKind::terminate()), signal(SignalKind::hangup()))
        else {
            log("No se pudieron instalar los manejadores de señales");
            return;
        };
        tokio::select! {
            _ = int.recv() => {}
            _ = term.recv() => {}
            _ = hup.recv() => {}
        }
        let _ = tx.send(Message::Quit).await;
        std::future::pending::<()>().await;
    })
}

/// Sustituye el proceso por uno nuevo del mismo ejecutable con los mismos argumentos. El bloqueo de
/// instancia única se libera solo: su descriptor se cierra en el `exec`.
fn restart_process() {
    use std::os::unix::process::CommandExt;
    let Ok(exe) = std::env::current_exe() else {
        log("No se pudo reiniciar: ejecutable desconocido");
        return;
    };
    // Si se reinstaló mientras corría, Linux añade " (deleted)" a la ruta del antiguo.
    let exe = PathBuf::from(exe.to_string_lossy().trim_end_matches(" (deleted)"));
    let args = std::env::args().skip(1).filter(|a| a != "--reset");
    let err = std::process::Command::new(&exe).args(args).exec();
    log(format!("No se pudo reiniciar {}: {err}", exe.display()));
}

impl App {
    // ───────────────────────────── Monitores ─────────────────────────────

    fn on_output(&mut self, event: OutputEvent, output: WlOutput) -> Task<Message> {
        match event {
            OutputEvent::Created(info) => {
                if self.screens.iter().any(|s| s.output == output) {
                    return Task::none();
                }
                let name = info
                    .as_ref()
                    .and_then(|i| i.name.clone())
                    .unwrap_or_else(|| format!("output-{}", output.id().protocol_id()));
                let (width, height) = info.as_ref().and_then(|i| i.logical_size).unwrap_or((0, 0));
                let surface = window::Id::unique();
                log(format!("Monitor {name}: {width}x{height}"));
                self.screens.push(Screen { surface, output: output.clone(), name, width, height });
                if self.output_lost {
                    // Margen por si el monitor parpadea varias veces al encenderse.
                    log("Monitor reconectado: reiniciando en 2 s");
                    self.restart_at = Some(Instant::now() + Duration::from_secs(2));
                }

                let settings = SctkLayerSurfaceSettings {
                    id: surface,
                    layer: Layer::Bottom,
                    keyboard_interactivity: KeyboardInteractivity::OnDemand,
                    // Sin entrada hasta saber dónde están los fences (sync_regions).
                    input_zone: Some(Vec::new()),
                    anchor: Anchor::TOP | Anchor::BOTTOM | Anchor::LEFT | Anchor::RIGHT,
                    output: IcedOutput::Output(output),
                    namespace: "clonfences".into(),
                    size: Some((None, None)),
                    margin: IcedMargin::default(),
                    // 0 = respetar las zonas exclusivas de otros (el panel), sin reservar espacio propio.
                    exclusive_zone: 0,
                    size_limits: Limits::NONE.min_width(1.0).min_height(1.0),
                };
                cosmic::task::message(cosmic::Action::Surface(cosmic::surface::action::app_layer_shell(
                    |_: &App| Default::default(),
                    move |_: &mut App| settings.clone(),
                    None,
                )))
            }
            OutputEvent::Removed => {
                let Some(pos) = self.screens.iter().position(|s| s.output == output) else {
                    return Task::none();
                };
                let s = self.screens.remove(pos);
                log(format!("Monitor {} desconectado", s.name));
                self.output_lost = true;
                self.restart_at = None;
                if self.menu.as_ref().is_some_and(|m| m.surface == s.surface) {
                    self.menu = None;
                }
                // Sus fences pasan al primer monitor que quede (sin cambiar su `output`, por si vuelve).
                self.clamp_fences();
                Task::batch([destroy_layer_surface(s.surface), self.sync_regions()])
            }
            OutputEvent::InfoUpdate(info) => {
                if let Some(s) = self.screens.iter_mut().find(|s| s.output == output)
                    && let Some(name) = info.name
                {
                    s.name = name;
                }
                Task::none()
            }
        }
    }

    /// Monitor donde se dibuja un fence: el suyo si está conectado; si no, el primero.
    fn screen_index_of(&self, f: &FenceModel) -> Option<usize> {
        f.output
            .as_ref()
            .and_then(|name| self.screens.iter().position(|s| &s.name == name))
            .or(if self.screens.is_empty() { None } else { Some(0) })
    }

    fn surface_of(&self, fid: &str) -> Option<window::Id> {
        let f = self.config.fence(fid)?;
        self.screen_index_of(f).map(|i| self.screens[i].surface)
    }

    fn fences_on<'a>(&'a self, screen: &'a Screen) -> impl Iterator<Item = &'a FenceModel> + 'a {
        self.config
            .fences
            .iter()
            .filter(move |f| self.screen_index_of(f).is_some_and(|i| self.screens[i].surface == screen.surface))
    }

    fn visible_rect(f: &FenceModel) -> Rect {
        Rect::new(f.x, f.y, f.width, if f.rolled_up { HEADER_HEIGHT } else { f.height })
    }

    /// Fence bajo un punto de una superficie (el de más arriba si se solapan).
    fn fence_at(&self, surface: window::Id, p: Point) -> Option<&FenceModel> {
        let screen = self.screens.iter().find(|s| s.surface == surface)?;
        let (x, y) = (p.x.round() as i32, p.y.round() as i32);
        self.fences_on(screen).filter(|f| Self::visible_rect(f).contains(x, y)).last()
    }

    /// ¿Esta superficie debe aceptar toda la entrada (menú abierto o arrastrando un elemento)?
    fn wants_full_input(&self, surface: window::Id) -> bool {
        self.menu.as_ref().is_some_and(|m| m.surface == surface)
            || self.item_drag.as_ref().is_some_and(|d| d.active && d.surface == surface)
    }

    /// Región de entrada y de desenfoque de cada monitor = rectángulos de sus fences.
    fn sync_regions(&self) -> Task<Message> {
        let mut tasks = Vec::new();
        for s in &self.screens {
            let rects: Vec<Rectangle> = self
                .fences_on(s)
                .map(|f| {
                    let r = Self::visible_rect(f);
                    Rectangle::new(Point::new(r.x as f32, r.y as f32), iced::Size::new(r.w as f32, r.h as f32))
                })
                .collect();
            let blur_zone = self.config.settings.blur_behind.then(|| rects.clone());
            tasks.push(blur(s.surface, blur_zone).discard());
            let input = if self.wants_full_input(s.surface) { None } else { Some(rects) };
            tasks.push(set_input_zone(s.surface, input));
        }
        Task::batch(tasks)
    }

    /// Mantiene cada fence dentro de su monitor (p. ej. tras cambiar la resolución).
    fn clamp_fences(&mut self) {
        let areas: Vec<Option<Rect>> = self
            .config
            .fences
            .iter()
            .map(|f| self.screen_index_of(f).map(|i| self.screens[i].area()).filter(|a| a.w > 0 && a.h > 0))
            .collect();
        for (f, area) in self.config.fences.iter_mut().zip(areas) {
            let Some(area) = area else { continue };
            let r = geometry::clamp_into(Rect::new(f.x, f.y, f.width, f.height), area);
            (f.x, f.y, f.width, f.height) = (r.x, r.y, r.w, r.h);
        }
    }

    // ───────────────────────────── Mover / redimensionar fences ─────────────────────────────

    fn begin_drag(&mut self, fid: &str, grab: Grab) {
        if self.config.settings.lock_all {
            return;
        }
        let Some(f) = self.config.fence(fid) else { return };
        if f.locked {
            return;
        }
        let Some(surface) = self.surface_of(fid) else { return };
        let Some(origin) = self.cursor.get(&surface).copied() else { return };
        self.drag = Some(Drag {
            fence: fid.to_string(),
            grab,
            surface,
            origin,
            start: Rect::new(f.x, f.y, f.width, f.height),
        });
    }

    fn drag_to(&mut self, p: Point) {
        let Some(drag) = &self.drag else { return };
        let Some(f) = self.config.fence(&drag.fence) else { return };
        let Some(i) = self.screen_index_of(f) else { return };
        let screen = &self.screens[i];
        let area = screen.area();
        let others: Vec<Rect> =
            self.fences_on(screen).filter(|o| o.id != drag.fence).map(Self::visible_rect).collect();
        let (dx, dy) = ((p.x - drag.origin.x).round() as i32, (p.y - drag.origin.y).round() as i32);
        let snap = self.config.settings.snap_enabled;
        let resizing = matches!(drag.grab, Grab::Resize(_));
        let r = match drag.grab {
            Grab::Move => {
                let visible_h = if f.rolled_up { HEADER_HEIGHT } else { drag.start.h };
                geometry::moved(drag.start, visible_h, dx, dy, area, &others, snap)
            }
            Grab::Resize(edges) => geometry::resized(drag.start, edges, dx, dy, area, &others, snap),
        };
        let name = screen.name.clone();
        let fid = drag.fence.clone();
        if let Some(f) = self.config.fence_mut(&fid) {
            (f.x, f.y, f.width) = (r.x, r.y, r.w);
            // Un fence enrollado sólo cambia de ancho al redimensionar: conserva su altura desplegada.
            if !(f.rolled_up && resizing) {
                f.height = r.h;
            }
            f.output = Some(name);
        }
    }

    fn toggle_rolled(&mut self, fid: &str) -> Task<Message> {
        if let Some(f) = self.config.fence_mut(fid) {
            f.rolled_up = !f.rolled_up;
            self.save_soon();
        }
        self.sync_regions()
    }

    // ───────────────────────────── Arrastrar elementos ─────────────────────────────

    fn arm_item_drag(&mut self, fid: &str, path: PathBuf) {
        let Some(f) = self.config.fence(fid) else { return };
        // Los portales muestran una carpeta: sus elementos no se "sacan".
        if f.is_portal() {
            return;
        }
        let Some(surface) = self.surface_of(fid) else { return };
        let Some(origin) = self.cursor.get(&surface).copied() else { return };
        self.item_drag = Some(ItemDrag { from: fid.to_string(), path, surface, origin, pos: origin, active: false });
    }

    /// Fence normal sobre el que se soltaría el elemento ahora mismo (distinto del de origen).
    fn drop_target(&self) -> Option<&FenceModel> {
        let d = self.item_drag.as_ref().filter(|d| d.active)?;
        self.fence_at(d.surface, d.pos).filter(|f| f.id != d.from && !f.is_portal())
    }

    fn drop_item(&mut self, d: ItemDrag) {
        let target = self.fence_at(d.surface, d.pos).map(|f| (f.id.clone(), f.is_portal()));
        match target {
            // Sobre el escritorio: sale del fence y vuelve a verse en el escritorio de COSMIC.
            None => self.remove_from_fence(&d.from, &d.path),
            Some((tid, false)) if tid != d.from => self.move_to_fence(&d.path, &tid),
            _ => {}
        }
    }

    fn remove_from_fence(&mut self, fid: &str, path: &Path) {
        let key = path.to_string_lossy();
        if let Some(f) = self.config.fence_mut(fid) {
            f.items.retain(|p| *p != key);
        }
        if self.selected.as_deref() == Some(path) {
            self.selected = None;
        }
        self.reload_fence(fid);
        self.save_soon();
        self.sync_hidden(true);
    }

    fn move_to_fence(&mut self, path: &Path, target: &str) {
        self.add_to_fence(target, vec![path.to_path_buf()]);
    }

    /// Mete elementos en un fence normal (quitándolos de cualquier otro fence). Los archivos no se mueven:
    /// el fence guarda una referencia; si están en el escritorio, dejan de verse en él.
    fn add_to_fence(&mut self, target: &str, paths: Vec<PathBuf>) {
        if self.config.fence(target).is_none_or(|f| f.is_portal()) {
            return;
        }
        let keys: Vec<String> = paths
            .iter()
            .filter(|p| p.exists())
            .map(|p| p.to_string_lossy().into_owned())
            .fold(Vec::new(), |mut v, k| {
                if !v.contains(&k) {
                    v.push(k);
                }
                v
            });
        if keys.is_empty() {
            return;
        }
        let mut touched = vec![target.to_string()];
        for f in self.config.fences.iter_mut().filter(|f| !f.is_portal() && f.id != target) {
            let before = f.items.len();
            f.items.retain(|p| !keys.contains(p));
            if f.items.len() != before {
                touched.push(f.id.clone());
            }
        }
        if let Some(t) = self.config.fence_mut(target) {
            for k in keys {
                if !t.items.contains(&k) {
                    t.items.push(k);
                }
            }
        }
        for id in touched {
            self.reload_fence(&id);
        }
        self.save_soon();
        self.sync_hidden(true);
    }

    fn drag_ghost(&self, surface: window::Id) -> Option<Element<'_, Message>> {
        let d = self.item_drag.as_ref().filter(|d| d.active && d.surface == surface)?;
        let f = self.config.fence(&d.from)?;
        let entry = self.entries.get(&d.from)?.iter().find(|e| e.item.path == d.path)?;
        let size = self.icon_size(f);
        let w = size as f32 + 42.0;
        let content = widget::column::with_capacity(2)
            .push(widget::icon(entry.icon.clone()).size(size))
            .push(
                widget::text(entry.item.name.as_str())
                    .size(12)
                    .width(Length::Fill)
                    .align_x(Alignment::Center)
                    .class(cosmic::theme::Text::Color(Color::WHITE))
                    .ellipsize(Ellipsize::End(EllipsizeHeightLimit::Lines(2))),
            )
            .spacing(4)
            .align_x(Alignment::Center);
        let ghost = container(content).padding(4).width(w).class(cosmic::theme::Container::custom(|_| container::Style {
            background: Some(Background::Color(Color::from_rgba8(0, 0, 0, 0.45))),
            border: Border { radius: 6.0.into(), ..Default::default() },
            ..Default::default()
        }));
        Some(pin(ghost).x(d.pos.x - w / 2.0).y(d.pos.y - size as f32 / 2.0 - 4.0).into())
    }

    // ───────────────────────────── Menús y renombrar ─────────────────────────────

    fn open_menu(&mut self, fid: &str, target: MenuTarget) -> Task<Message> {
        self.item_drag = None;
        self.drag = None;
        let Some(surface) = self.surface_of(fid) else { return Task::none() };
        let at = self.cursor.get(&surface).copied().unwrap_or(Point::ORIGIN);
        self.menu = Some(Menu { surface, at, target });
        self.sync_regions()
    }

    fn menu_entries(&self, target: &MenuTarget) -> Vec<(&'static str, MenuAction)> {
        match target {
            MenuTarget::Fence(fid) => {
                let Some(f) = self.config.fence(fid) else { return Vec::new() };
                vec![
                    ("Renombrar", MenuAction::Rename(fid.clone())),
                    (if f.rolled_up { "Desenrollar" } else { "Enrollar" }, MenuAction::ToggleRolled(fid.clone())),
                    (
                        if f.locked { "Desbloquear posición" } else { "Bloquear posición" },
                        MenuAction::ToggleLocked(fid.clone()),
                    ),
                ]
            }
            MenuTarget::Item(fid, path) => {
                let mut v = vec![("Abrir", MenuAction::Open(path.clone()))];
                if self.config.fence(fid).is_some_and(|f| !f.is_portal()) {
                    v.push(("Quitar del fence", MenuAction::RemoveFromFence(fid.clone(), path.clone())));
                }
                v
            }
        }
    }

    fn run_menu_action(&mut self, action: MenuAction) -> Task<Message> {
        match action {
            MenuAction::Rename(fid) => {
                let Some(f) = self.config.fence_mut(&fid) else { return Task::none() };
                // Para renombrar hace falta ver el título: desenrollar no es necesario, el título siempre se ve.
                let text = f.title.clone();
                self.renaming = Some(Rename { fence: fid, text });
                return Task::batch([
                    widget::text_input::focus(RENAME_INPUT.clone()),
                    widget::text_input::select_all(RENAME_INPUT.clone()),
                ]);
            }
            MenuAction::ToggleRolled(fid) => return self.toggle_rolled(&fid),
            MenuAction::ToggleLocked(fid) => {
                if let Some(f) = self.config.fence_mut(&fid) {
                    f.locked = !f.locked;
                    self.save_soon();
                }
            }
            MenuAction::Open(path) => launch::open(&path),
            MenuAction::RemoveFromFence(fid, path) => self.remove_from_fence(&fid, &path),
        }
        Task::none()
    }

    fn commit_rename(&mut self) {
        let Some(r) = self.renaming.take() else { return };
        let title = r.text.trim();
        if title.is_empty() {
            return;
        }
        if let Some(f) = self.config.fence_mut(&r.fence)
            && f.title != title
        {
            f.title = title.to_string();
            self.save_soon();
        }
    }

    fn menu_layers<'a>(&'a self, menu: &'a Menu, screen: &Screen) -> Vec<Element<'a, Message>> {
        let entries = self.menu_entries(&menu.target);
        let h = entries.len() as f32 * MENU_ITEM_HEIGHT + 8.0;
        let x = menu.at.x.min(screen.width as f32 - MENU_WIDTH).max(0.0);
        let y = menu.at.y.min(screen.height as f32 - h).max(0.0);

        // Capa invisible que cierra el menú al hacer clic fuera de él.
        let catcher = widget::mouse_area(container(widget::space::horizontal()).width(Length::Fill).height(Length::Fill))
            .on_press(Message::CloseMenu)
            .on_right_press(Message::CloseMenu);

        let buttons: Vec<Element<'a, Message>> = entries
            .into_iter()
            .map(|(label, action)| {
                widget::button::custom(widget::text(label).size(14))
                    .padding([8, 12])
                    .width(Length::Fill)
                    .class(cosmic::theme::Button::MenuItem)
                    .on_press(Message::Menu(action))
                    .into()
            })
            .collect();
        let list = container(widget::column::with_children(buttons).padding(4).width(MENU_WIDTH))
            .class(cosmic::theme::Container::Dropdown);

        vec![catcher.into(), pin(list).x(x).y(y).into()]
    }

    // ───────────────────────────── Elementos ─────────────────────────────

    fn icon_size(&self, f: &FenceModel) -> u16 {
        f.icon_size.unwrap_or(self.config.settings.default_icon_size).clamp(16, 256) as u16
    }

    fn resolve_icon(spec: IconSpec, size: u16) -> icon::Handle {
        match spec {
            IconSpec::File(path) => icon::from_path(path),
            IconSpec::Names(mut names) => {
                let first = if names.is_empty() { "text-x-generic".to_string() } else { names.remove(0) };
                icon::from_name(first)
                    .size(size)
                    .fallback(Some(IconFallback::Names(names.into_iter().map(Into::into).collect())))
                    .handle()
            }
        }
    }

    /// Vuelve a leer los elementos de un fence desde el disco.
    fn reload_fence(&mut self, fid: &str) {
        let Some(f) = self.config.fence(fid) else { return };
        let size = self.icon_size(f);
        let mut items: Vec<FenceItem> = if let Some(folder) = f.folder_path.as_deref().filter(|_| f.is_portal()) {
            let folder = Path::new(folder);
            self.dir_stamps.entry(folder.to_path_buf()).or_insert_with(|| scan::dir_stamp(folder));
            scan::list_dir(folder).iter().filter_map(|p| scan::item_for(p)).collect()
        } else {
            let mut found = Vec::new();
            let mut gone = Vec::new();
            for p in &f.items {
                match scan::item_for(Path::new(p)) {
                    Some(item) => found.push(item),
                    None if scan::should_prune(Path::new(p)) => gone.push(p.clone()),
                    None => {}
                }
            }
            if !gone.is_empty()
                && let Some(f) = self.config.fence_mut(fid)
            {
                f.items.retain(|p| !gone.contains(p));
                self.save_soon();
            }
            found
        };
        let Some(f) = self.config.fence(fid) else { return };
        sort_items(&mut items, f.sort, f.is_portal());
        let entries = items
            .into_iter()
            .map(|item| {
                let icon = Self::resolve_icon(icon_for(&item), size);
                Entry { item, icon }
            })
            .collect();
        self.entries.insert(fid.to_string(), entries);
    }

    /// Revisa las carpetas vigiladas; devuelve true si algo cambió.
    fn refresh_changed_dirs(&mut self) -> bool {
        let changed: Vec<PathBuf> = self
            .dir_stamps
            .iter()
            .filter(|(dir, stamp)| scan::dir_stamp(dir) != **stamp)
            .map(|(dir, _)| dir.clone())
            .collect();
        if changed.is_empty() {
            return false;
        }
        for dir in &changed {
            self.dir_stamps.insert(dir.clone(), scan::dir_stamp(dir));
        }
        if changed.contains(&self.desktop) {
            self.add_new_desktop_items();
        }
        let ids: Vec<String> = self
            .config
            .fences
            .iter()
            .filter(|f| {
                // Los fences normales se recargan siempre (sus archivos pueden haberse borrado o renombrado).
                !f.is_portal() || f.folder_path.as_deref().is_some_and(|p| changed.iter().any(|c| c == Path::new(p)))
            })
            .map(|f| f.id.clone())
            .collect();
        for id in ids {
            self.reload_fence(&id);
        }
        true
    }

    /// Los elementos que aparecen nuevos en el escritorio van al fence cuya regla coincida, o al
    /// predeterminado. Lo que el usuario sacó de un fence no se vuelve a meter: ya era conocido.
    fn add_new_desktop_items(&mut self) {
        let current: HashSet<PathBuf> = scan::list_dir(&self.desktop).into_iter().collect();
        let mut new: Vec<PathBuf> = current.difference(&self.desktop_known).cloned().collect();
        self.desktop_known = current;
        if !self.config.settings.auto_add_new_items || new.is_empty() {
            return;
        }
        let fenced = self.config.fenced_paths();
        new.retain(|p| !fenced.contains(p));
        new.sort();
        for p in new {
            let Some(item) = scan::item_for(&p) else { continue };
            let target = self
                .config
                .fences
                .iter()
                .find(|f| !f.is_portal() && crate::model::rules::rules_match(&f.rules, &item.extension, item.is_dir))
                .or_else(|| {
                    self.config.settings.default_fence_id.as_deref().and_then(|id| self.config.fence(id))
                })
                .filter(|f| !f.is_portal())
                .map(|f| f.id.clone());
            if let Some(id) = target
                && let Some(f) = self.config.fence_mut(&id)
            {
                f.items.push(p.to_string_lossy().into_owned());
            }
        }
        self.save_soon();
    }

    /// Primer arranque: un fence "Escritorio" con lo que hay en el escritorio, arriba a la derecha.
    fn first_run(&mut self) {
        let Some(screen) = self.screens.first() else { return };
        if screen.width <= 0 || screen.height <= 0 {
            return;
        }
        let area = screen.area();
        let name = screen.name.clone();
        self.config.settings.first_run = false;
        if !self.config.fences.is_empty() {
            self.save_soon();
            return;
        }

        let items: Vec<String> =
            scan::list_dir(&self.desktop).iter().map(|p| p.to_string_lossy().into_owned()).collect();
        let icon = self.config.settings.default_icon_size;
        let w = 360;
        let tile_w = icon + 42;
        let tile_h = icon + 50;
        let cols = ((w - 12) / tile_w).max(1);
        let rows = (items.len() as i32 + cols - 1) / cols;
        let h = (HEADER_HEIGHT + 16 + rows.max(1) * tile_h).clamp(150, 520);
        let spot = geometry::free_spot(w, h, area, &[]);

        let id = new_id();
        self.config.fences.push(FenceModel {
            id: id.clone(),
            title: "Escritorio".into(),
            x: spot.x,
            y: spot.y,
            width: w,
            height: h,
            items,
            output: Some(name),
            ..FenceModel::default()
        });
        self.config.settings.default_fence_id = Some(id.clone());
        self.reload_fence(&id);
        log("Primer arranque: creado el fence «Escritorio»");
        self.save_soon();
    }

    // ───────────────────────────── Escritorio de COSMIC ─────────────────────────────

    /// Oculta del escritorio de COSMIC lo que está en un fence (`enabled = false` lo devuelve todo).
    fn sync_hidden(&mut self, enabled: bool) {
        let want = if enabled && self.config.settings.hide_fenced_items {
            hidden::fenced_names(&self.config, &self.desktop)
        } else {
            BTreeSet::new()
        };
        if self.hidden_synced.as_ref() == Some(&want) {
            return;
        }
        match hidden::sync(&self.desktop, &want, &self.config.settings.hidden_on_desktop) {
            Ok(managed) => {
                if managed != self.config.settings.hidden_on_desktop {
                    self.config.settings.hidden_on_desktop = managed;
                    self.save_soon();
                }
                self.hidden_synced = Some(want);
            }
            Err(e) => log(format!("No se pudo actualizar {}/.hidden: {e}", self.desktop.display())),
        }
    }

    // ───────────────────────────── Guardado ─────────────────────────────

    fn save_soon(&mut self) {
        self.save_due = Some(Instant::now() + SAVE_DELAY);
    }

    fn save_now(&mut self) {
        self.save_due = None;
        if let Err(e) = store::save(&self.config) {
            log(format!("No se pudo guardar la configuración: {e}"));
        }
    }

    // ───────────────────────────── Vista ─────────────────────────────

    fn fence_colors(&self, f: &FenceModel) -> (Color, Color) {
        let s = &self.config.settings;
        let rgb = f
            .color
            .as_deref()
            .and_then(parse_hex)
            .or_else(|| parse_hex(&s.default_color))
            .unwrap_or(crate::model::color::Rgb(0x1E, 0x1E, 0x1E));
        let opacity = f.opacity.unwrap_or(s.default_opacity).clamp(0.0, 1.0);
        let bg = Color::from_rgba8(rgb.0, rgb.1, rgb.2, opacity as f32);
        let fg = if wants_dark_text(rgb, opacity) { Color::from_rgb8(0x1A, 0x1A, 0x1A) } else { Color::WHITE };
        (bg, fg)
    }

    fn header_view<'a>(&'a self, f: &'a FenceModel, fg: Color, fixed: bool) -> Element<'a, Message> {
        if let Some(r) = self.renaming.as_ref().filter(|r| r.fence == f.id) {
            let input = widget::text_input("Nombre del fence", r.text.as_str())
                .id(RENAME_INPUT.clone())
                .on_input(Message::RenameInput)
                .on_submit(|_| Message::RenameSubmit);
            return container(input)
                .padding([2, 8])
                .width(Length::Fill)
                .height(HEADER_HEIGHT as f32)
                .align_y(Alignment::Center)
                .into();
        }

        let title = widget::text(f.title.as_str())
            .size(14)
            .class(cosmic::theme::Text::Color(fg))
            .ellipsize(Ellipsize::End(EllipsizeHeightLimit::Lines(1)));
        widget::mouse_area(
            container(title)
                .padding([0, 12])
                .width(Length::Fill)
                .height(HEADER_HEIGHT as f32)
                .align_y(Alignment::Center)
                .align_x(Alignment::Center),
        )
        .on_press(Message::HeaderPressed(f.id.clone()))
        .on_right_press(Message::HeaderRightPressed(f.id.clone()))
        .on_double_click(Message::ToggleRolled(f.id.clone()))
        .interaction(if fixed { mouse::Interaction::Idle } else { mouse::Interaction::Grab })
        .into()
    }

    fn fence_view<'a>(&'a self, f: &'a FenceModel) -> Element<'a, Message> {
        let (bg, fg) = self.fence_colors(f);
        let w = f.width as f32;
        let h = if f.rolled_up { HEADER_HEIGHT } else { f.height } as f32;
        let fixed = f.locked || self.config.settings.lock_all;
        let is_drop_target = self.drop_target().is_some_and(|t| t.id == f.id)
            || self.dnd_hover.as_deref() == Some(f.id.as_str());

        let mut column = widget::column::with_capacity(3).push(self.header_view(f, fg, fixed));
        if !f.rolled_up {
            column = column
                .push(container(widget::space::horizontal()).height(1.0).width(Length::Fill).class(
                    cosmic::theme::Container::custom(move |_| container::Style {
                        background: Some(Background::Color(Color { a: 0.18, ..fg })),
                        ..Default::default()
                    }),
                ))
                .push(self.items_view(f, fg));
        }

        let border = if is_drop_target {
            Border { radius: 10.0.into(), width: 2.0, color: Color { a: 0.85, ..fg } }
        } else {
            Border { radius: 10.0.into(), width: 1.0, color: Color { a: 0.12, ..fg } }
        };
        let body = container(column).width(w).height(h).class(cosmic::theme::Container::custom(move |_| {
            container::Style {
                background: Some(Background::Color(bg)),
                border,
                text_color: Some(fg),
                ..Default::default()
            }
        }));

        if fixed {
            return body.into();
        }

        // Bordes para redimensionar (encima del contenido).
        let id = &f.id;
        let grip = |edges: Edges, gw: f32, gh: f32, x: f32, y: f32, cursor: mouse::Interaction| -> Element<'a, Message> {
            pin(widget::mouse_area(widget::space::horizontal().width(gw).height(gh))
                .on_press(Message::ResizePressed(id.clone(), edges))
                .interaction(cursor))
            .x(x)
            .y(y)
            .into()
        };
        let mut layers: Vec<Element<'a, Message>> = vec![
            body.into(),
            grip(Edges::LEFT, GRIP, h, 0.0, 0.0, mouse::Interaction::ResizingHorizontally),
            grip(Edges::RIGHT, GRIP, h, w - GRIP, 0.0, mouse::Interaction::ResizingHorizontally),
        ];
        if !f.rolled_up {
            layers.push(grip(Edges::BOTTOM, w, GRIP, 0.0, h - GRIP, mouse::Interaction::ResizingVertically));
            layers.push(grip(Edges::BOTTOM_LEFT, 14.0, 14.0, 0.0, h - 14.0, mouse::Interaction::ResizingDiagonallyUp));
            layers.push(grip(
                Edges::BOTTOM_RIGHT,
                14.0,
                14.0,
                w - 14.0,
                h - 14.0,
                mouse::Interaction::ResizingDiagonallyDown,
            ));
        }
        container(stack(layers)).width(w).height(h).into()
    }

    fn items_view<'a>(&'a self, f: &'a FenceModel, fg: Color) -> Element<'a, Message> {
        let size = self.icon_size(f);
        let tile_w = size as f32 + 42.0;
        let entries = self.entries.get(&f.id).map(Vec::as_slice).unwrap_or_default();

        if entries.is_empty() {
            let hint = if f.is_portal() { "Carpeta vacía" } else { "Arrastra elementos aquí" };
            return container(widget::text(hint).size(12).class(cosmic::theme::Text::Color(Color { a: 0.6, ..fg })))
                .center(Length::Fill)
                .into();
        }

        let dragging = self.item_drag.as_ref().filter(|d| d.active).map(|d| d.path.as_path());
        let tiles: Vec<Element<'a, Message>> = entries
            .iter()
            .map(|e| {
                let selected = self.selected.as_deref() == Some(e.item.path.as_path());
                // El elemento que se está arrastrando se atenúa en su sitio.
                let alpha = if dragging == Some(e.item.path.as_path()) { 0.35 } else { 1.0 };
                let label = widget::text(e.item.name.as_str())
                    .size(12)
                    .width(Length::Fill)
                    .align_x(Alignment::Center)
                    .class(cosmic::theme::Text::Color(Color { a: alpha, ..fg }))
                    .ellipsize(Ellipsize::End(EllipsizeHeightLimit::Lines(2)));
                let content = widget::column::with_capacity(2)
                    .push(widget::icon(e.icon.clone()).size(size))
                    .push(label)
                    .spacing(4)
                    .align_x(Alignment::Center)
                    .width(Length::Fill);
                let tile = container(content).padding(4).width(tile_w).class(cosmic::theme::Container::custom(
                    move |_| container::Style {
                        background: selected.then_some(Background::Color(Color { a: 0.22, ..fg })),
                        border: Border { radius: 6.0.into(), ..Default::default() },
                        ..Default::default()
                    },
                ));
                widget::mouse_area(tile)
                    .on_press(Message::ItemPressed(f.id.clone(), e.item.path.clone()))
                    .on_right_press(Message::ItemRightPressed(f.id.clone(), e.item.path.clone()))
                    .on_double_click(Message::Open(e.item.path.clone()))
                    .into()
            })
            .collect();

        widget::scrollable(container(widget::flex_row(tiles).spacing(4)).padding(6).width(Length::Fill))
            .height(Length::Fill)
            .into()
    }
}
