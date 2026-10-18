# Tareas habituales: `just run`, `just test`, `just release`

run *ARGS:
    cargo run -- {{ARGS}}

test:
    cargo test

check:
    cargo clippy --all-targets -- -D warnings

release:
    cargo build --release

# Compila, instala en ~/.local/bin y activa el inicio automático al iniciar sesión
install: release
    install -Dm755 target/release/clonfences ~/.local/bin/clonfences
    ~/.local/bin/clonfences --autostart

uninstall:
    -~/.local/bin/clonfences --no-autostart
    -~/.local/bin/clonfences --restore
    rm -f ~/.local/bin/clonfences
