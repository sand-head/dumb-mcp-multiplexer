# Build stage — Rust stable with cargo-leptos
FROM rust:1-bookworm AS builder

# Install cargo-binstall for faster tool installation
RUN wget https://github.com/cargo-bins/cargo-binstall/releases/latest/download/cargo-binstall-x86_64-unknown-linux-musl.tgz \
    && tar -xvf cargo-binstall-x86_64-unknown-linux-musl.tgz \
    && cp cargo-binstall /usr/local/cargo/bin \
    && rm -rf cargo-binstall*

# Install required system packages
RUN apt-get update -y \
    && apt-get install -y --no-install-recommends clang lld \
    && rm -rf /var/lib/apt/lists/*

# Install cargo-leptos
RUN cargo binstall cargo-leptos -y

# Add the WASM target for client-side hydration
RUN rustup target add wasm32-unknown-unknown

WORKDIR /app

# Pre-fetch dependencies (cached unless Cargo.toml/Cargo.lock change)
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo "fn main() {}" > src/main.rs && echo "" > src/lib.rs
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/app/target \
    cargo fetch

# Copy full source
COPY . .

# Build both the SSR server binary and the WASM client bundle
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/app/target \
    cargo leptos build --release -vv \
    && cp target/release/dumb-mcp-server-proxy /app/app-bin \
    && cp -r target/site /app/site-out

# Runtime stage — minimal image
FROM debian:bookworm-slim AS runtime

RUN apt-get update -y \
    && apt-get install -y --no-install-recommends openssl ca-certificates \
    && apt-get autoremove -y \
    && apt-get clean -y \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy the server binary
COPY --from=builder /app/app-bin /app/server

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
