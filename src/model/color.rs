//! Colores de los fences (mismo formato "#RRGGBB" que la versión de Windows).

pub const PRESETS: &[(&str, &str)] = &[
    ("Grafito", "#1E1E1E"),
    ("Negro", "#000000"),
    ("Azul", "#1F4E8C"),
    ("Verde azulado", "#136F63"),
    ("Verde", "#2E7D32"),
    ("Morado", "#5B2A86"),
    ("Rojo", "#8E1B1B"),
    ("Naranja", "#B5541C"),
    ("Gris", "#6B7280"),
    ("Blanco", "#F2F2F2"),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rgb(pub u8, pub u8, pub u8);

/// Acepta "#RGB", "#RRGGBB" y "#AARRGGBB" (el alfa se ignora, como en Windows).
pub fn parse_hex(text: &str) -> Option<Rgb> {
    let h = text.trim().strip_prefix('#')?;
    let digits = |s: &str| u8::from_str_radix(s, 16).ok();
    match h.len() {
        3 => {
            let c: Vec<u8> = h.chars().map(|c| c.to_digit(16).map(|d| (d * 17) as u8)).collect::<Option<_>>()?;
            Some(Rgb(c[0], c[1], c[2]))
        }
        6 => Some(Rgb(digits(&h[0..2])?, digits(&h[2..4])?, digits(&h[4..6])?)),
        8 => Some(Rgb(digits(&h[2..4])?, digits(&h[4..6])?, digits(&h[6..8])?)),
        _ => None,
    }
}

pub fn to_hex(c: Rgb) -> String {
    format!("#{:02X}{:02X}{:02X}", c.0, c.1, c.2)
}

/// Luminancia relativa aproximada en [0, 1].
pub fn luminance(c: Rgb) -> f64 {
    (0.2126 * c.0 as f64 + 0.7152 * c.1 as f64 + 0.0722 * c.2 as f64) / 255.0
}

/// ¿Conviene texto oscuro sobre este fondo (teniendo en cuenta la opacidad)?
pub fn wants_dark_text(c: Rgb, opacity: f64) -> bool {
    luminance(c) > 0.6 && opacity >= 0.5
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses() {
        assert_eq!(parse_hex("#1E1E1E"), Some(Rgb(0x1E, 0x1E, 0x1E)));
        assert_eq!(parse_hex("#fff"), Some(Rgb(255, 255, 255)));
        assert_eq!(parse_hex("#801F4E8C"), Some(Rgb(0x1F, 0x4E, 0x8C)));
        assert_eq!(parse_hex("rojo"), None);
        assert_eq!(to_hex(Rgb(1, 2, 255)), "#0102FF");
        assert!(wants_dark_text(Rgb(242, 242, 242), 0.9));
        assert!(!wants_dark_text(Rgb(30, 30, 30), 0.9));
    }
}
