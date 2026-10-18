//! Lectura de `text/uri-list` (RFC 2483), el formato con el que se arrastran archivos en Linux.

use std::ffi::OsString;
use std::os::unix::ffi::OsStringExt;
use std::path::PathBuf;

/// Decodifica `%XX`; devuelve `None` si una secuencia no es válida.
fn percent_decode(s: &str) -> Option<Vec<u8>> {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' {
            let hex = s.get(i + 1..i + 3)?;
            out.push(u8::from_str_radix(hex, 16).ok()?);
            i += 3;
        } else {
            out.push(b[i]);
            i += 1;
        }
    }
    Some(out)
}

/// Rutas locales de una lista de URIs. Ignora comentarios (`#`), URIs no `file:` y hosts remotos.
pub fn parse_uri_list(text: &str) -> Vec<PathBuf> {
    text.lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
        .filter_map(|l| {
            let rest = l.strip_prefix("file://")?;
            // "file:///ruta" o "file://localhost/ruta"
            let path = if rest.starts_with('/') { rest } else { rest.strip_prefix("localhost")? };
            let bytes = percent_decode(path)?;
            Some(PathBuf::from(OsString::from_vec(bytes)))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses() {
        let text = "# comentario\r\nfile:///home/ana/Escritorio/Mi%20carpeta\r\nfile://localhost/tmp/a%C3%B1o.txt\r\n\
                    https://ejemplo.com/x\r\nfile://otro-equipo/x\r\n";
        assert_eq!(
            parse_uri_list(text),
            vec![PathBuf::from("/home/ana/Escritorio/Mi carpeta"), PathBuf::from("/tmp/año.txt")]
        );
        assert!(parse_uri_list("file:///mal%ZZ").is_empty());
    }
}
