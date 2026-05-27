# --- Chef base: shared tooling for planner and builder ---
FROM rust:1-bookworm AS chef

# Install cargo-binstall (arch-aware)
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
        "amd64") ARCH="x86_64" ;; \
        "arm64") ARCH="aarch64" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && wget "https://github.com/cargo-bins/cargo-binstall/releases/latest/download/cargo-binstall-${ARCH}-unknown-linux-musl.tgz" \
    && tar -xvf "cargo-binstall-${ARCH}-unknown-linux-musl.tgz" \
    && cp cargo-binstall /usr/local/cargo/bin \
    && rm -rf "cargo-binstall-${ARCH}-unknown-linux-musl.tgz" cargo-binstall

# Install required system packages
RUN apt-get update -y \
    && apt-get install -y --no-install-recommends clang lld \
    && rm -rf /var/lib/apt/lists/*

# Install cargo-leptos and cargo-chef
RUN cargo binstall cargo-leptos cargo-chef -y

# Add the WASM target for client-side hydration
RUN rustup target add wasm32-unknown-unknown

WORKDIR /app

# --- Planner: generate dependency recipe ---
FROM chef AS planner
COPY . .
RUN cargo chef prepare --recipe-path recipe.json

# --- Builder: cook deps then build app ---
FROM chef AS builder

# Copy linker config so cook uses lld
COPY .cargo .cargo

COPY --from=planner /app/recipe.json recipe.json

# Cook server dependencies (this layer is cached when deps don't change)
RUN cargo chef cook --release --no-default-features --features ssr --recipe-path recipe.json

# Cook WASM/client dependencies (also a cached layer)
RUN cargo chef cook --release --no-default-features --features hydrate \
    --target wasm32-unknown-unknown --recipe-path recipe.json

# Copy full source and build
COPY . .

RUN cargo leptos build --release -vv \
    && cp target/release/dumb-mcp-server-proxy /app/app-bin \
    && cp target/release/hash.txt /app/hash.txt \
    && cp -r target/site /app/site-out

# --- Runtime: minimal image ---
FROM debian:bookworm-slim AS runtime

RUN apt-get update -y \
    && apt-get install -y --no-install-recommends openssl ca-certificates \
    && apt-get autoremove -y \
    && apt-get clean -y \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy the server binary and hash file
COPY --from=builder /app/app-bin /app/server
COPY --from=builder /app/hash.txt /app/hash.txt

# Copy the site bundle (JS/WASM/CSS)
COPY --from=builder /app/site-out /app/site

# Copy Cargo.toml — cargo-leptos reads [package.metadata.leptos] at runtime
COPY --from=builder /app/Cargo.toml /app/

RUN mkdir -p /data

ENV RUST_LOG="info"
ENV LEPTOS_SITE_ADDR="0.0.0.0:3000"
ENV LEPTOS_SITE_ROOT="site"
ENV DATABASE_URL="sqlite:/data/proxy.db?mode=rwc"
EXPOSE 3000

CMD ["/app/server"]
