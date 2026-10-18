//! Aplicación COSMIC: una superficie layer-shell transparente a pantalla completa por monitor,
//! en la capa `Bottom` (encima del fondo, debajo de las ventanas). Los fences se dibujan dentro de ella
//! y la región de entrada se limita a sus rectángulos, así el resto de clics llegan al escritorio de abajo.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime};

use cosmic::app::{Core, Task};
use cosmic::cctk::wayland_client::Proxy;
use cosmic::cctk::wayland_client::protocol::wl_output::WlOutput;
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
use cosmic::iced::core::text::{Ellipsize, EllipsizeHeightLimit};
use cosmic::iced::{event, keyboard, mouse, window};
use cosmic::widget::icon::{self, IconFallback};
use cosmic::widget::{self, container};
use cosmic::{Application, Element};

use crate::model::color::{parse_hex, wants_dark_text};
use crate::model::config::{AppConfig, FenceModel, new_id};
use crate::model::geometry::{self, Edges, HEADER_HEIGHT, Rect};
use crate::model::item::{FenceItem, sort_items};
use crate::platform::icons::{IconSpec, icon_for};
use crate::platform::{launch, log::log, paths, scan, store};

/// Grosor de la zona sensible de los bordes para redimensionar.
const GRIP: f32 = 6.0;
const SAVE_DELAY: Duration = Duration::from_millis(800);

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

struct Drag {
    fence: String,
    grab: Grab,
    surface: window::Id,
    origin: Point,
    start: Rect,
}

#[derive(Debug, Clone)]
pub enum Message {
    Output(OutputEvent, WlOutput),
    SurfaceSize(window::Id, f32, f32),
    CursorMoved(window::Id, Point),
    LeftReleased,
    HeaderPressed(String),
    ResizePressed(String, Edges),
    ToggleRolled(String),
    Select(PathBuf),
    Open(PathBuf),
    Escape,
    Tick,
}

pub struct App {
    core: Core,
    config: AppConfig,
    desktop: PathBuf,
    screens: Vec<Screen>,
    entries: HashMap<String, Vec<Entry>>,
    drag: Option<Drag>,
    cursor: HashMap<window::Id, Point>,
    selected: Option<PathBuf>,
    /// Última fecha de modificación vista de cada carpeta vigilada (escritorio y portales).
    dir_stamps: HashMap<PathBuf, Option<SystemTime>>,
    save_due: Option<Instant>,
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
        let mut app = App {
            core,
            config: flags.config,
            desktop: paths::desktop_dir(),
            screens: Vec::new(),
            entries: HashMap::new(),
            drag: None,
            cursor: HashMap::new(),
            selected: None,
            dir_stamps: HashMap::new(),
            save_due: None,
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
            }

            Message::CursorMoved(id, p) => {
                self.cursor.insert(id, p);
                if let Some(drag) = &self.drag
                    && drag.surface == id
                {
                    self.drag_to(p);
                }
            }

            Message::LeftReleased => {
                if self.drag.take().is_some() {
                    self.save_soon();
                    return self.sync_regions();
                }
            }

            Message::HeaderPressed(fid) => self.begin_drag(&fid, Grab::Move),
            Message::ResizePressed(fid, edges) => self.begin_drag(&fid, Grab::Resize(edges)),

            Message::ToggleRolled(fid) => {
                self.drag = None;
                if let Some(f) = self.config.fence_mut(&fid) {
                    f.rolled_up = !f.rolled_up;
                    self.save_soon();
                    return self.sync_regions();
                }
            }

            Message::Select(path) => self.selected = Some(path),

            Message::Open(path) => {
                self.selected = Some(path.clone());
                launch::open(&path);
            }

            Message::Escape => self.selected = None,

            Message::Tick => {
                let mut task = Task::none();
                if self.refresh_changed_dirs() {
                    task = self.sync_regions();
                }
                if self.save_due.is_some_and(|t| Instant::now() >= t) {
                    self.save_now();
                }
                return task;
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
        let layers: Vec<Element<'_, Message>> = self
            .fences_on(screen)
            .map(|f| pin(self.fence_view(f)).x(f.x as f32).y(f.y as f32).into())
            .collect();
        stack(layers).width(Length::Fill).height(Length::Fill).into()
    }

    fn subscription(&self) -> Subscription<Message> {
        Subscription::batch([
            event::listen_with(|event, status, id| match event {
                Event::Mouse(mouse::Event::CursorMoved { position }) => Some(Message::CursorMoved(id, position)),
                Event::Mouse(mouse::Event::ButtonReleased(mouse::Button::Left)) => Some(Message::LeftReleased),
                Event::Window(window::Event::Resized(size)) => Some(Message::SurfaceSize(id, size.width, size.height)),
                Event::Window(window::Event::Opened { size, .. }) => {
                    Some(Message::SurfaceSize(id, size.width, size.height))
                }
                Event::Keyboard(keyboard::Event::KeyPressed {
                    key: keyboard::Key::Named(keyboard::key::Named::Escape),
                    ..
                }) if status == event::Status::Ignored => Some(Message::Escape),
                Event::PlatformSpecific(event::PlatformSpecific::Wayland(WaylandEvent::Output(ev, output))) => {
                    Some(Message::Output(ev, output))
                }
                _ => None,
            }),
            iced::time::every(Duration::from_secs(1)).map(|_| Message::Tick),
        ])
    }

    fn on_app_exit(&mut self) -> Option<Message> {
        self.save_now();
        None
    }
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
                let (width, height) = info
                    .as_ref()
                    .and_then(|i| i.logical_size)
                    .unwrap_or((0, 0));
                let surface = window::Id::unique();
                log(format!("Monitor {name}: {width}x{height}"));
                self.screens.push(Screen { surface, output: output.clone(), name, width, height });

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

    fn fences_on<'a>(&'a self, screen: &'a Screen) -> impl Iterator<Item = &'a FenceModel> + 'a {
        self.config
            .fences
            .iter()
            .filter(move |f| self.screen_index_of(f).is_some_and(|i| self.screens[i].surface == screen.surface))
    }

    fn visible_rect(f: &FenceModel) -> Rect {
        Rect::new(f.x, f.y, f.width, if f.rolled_up { HEADER_HEIGHT } else { f.height })
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
            tasks.push(set_input_zone(s.surface, Some(rects)));
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

    // ───────────────────────────── Mover / redimensionar ─────────────────────────────

    fn begin_drag(&mut self, fid: &str, grab: Grab) {
        if self.config.settings.lock_all {
            return;
        }
        let Some(f) = self.config.fence(fid) else { return };
        if f.locked {
            return;
        }
        let Some(i) = self.screen_index_of(f) else { return };
        let surface = self.screens[i].surface;
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
            if !(f.rolled_up && matches!(self.drag.as_ref().map(|d| d.grab), Some(Grab::Resize(_)))) {
                f.height = r.h;
            }
            f.output = Some(name);
        }
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

    /// Los elementos nuevos del escritorio van al fence cuya regla coincida, o al fence predeterminado.
    fn add_new_desktop_items(&mut self) {
        if !self.config.settings.auto_add_new_items {
            return;
        }
        let fenced = self.config.fenced_paths();
        let new: Vec<PathBuf> =
            scan::list_dir(&self.desktop).into_iter().filter(|p| !fenced.contains(p)).collect();
        if new.is_empty() {
            return;
        }
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

    fn fence_view<'a>(&'a self, f: &'a FenceModel) -> Element<'a, Message> {
        let (bg, fg) = self.fence_colors(f);
        let w = f.width as f32;
        let h = if f.rolled_up { HEADER_HEIGHT } else { f.height } as f32;
        let fixed = f.locked || self.config.settings.lock_all;

        let title = widget::text(f.title.as_str())
            .size(14)
            .class(cosmic::theme::Text::Color(fg))
            .ellipsize(Ellipsize::End(EllipsizeHeightLimit::Lines(1)));
        let header = widget::mouse_area(
            container(title)
                .padding([0, 12])
                .width(Length::Fill)
                .height(HEADER_HEIGHT as f32)
                .align_y(Alignment::Center)
                .align_x(Alignment::Center),
        )
        .on_press(Message::HeaderPressed(f.id.clone()))
        .on_double_click(Message::ToggleRolled(f.id.clone()))
        .interaction(if fixed { mouse::Interaction::Idle } else { mouse::Interaction::Grab });

        let mut column = widget::column::with_capacity(3).push(header);
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

        let body = container(column)
            .width(w)
            .height(h)
            .class(cosmic::theme::Container::custom(move |_| container::Style {
                background: Some(Background::Color(bg)),
                border: Border { radius: 10.0.into(), width: 1.0, color: Color { a: 0.12, ..fg } },
                text_color: Some(fg),
                ..Default::default()
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
            let hint = if f.is_portal() { "Carpeta vacía" } else { "Vacío" };
            return container(widget::text(hint).size(12).class(cosmic::theme::Text::Color(Color { a: 0.6, ..fg })))
                .center(Length::Fill)
                .into();
        }

        let tiles: Vec<Element<'a, Message>> = entries
            .iter()
            .map(|e| {
                let selected = self.selected.as_deref() == Some(e.item.path.as_path());
                let label = widget::text(e.item.name.as_str())
                    .size(12)
                    .width(Length::Fill)
                    .align_x(Alignment::Center)
                    .class(cosmic::theme::Text::Color(fg))
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
                    .on_press(Message::Select(e.item.path.clone()))
                    .on_double_click(Message::Open(e.item.path.clone()))
                    .into()
            })
            .collect();

        widget::scrollable(container(widget::flex_row(tiles).spacing(4)).padding(6).width(Length::Fill))
            .height(Length::Fill)
            .into()
    }
}
