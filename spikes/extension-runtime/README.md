# Spike: extension runtime (Gate 0)

Throwaway spike backing [ADR 0006](../../docs/adr/0006-extension-runtime.md). It answers three
questions: can the .NET host run WebAssembly **components** in-process, which guest toolchain do we
support first, and can a pinned Wasmtime enforce the isolation and limits the master spec requires
(REQ-EXT-001..003)?

**Result:** the .NET binding cannot run components (see [evidence](#1-wasmtime-dotnet-component-model-probe)),
so the runtime is a **Rust sidecar** built on the `wasmtime` crate, pinned to `=48.0.5` (an LTS line),
with the Component Model and WASI 0.2. The first supported guest toolchain is **Rust `wasm32-wasip2` +
`wit-bindgen =0.62.0`**. Every limit worked as designed. A warm invocation (fresh `Store`, instantiate,
call) takes about 50 µs p50 in-process and about 0.6 ms p50 from .NET over loopback HTTP.

This is not production code. The real runtime is built in Gate 3.

## Layout

| Path | What it is |
|---|---|
| `wit/esmbler-extension.wit` | `esmbler:extension@0.1.0`: `types`, the host import `host` (`log`, `get-setting`), the export `validator` (`validate`), and the world `record-validator` |
| `guest/` | Rust guest component (`ticket-validator`). It rejects priority 1 tickets that have no `impact`, with the minimum length taken from the tenant setting. It also has spike-only chaos switches through `"_spike": "spin" \| "oom" \| "secret"` |
| `host/` | Rust sidecar `esmbler-ext-host`, with `serve` (HTTP), `bench` (in-process numbers) and `inspect` (component imports and exports) |
| `packages/ticket-validator/manifest.json` | Package manifest: hooks, capabilities (`log`, `settings_read: ["ticket.*"]`), and per-package limits (fuel 200M, 100 ms, 16 MiB) |
| `dotnet-client/` | .NET 10 console app that calls the sidecar. This is the integration path for the platform |
| `dotnet-probe/` | .NET 10 console app that tries to load the component with NuGet `Wasmtime` 48.0.2. This is the evidence for the decision |
| `Dockerfile` | Multi-stage sidecar image: `rust:1.97.0-slim-trixie` build stage, `gcr.io/distroless/cc-debian13:nonroot` runtime, uid 65532 |
| `run-demo.sh` | Builds everything, runs the bench, starts the sidecar, runs the .NET client, then stops the sidecar |
| `Directory.Build.props`, `Directory.Packages.props` | Keep the spike's .NET projects out of the repo-wide central package management and warnings-as-errors settings |

## Build and run

Prerequisites: Rust 1.95 or later (tested with 1.97.0) with the `wasm32-wasip2` target, and the .NET 10 SDK.

```bash
cd spikes/extension-runtime
./run-demo.sh                      # everything, end to end; leaves nothing running

# or step by step
rustup target add wasm32-wasip2
(cd guest && cargo build --target wasm32-wasip2 --release)
cp guest/target/wasm32-wasip2/release/ticket_validator.wasm packages/ticket-validator/component.wasm
(cd host && cargo build --release)
host/target/release/esmbler-ext-host inspect packages/ticket-validator/component.wasm
host/target/release/esmbler-ext-host bench --packages packages --iterations 10000 [--pooling]
host/target/release/esmbler-ext-host serve --packages packages --listen 127.0.0.1:8080 --pooling &
(cd dotnet-client && dotnet run -c Release -- http://127.0.0.1:8080 1000)
(cd dotnet-probe && dotnet run -- ../packages/ticket-validator/component.wasm)

# container (build context = this directory)
docker build -t esmbler/ext-runtime:spike .
docker run --rm -p 8080:8080 --read-only --cap-drop=ALL --security-opt no-new-privileges \
  --tmpfs /tmp:size=64m --memory=512m --pids-limit=128 esmbler/ext-runtime:spike
```

### HTTP API

`POST /invoke`

```json
{
  "tenant": "acme", "environment": "prod", "package": "ticket-validator", "hook": "validate",
  "input": { "record": { "priority": 1, "title": "Mail down" }, "correlation_id": "…", "actor_id": "user-42" },
  "settings": { "ticket.impact.min-length": "10" },
  "limits": { "timeout_ms": 10 }
}
```

The response looks like this:

```json
{ "status": "rejected",
  "violations": [{ "field": "impact", "code": "ticket.impact.required", "message": "…" }],
  "logs": [{ "level": "info", "msg": "validated validate for tenant acme (1 violations)" }],
  "denied_capabilities": [],
  "metrics": { "instantiate_us": 132, "call_us": 63, "total_us": 344, "fuel_consumed": 9176,
               "limits": { "fuel": 200000000, "timeout_ms": 100, "memory_bytes": 16777216 } } }
```

`status` is one of `accepted | rejected | extension_error | cpu_limit_exceeded | timeout | memory_limit_exceeded | trap`,
plus `unknown_package | unknown_hook | tenant_concurrency_exceeded` (HTTP 429). The effective limits are
`min(server ceiling, package manifest, request)`, so a request can lower a limit but never raise it.
The platform sends only the settings snapshot for the caller's tenant and environment. The sidecar
holds no credentials and has no data access of its own.

`GET /healthz` and `GET /packages` are also available.

## Results observed

Test machine: 4 vCPU Intel Xeon @ 2.80GHz cloud container, Linux 6.18, Rust 1.97.0, .NET SDK 10.0.401,
release builds. Latency numbers come from single runs. Ranges cover the 3 or 4 runs made during the
spike. Expect variance of roughly ±30% on this shared hardware.

### 1. wasmtime-dotnet Component Model probe

The `dotnet-probe` output with NuGet `Wasmtime` 48.0.2 (published 2026-09-18, commit `d9db6b0`):

```text
Wasmtime.Dotnet assembly: 48.0.2.0 (48.0.2+d9db6b0005b93ba09a6ada8ddce2d5a3846511eb)
Public types containing 'Component'/'Wit': <none>
Public members containing 'Component': Config.WithComponentModel, PoolingAllocationConfig.WithTotalComponentInstances, …
Module.FromFile(component) failed as expected: WasmtimeException: WebAssembly module 'component' is not valid: failed to parse WebAssembly module
Core module call add(2,3) = 5
```

- The bundled native `libwasmtime.so` exports the C API's component functions
  (`wasmtime_component_linker_*`, `wasmtime_component_func_call`, `wasmtime_component_linker_add_wasip2`,
  168 `component` symbols in total).
- The managed layer does not bind any of them. There are no `Component`, component `Linker`, WIT value
  or bindgen types. `Config.WithComponentModel(true)` exists, but nothing can consume it.
- The upstream `main` branch was checked on 2026-10-10 (latest commit 2026-09-18). It has no
  `wasmtime_component` P/Invoke declarations and no C# WIT bindgen.

**Conclusion:** using components in-process from .NET today would mean writing and maintaining our
own P/Invoke layer over the component C API, with dynamically typed value marshalling and no code
generation. That fails the "pinned, supportable runtime" requirement. Hence the sidecar.

### 2. Startup and per-invocation latency (`esmbler-ext-host bench`, n=10,000, one thread)

| Metric | On-demand allocator | Pooling allocator |
|---|---|---|
| Component size (`.wasm`, `opt-level="s"`, LTO, stripped) | 90.6 KB | 90.6 KB |
| Cold compile (Cranelift, `.wasm` to native) | 72–117 ms | 75–126 ms |
| `Component::serialize` (`.cwasm` = 335 KB) | 0.3–0.5 ms | 0.3–0.5 ms |
| Engine creation | 0.2 ms | 2.5–4.4 ms (virtual address reservation) |
| Warm start: `Component::deserialize_file` | 0.2–0.3 ms | 0.25–0.4 ms |
| `Linker::instantiate_pre` (`InstancePre`) | 0.02–0.06 ms | 0.03–0.08 ms |
| Instantiate, per call (fresh `Store` + `InstancePre::instantiate`) p50 / p95 / p99 | 15–16 / 26–42 / 56–80 µs | 10–11 / 19–21 / 41–55 µs |
| Guest `validate` call p50 / p95 / p99 | 19–21 / 31–48 / 67–91 µs | 19–20 / 34–40 / 64–79 µs |
| **Total per invocation** (with JSON and context setup) p50 / p95 / p99 | **47–58 / 88–123 / 162–251 µs** | **48–50 / 85–94 / 120–260 µs** |
| Throughput, one thread | 13.3k–16.9k/s | 16.3k–16.9k/s |
| Fuel used by one happy-path `validate` | about 8k–12k | about 8k–12k |

From .NET through the sidecar (`dotnet-client`, HTTP/1.1 keep-alive over loopback, sequential, n=1000):
**p50 0.56–0.67 ms, p95 1.08–1.95 ms, p99 2.4–4.2 ms, max 6.3–6.6 ms** over two runs. The first
call took 144–221 ms because of JIT and connection setup.

### 3. Limits and isolation (all through the sidecar, called from .NET)

| Scenario | Outcome |
|---|---|
| (a) P1 ticket without `impact`, acme `min-length=10` | `rejected`, `impact:ticket.impact.required`, guest log captured, 0.38 ms in the guest |
| (a) P1 with impact, or P3 | `accepted`, 0.14–0.32 ms |
| (b) infinite loop, manifest limits (fuel 200M, 100 ms) | `cpu_limit_exceeded` ("all fuel consumed by WebAssembly") after **29–38 ms** |
| (b) infinite loop, request `timeout_ms=10` | `timeout` ("wasm trap: interrupt", epoch deadline) after **11–18 ms** (epoch tick is 5 ms) |
| (c) unbounded allocation, 16 MiB cap | `memory_limit_exceeded` ("forcing trap when growing memory to 16842752 bytes") after **12–13 ms** |
| Guest reads ungranted `platform.db.connection-string` | host returns `none` and the response reports `denied_capabilities: ["settings_read:platform.db.connection-string"]` |
| Same input, tenant `globex` with no settings | `accepted` (default min length 1), because each invocation gets only its own tenant's settings |
| 8 concurrent slow calls for tenant `noisy` (quota 4) | 4 × HTTP 200, 4 × HTTP **429**. A concurrent call for tenant `quiet` returned 200 `accepted` |

The guest's imports (`inspect`): `esmbler:extension/{types,host}@0.1.0` plus
`wasi:io/{poll,error,streams}@0.2.9`, `wasi:clocks/monotonic-clock@0.2.9`, and
`wasi:cli/{stdin,stdout,stderr,environment,exit,terminal-*}@0.2.9`. Rust `std` pulls these in.
There are **no** `wasi:filesystem`, `wasi:sockets` or `wasi:http` imports. The host still links all of
WASI 0.2, but every invocation's `WasiCtx` is empty: no preopens, env or args, stdin closed,
TCP/UDP/DNS disabled, and stderr captured to an 8 KiB in-memory pipe.

### 4. Container image

The `Dockerfile` builds both the sidecar and the example guest in the build stage. The runtime stage
is distroless `cc-debian13:nonroot`, running as uid 65532, with no shell. The component cache is a
tmpfs at `/tmp`. Run it with `--read-only --cap-drop=ALL --security-opt no-new-privileges`.

Verified on 2026-10-10:

- Image size: **78.7 MB**. The build took 6.5 minutes from cold.
- Ran as `65532:65532` with `--read-only --cap-drop=ALL --security-opt no-new-privileges --tmpfs /tmp
  --memory=512m --pids-limit=128`. `docker exec … id` fails because the image has no binaries
  besides the sidecar.
- Container startup took 84 ms, including the first compile into the tmpfs cache.
- The happy-path call returned `rejected` as expected, and the `oom` call returned
  `memory_limit_exceeded`.
- The container was removed afterwards.

## Toolchain compatibility matrix (as of 2026-10-10)

| Guest toolchain | Component Model output | WASI 0.2 (p2) | WASI 0.3 (p3) | Verified here | Esmbler decision |
|---|---|---|---|---|---|
| **Rust**: `wasm32-wasip2` (tier 2) + `wit-bindgen 0.62.0` (`cargo-component 0.21.1` optional) | Native: rustc and `wasm-component-ld` emit a component directly | Yes, stable target | `wasm32-wasip3` target exists in rustc 1.97 but is tier 3 (not shipped by rustup); `wit-bindgen` async support is experimental | **Built and run** | **Supported (first and only toolchain at GA)** |
| JavaScript/TypeScript: `componentize-js 0.23.0` / `jco 1.37.0` | Yes, through SpiderMonkey embedding | Yes | Experimental | Not built (registry versions checked) | Candidate for a second toolchain (low-code script hooks). Must first be measured: components are about 8–12 MB, compile time is much higher, and memory per instance is larger |
| Go: TinyGo `-target=wasip2` + `wit-bindgen-go` | Yes (TinyGo 0.33 or later) | Yes | No | Not built | Not supported. Language subset and GC/reflection limits. Revisit |
| Go (upstream `gc`) | No: `GOOS=wasip1` core modules only (Go 1.27.2) | No (p1 only) | No | Not built | Not supported |
| C#/.NET: `componentize-dotnet 0.8.0-preview00011` (NativeAOT-LLVM) | Yes | Yes | No | Not built (NuGet version checked) | Not supported. Still preview, NativeAOT-LLVM is experimental, and large output. Revisit when it reaches 1.0 |
| Python: `componentize-py 0.25.1` | Yes (embeds CPython) | Yes | No | Not built | Not supported (large components, slow cold start) |
| Host: `wasmtime` crate 48.0.5 (LTS) | Full (`component-model`, `bindgen!`) | Yes (`wasmtime-wasi` feature `p2`) | Feature `p3`: "experimental, unstable and incomplete" per crate docs; WIT `wasi:*@0.3.0` | **Used** | Host runtime. WASI 0.3 is **off** (feature not compiled in) |
| Host: `Wasmtime` NuGet 48.0.2 (wasmtime-dotnet) | **No managed API** (core modules only) | p1 only (`WasiConfiguration`) | No | **Probed** | Rejected for extensions |

## Gotchas

1. **`record` is a WIT keyword.** The parameter has to be written `%record: record-json`. It
   appears as `record` in Rust bindings.
2. **Wasmtime 48 has its own `wasmtime::Error`, not `anyhow::Error`.** `From<wasmtime::Error> for
   anyhow::Error` exists, but the reverse conversion does not. Functions that take errors
   (`classify_trap`) must use `wasmtime::Error`. Use `root_cause()` for messages returned to callers,
   and keep the full wasm backtrace in host logs only.
3. **Disabled Cargo features remove `Config` methods.** For example, `Config::wasm_threads` does not
   exist without the `threads` feature. The spike compiles threads, GC, component-model-async (p3) and
   WAT out of wasmtime.
4. **Rust `std` guests import WASI CLI and IO interfaces even for pure compute.** WASI 0.2 must be
   linked, with an empty context. A WIT-only linker would reject the component. The guest imports
   `@0.2.9`, which wasmtime 48 satisfies through semver-compatible matching.
5. **Fuel is under-reported after an epoch interrupt.** The spin loop stopped by the 10 ms deadline
   reported about 3.3k fuel consumed. Use wall time, not fuel, to account for interrupted calls. Fuel
   is still the deterministic CPU cap.
6. **`Component::deserialize` is `unsafe`.** Only deserialize `.cwasm` files the sidecar wrote itself,
   into a private cache whose key includes `Engine::precompile_compatibility_hash()`. Never accept
   precompiled artifacts from a package upload.
7. **The pooling allocator trades about 3 ms of engine startup** (address-space reservation) for
   roughly 35% lower instantiate p50 and a tighter tail. It must be sized: total memories × max
   memory per slot.
8. **`tiny_http` dropped keep-alive connections** under concurrent .NET `HttpClient` load (the second
   request failed with "response ended prematurely"). The spike moved to axum/hyper, with guest
   execution on tokio's blocking pool capped by `max_blocking_threads`.
9. **The repo root enables central package management and `TreatWarningsAsErrors`.** The spike has
   its own `Directory.Build.props` and `Directory.Packages.props`, so spike-only packages (`Wasmtime`)
   stay out of the central list.
10. **Docker builds inside this sandbox** need the egress-proxy CA, which `rustup` and `cargo` reject
    otherwise. The image was verified with a sandbox-only copy of the Dockerfile that injects the CA.
    The committed `Dockerfile` contains no sandbox-specific settings.
