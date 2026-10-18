# Tareas habituales: `just run`, `just test`, `just release`

run *ARGS:
    cargo run -- {{ARGS}}

test:
    cargo test

check:
    cargo clippy --all-targets -- -D warnings

release:
    cargo build --release
