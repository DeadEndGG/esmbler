# syntax=docker/dockerfile:1.7
# Esmbler extension runtime sidecar (Gate 0 spike).
# Build context: spikes/extension-runtime/
#   docker build -t esmbler/ext-runtime:spike .
#   docker run --rm -p 8080:8080 --read-only --cap-drop=ALL --security-opt no-new-privileges \
#     --tmpfs /tmp:size=64m --memory=512m --pids-limit=128 esmbler/ext-runtime:spike
# Production: pin base images by digest (@sha256:...) and verify signatures.

ARG RUST_IMAGE=rust:1.97.0-slim-trixie
ARG RUNTIME_IMAGE=gcr.io/distroless/cc-debian13:nonroot

FROM ${RUST_IMAGE} AS build
WORKDIR /src
RUN rustup target add wasm32-wasip2
# Dependency layer caching: copy manifests first.
COPY wit/ wit/
COPY host/Cargo.toml host/Cargo.lock host/
COPY guest/Cargo.toml guest/Cargo.lock guest/
RUN mkdir -p host/src guest/src \
 && echo 'fn main() {}' > host/src/main.rs && touch guest/src/lib.rs \
 && cargo fetch --locked --manifest-path host/Cargo.toml \
 && cargo fetch --locked --manifest-path guest/Cargo.toml
COPY host/src/ host/src/
COPY guest/src/ guest/src/
RUN cargo build --locked --release --manifest-path host/Cargo.toml \
 && cargo build --locked --release --target wasm32-wasip2 --manifest-path guest/Cargo.toml
# In production, packages are NOT baked into the image: signed components are
# pulled from the package registry by digest. The spike bakes in the example.
COPY packages/ /out/packages/
RUN cp guest/target/wasm32-wasip2/release/ticket_validator.wasm /out/packages/ticket-validator/component.wasm \
 && cp host/target/release/esmbler-ext-host /out/esmbler-ext-host

FROM ${RUNTIME_IMAGE}
# distroless :nonroot => uid/gid 65532, no shell, no package manager.
COPY --from=build --chown=0:0 /out/esmbler-ext-host /usr/local/bin/esmbler-ext-host
COPY --from=build --chown=0:0 /out/packages/ /opt/esmbler/packages/
USER 65532:65532
EXPOSE 8080
# The precompiled-component cache lives on a tmpfs (/tmp); nothing else is writable.
ENTRYPOINT ["/usr/local/bin/esmbler-ext-host", "serve", \
            "--packages", "/opt/esmbler/packages", "--cache", "/tmp/esmbler-ext-cache", \
            "--listen", "0.0.0.0:8080", "--workers", "8", "--pooling"]
