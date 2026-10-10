#!/usr/bin/env bash
# Builds the guest + sidecar, runs the in-process benchmark, then starts the sidecar
# and drives it from the .NET client. Leaves nothing running.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="/root/.cargo/bin:/root/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
PORT="${PORT:-18733}"
CACHE="${CACHE:-$(mktemp -d)}"

echo "== build guest (wasm32-wasip2)"
rustup target add wasm32-wasip2 >/dev/null
(cd guest && cargo build --quiet --target wasm32-wasip2 --release)
cp guest/target/wasm32-wasip2/release/ticket_validator.wasm packages/ticket-validator/component.wasm

echo "== build sidecar host"
(cd host && cargo build --quiet --release)
host/target/release/esmbler-ext-host inspect packages/ticket-validator/component.wasm

echo "== in-process bench (on-demand allocator)"
host/target/release/esmbler-ext-host bench --packages packages --cache "$CACHE" --iterations 10000
echo "== in-process bench (pooling allocator)"
host/target/release/esmbler-ext-host bench --packages packages --cache "$CACHE" --iterations 10000 --pooling

echo "== sidecar + .NET client (port $PORT)"
host/target/release/esmbler-ext-host serve --packages packages --cache "$CACHE" --listen "127.0.0.1:$PORT" --workers 8 --pooling 2> "$CACHE/serve.log" &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 100); do curl -sf "http://127.0.0.1:$PORT/healthz" >/dev/null && break; sleep 0.05; done
head -1 "$CACHE/serve.log"
curl -s -H "Content-Type: application/json" "http://127.0.0.1:$PORT/invoke" \
  -d '{"tenant":"acme","environment":"prod","package":"ticket-validator","hook":"validate","input":{"record":{"priority":1}},"settings":{"ticket.impact.min-length":"10"}}'
echo
(cd dotnet-client && dotnet run -c Release -- "http://127.0.0.1:$PORT" "${ITERATIONS:-1000}")
