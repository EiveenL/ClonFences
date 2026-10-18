//! Geometría de los fences: mover, redimensionar, imantar y mantenerlos dentro del monitor.
//! Todo en píxeles lógicos relativos a la zona útil del monitor (0,0 = esquina superior izquierda).

pub const HEADER_HEIGHT: i32 = 32;
pub const MIN_WIDTH: i32 = 140;
pub const MIN_HEIGHT: i32 = HEADER_HEIGHT + 2 + 56;
const SNAP_THRESHOLD: i32 = 10;
const SNAP_GAP: i32 = 8;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

impl Rect {
    pub const fn new(x: i32, y: i32, w: i32, h: i32) -> Self {
        Self { x, y, w, h }
    }
    pub fn right(&self) -> i32 {
        self.x + self.w
    }
    pub fn bottom(&self) -> i32 {
        self.y + self.h
    }
    pub fn contains(&self, px: i32, py: i32) -> bool {
        px >= self.x && px < self.right() && py >= self.y && py < self.bottom()
    }
}

/// Bordes que se arrastran al redimensionar.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Edges {
    pub left: bool,
    pub right: bool,
    pub top: bool,
    pub bottom: bool,
}

impl Edges {
    pub const RIGHT: Self = Self { left: false, right: true, top: false, bottom: false };
    pub const BOTTOM: Self = Self { left: false, right: false, top: false, bottom: true };
    pub const LEFT: Self = Self { left: true, right: false, top: false, bottom: false };
    pub const BOTTOM_RIGHT: Self = Self { left: false, right: true, top: false, bottom: true };
    pub const BOTTOM_LEFT: Self = Self { left: true, right: false, top: false, bottom: true };
}

/// Desplazamiento hacia el candidato más cercano, si está dentro del umbral.
fn nearest(value: i32, candidates: &[i32]) -> Option<i32> {
    candidates
        .iter()
        .map(|c| c - value)
        .filter(|d| d.abs() <= SNAP_THRESHOLD)
        .min_by_key(|d| d.abs())
}

struct Candidates {
    left: Vec<i32>,
    right: Vec<i32>,
    top: Vec<i32>,
    bottom: Vec<i32>,
}

fn candidates(area: Rect, others: &[Rect]) -> Candidates {
    let mut c = Candidates {
        left: vec![area.x],
        right: vec![area.right()],
        top: vec![area.y],
        bottom: vec![area.bottom()],
    };
    for o in others {
        c.left.extend([o.x, o.right() + SNAP_GAP]);
        c.right.extend([o.right(), o.x - SNAP_GAP]);
        c.top.extend([o.y, o.bottom() + SNAP_GAP]);
        c.bottom.extend([o.bottom(), o.y - SNAP_GAP]);
    }
    c
}

/// Elige el menor de dos desplazamientos posibles para un eje.
fn pick(a: Option<i32>, b: Option<i32>) -> i32 {
    match (a, b) {
        (Some(a), Some(b)) => if a.abs() <= b.abs() { a } else { b },
        (Some(a), None) => a,
        (None, Some(b)) => b,
        (None, None) => 0,
    }
}

/// Mantiene el rectángulo dentro de `area` (sin cambiar su tamaño salvo que no quepa).
pub fn clamp_into(r: Rect, area: Rect) -> Rect {
    let w = r.w.min(area.w).max(MIN_WIDTH.min(area.w));
    let h = r.h.min(area.h).max(HEADER_HEIGHT.min(area.h));
    let x = r.x.clamp(area.x, (area.right() - w).max(area.x));
    let y = r.y.clamp(area.y, (area.bottom() - h).max(area.y));
    Rect::new(x, y, w, h)
}

/// Rectángulo al mover `start` por (dx, dy). `visible_h` es la altura en pantalla (menor si está enrollado).
pub fn moved(start: Rect, visible_h: i32, dx: i32, dy: i32, area: Rect, others: &[Rect], snap: bool) -> Rect {
    let mut r = Rect::new(start.x + dx, start.y + dy, start.w, visible_h);
    if snap {
        let c = candidates(area, others);
        r.x += pick(nearest(r.x, &c.left), nearest(r.right(), &c.right));
        r.y += pick(nearest(r.y, &c.top), nearest(r.bottom(), &c.bottom));
    }
    let r = clamp_into(r, area);
    Rect::new(r.x, r.y, start.w, start.h)
}

/// Rectángulo al arrastrar los bordes `edges` de `start` por (dx, dy).
pub fn resized(start: Rect, edges: Edges, dx: i32, dy: i32, area: Rect, others: &[Rect], snap: bool) -> Rect {
    let (mut l, mut t, mut r, mut b) = (start.x, start.y, start.right(), start.bottom());
    if edges.left { l += dx; }
    if edges.right { r += dx; }
    if edges.top { t += dy; }
    if edges.bottom { b += dy; }

    if snap {
        let c = candidates(area, others);
        if edges.left { l += nearest(l, &c.left).unwrap_or(0); }
        if edges.right { r += nearest(r, &c.right).unwrap_or(0); }
        if edges.top { t += nearest(t, &c.top).unwrap_or(0); }
        if edges.bottom { b += nearest(b, &c.bottom).unwrap_or(0); }
    }

    l = l.max(area.x);
    t = t.max(area.y);
    r = r.min(area.right());
    b = b.min(area.bottom());
    if r - l < MIN_WIDTH {
        if edges.left { l = r - MIN_WIDTH } else { r = l + MIN_WIDTH }
    }
    if b - t < MIN_HEIGHT {
        if edges.top { t = b - MIN_HEIGHT } else { b = t + MIN_HEIGHT }
    }
    Rect::new(l, t, r - l, b - t)
}

/// Posición libre para un fence nuevo de tamaño (w, h): empieza arriba a la derecha y baja en columnas
/// para no tapar los iconos del escritorio de COSMIC, que se colocan desde la izquierda.
pub fn free_spot(w: i32, h: i32, area: Rect, others: &[Rect]) -> Rect {
    let margin = 20;
    let step = 16;
    let mut x = area.right() - margin - w;
    while x >= area.x + margin {
        let mut y = area.y + margin;
        while y + h <= area.bottom() - margin {
            let cand = Rect::new(x, y, w, h);
            if let Some(hit) = others.iter().find(|o| intersects(&cand, o)) {
                y = hit.bottom() + step;
                continue;
            }
            return cand;
        }
        x -= w + step;
    }
    clamp_into(Rect::new(area.right() - margin - w, area.y + margin, w, h), area)
}

pub fn intersects(a: &Rect, b: &Rect) -> bool {
    a.x < b.right() && b.x < a.right() && a.y < b.bottom() && b.y < a.bottom()
}

#[cfg(test)]
mod tests {
    use super::*;

    const AREA: Rect = Rect::new(0, 0, 1920, 1040);

    #[test]
    fn snaps_to_screen_edges_and_neighbours() {
        let start = Rect::new(100, 100, 300, 200);
        // A 6 px del borde izquierdo: se imanta.
        assert_eq!(moved(start, 200, -94, 0, AREA, &[], true).x, 0);
        // Sin imán se queda donde se suelta.
        assert_eq!(moved(start, 200, -94, 0, AREA, &[], false).x, 6);
        // Junto a otro fence: deja un hueco de 8 px.
        let other = Rect::new(500, 100, 200, 200);
        assert_eq!(moved(start, 200, 95, 0, AREA, &[other], true).right(), 492);
    }

    #[test]
    fn move_keeps_inside_and_full_height() {
        let start = Rect::new(100, 100, 300, 400);
        let r = moved(start, HEADER_HEIGHT, 5000, 5000, AREA, &[], false);
        assert_eq!((r.right(), r.y + HEADER_HEIGHT), (1920, 1040));
        assert_eq!(r.h, 400, "conserva la altura desplegada");
    }

    #[test]
    fn resize_respects_minimum() {
        let start = Rect::new(100, 100, 300, 200);
        let r = resized(start, Edges::BOTTOM_RIGHT, -1000, -1000, AREA, &[], false);
        assert_eq!((r.w, r.h), (MIN_WIDTH, MIN_HEIGHT));
        let r = resized(start, Edges::LEFT, 1000, 0, AREA, &[], false);
        assert_eq!((r.x, r.right()), (400 - MIN_WIDTH, 400));
    }

    #[test]
    fn finds_free_spot() {
        let a = free_spot(300, 200, AREA, &[]);
        assert_eq!((a.x, a.y), (1600, 20));
        let b = free_spot(300, 200, AREA, &[a]);
        assert_eq!((b.x, b.y), (1600, 236));
        assert!(!intersects(&a, &b));
    }
}
