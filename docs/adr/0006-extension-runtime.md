---
title: "ADR 0006: Extension runtime"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-EXT-001, REQ-EXT-002, REQ-EXT-003]
---

# ADR 0006: Extension runtime

## Context and problem statement

The master spec, section "Extensibility", requires server-side extensions to run in a WebAssembly
sandbox with these properties:

- It uses the **Component Model**, **WIT** interfaces and a **pinned Wasmtime** runtime.
- Permissions are **capability-based**.
- **CPU, memory and time are limited.**
- **Tenants and environments are isolated.**

The spec also asks us to:

- evaluate WASI 0.3 against the host interfaces we need and the language toolchains that actually exist;
- publish a tested compatibility matrix;
- start with **one** supported toolchain.

The non-functional requirements add startup and execution budgets, host-call and egress quotas, and
one more rule: an overloaded tenant or extension must not be able to exhaust the shared worker pool.

The execution brief adds: *"Verify Component Model support in the .NET host; if insufficient, run
the runtime as an isolated sidecar container."* The backend is C# on .NET 10 (ASP.NET Core).

This ADR settles three things:

1. where Wasmtime runs (inside the .NET process or beside it);
2. which guest toolchain is supported first;
3. how the isolation and limits are enforced.

It is based on the Gate 0 spike. The production runtime is built in Gate 3. This ADR is the decision
and the evidence for it.

## Decision drivers

- **Real Component Model support.** We need to load components, link WIT imports and call typed WIT
  exports, with code generated from WIT rather than hand-written marshalling.
- **A pinned, supported runtime** that gets security fixes on a predictable schedule (an LTS line).
- **Enforced limits:**
  - deterministic CPU (fuel);
  - wall-clock time (epoch interruption);
  - linear memory, tables and instances (`ResourceLimiter`);
  - concurrency per tenant.
- **Isolation by default:**
  - no state shared between invocations, tenants or environments;
  - no ambient filesystem, network, environment or credentials;
  - every host call capability-checked and mediated.
- **Blast radius.** A runtime bug or escape must not land inside the process that holds database
  credentials and tenant data.
- **Low latency for synchronous hooks.** Before-save validators sit on the record write path.
- **A mature guest toolchain**, so extension authors and our own first-party code (the dogfooding
  rule) start on solid ground.
- **Operability.** Separate scaling, memory accounting and failure domain from the API.

## Considered options

1. **In-process with `wasmtime-dotnet`** (NuGet `Wasmtime`). Embed Wasmtime in the ASP.NET Core
   process.
2. **Rust sidecar using the `wasmtime` crate.** A small Rust service in its own container hosts
   components through the Component Model and WASI 0.2. The platform calls it over a local RPC
   (HTTP/JSON in the spike; gRPC or HTTP/2 can come later behind the same port).
3. **Other options:**
   - 3a. our own P/Invoke layer over Wasmtime's component C API, used from .NET;
   - 3b. a different engine (WasmEdge, Wasmer, or wazero in a Go sidecar);
   - 3c. JavaScript isolates (V8 or workerd) instead of Wasm.

## Decision outcome

**Chosen option: (2), a Rust sidecar built on the `wasmtime` crate.**

| Item | Decision |
|---|---|
| Host runtime | `wasmtime = "=48.0.5"` and `wasmtime-wasi = "=48.0.5"` (feature `p2` only), pinned exactly. 48.x is a Wasmtime LTS line |
| Compiled-out wasmtime features | threads, GC, component-model-async (WASI 0.3), WAT |
| WASI | **0.2** (wasi:io/cli/clocks/random, linked with an empty context). WASI 0.3 is not enabled (see the assessment below) |
| Contract | `esmbler:extension@0.1.0`: `types`, the host import `host` (`log`, `get-setting`), the export `validator.validate(ctx, %record) -> result<list<violation>, string>`, and the world `record-validator` |
| First supported guest toolchain | **Rust on `wasm32-wasip2`** (tier 2) with `wit-bindgen = "=0.62.0"`. `cargo-component` is optional. Minimum Rust is 1.95 (wasmtime 48's MSRV for the host); tested with 1.97.0 |
| Integration | An `IExtensionRuntime` port in the Extensions module, with an HTTP client adapter to the sidecar. The platform resolves tenant and environment from the authenticated context, never from extension input |
| Deployment | Its own container (distroless, non-root, read-only rootfs, no capabilities). It has **no database or vault credentials** and its network policy allows ingress from the platform only and **denies all egress** |

Option (1) is rejected on evidence. The managed `wasmtime-dotnet` 48.0.2 API (2026-09-18, latest)
has **no Component Model surface**: no `Component` type, no component `Linker`, no WIT value types,
no bindgen. `Config.WithComponentModel(true)` exists, but nothing can use it. Loading our component
through `Module.FromFile` fails with *"failed to parse WebAssembly module"*. The native library
underneath does export the C API's component functions, which leads to option 3a.

Option 3a is rejected because we would be writing and maintaining a dynamically typed marshalling
layer over an unstable C API, with no WIT code generation, inside the process that holds tenant
data. That is the opposite of "pinned, supportable runtime" plus "small blast radius".

Option 3b is rejected because Component Model and WASI 0.2 support in those engines lags Wasmtime,
which is the Bytecode Alliance reference implementation, and the spec names Wasmtime.

Option 3c is rejected because it is not the Component Model and ties us to one guest language.

### Consequences

Good:

- We get the full, first-class Component Model API: `bindgen!`, `InstancePre`, `StoreLimits`,
  fuel, epochs and the pooling allocator. All of it is on an LTS line we pin and bump deliberately.
- The process boundary is a security boundary. A runtime bug or sandbox escape lands in a container
  with no credentials, no egress and no tenant data beyond the request being processed.
- The sidecar scales separately from the API. Its memory accounting (pooling slots) is separate, and
  a runaway guest cannot cause GC pressure or thread starvation in ASP.NET Core.
- Host calls are mediated by design. The sidecar can only do what the request and the manifest
  allow, and every platform-side effect goes back through the platform's authorization.

Bad, and how we mitigate it:

- **A network hop on the synchronous write path.** Measured at 0.56–0.67 ms p50 and 2.4–4.2 ms p99
  over loopback HTTP/1.1, against about 50 µs for the execution itself. It is acceptable for before-save
  hooks with budgets of 10–100 ms. Deploy the sidecar on the same node or pod (localhost or a Unix
  socket) and keep connections alive.
- **A second language (Rust) in the platform team's stack.** The sidecar is small: wasmtime glue,
  limits, capability checks and RPC. It is owned by platform-architecture and covered by
  conformance tests.
- **Another deployable, with health checks, versioning and image pipeline.** The sidecar's contract
  is versioned (`/invoke` plus the WIT package version). The platform handles an unavailable sidecar
  through each hook's declared fail policy (fail closed by default for validators).
- **Host imports that need platform data** must either be passed in the request (as the settings
  snapshot is) or call back to the platform's internal API with a short-lived, invocation-scoped
  token. Callbacks are designed in Gate 3. The spike passes only a snapshot.

## Spike evidence

The full details, commands and raw output are in
[../../spikes/extension-runtime/README.md](../../spikes/extension-runtime/README.md).

Setup: 4 vCPU Xeon @ 2.8 GHz container, release builds, single-threaded bench, n=10,000. The guest
is the Rust `ticket-validator`: 90.6 KB `.wasm`, 335 KB precompiled `.cwasm`.

| Measurement | Result |
|---|---|
| `wasmtime-dotnet` 48.0.2 loads a component | **No.** No managed component API; `Module.FromFile` fails to parse the component |
| Cold compile (Cranelift) of the component | 72–126 ms (once per package version per node, then cached) |
| Warm start (`Component::deserialize_file` + `instantiate_pre`) | 0.25–0.45 ms per package; engine creation 0.2 ms (on-demand) or about 3 ms (pooling) |
| Per invocation: fresh `Store`, instantiate from `InstancePre`, call `validate` | p50 **47–58 µs**, p99 162–251 µs (on-demand); p50 **48–50 µs**, p99 120–260 µs (pooling); instantiate alone p50 15 µs (on-demand) or 10 µs (pooling) |
| Throughput per core | about 13k–17k invocations/s |
| .NET client to sidecar, HTTP keep-alive over loopback | p50 **0.56–0.67 ms**, p95 1.1–1.9 ms, p99 2.4–4.2 ms |
| Infinite loop with fuel 200M | `cpu_limit_exceeded` after 29–38 ms |
| Infinite loop with a 10 ms deadline (epoch tick 5 ms) | `timeout` (`wasm trap: interrupt`) after 11–18 ms |
| Unbounded allocation with a 16 MiB cap | `memory_limit_exceeded` (`forcing trap when growing memory`) after about 12 ms |
| Read of an ungranted setting | `none` returned, and the denial is reported in `denied_capabilities` |
| 8 concurrent slow calls for one tenant (quota 4) | 4 succeeded, 4 got HTTP 429; another tenant's concurrent call succeeded |

## WASI 0.2 vs 0.3 assessment

| Aspect | WASI 0.2 (p2) | WASI 0.3 (p3) |
|---|---|---|
| Status | Stable and widely implemented (`wasi:*@0.2.x`; our guest imports `@0.2.9`) | `wasi:*@0.3.0` WIT exists. In `wasmtime-wasi` 48 the `p3` module is documented as *"experimental, unstable and incomplete"* and needs `component-model-async` |
| Async model | Pollables and streams as resources (`wasi:io`); sync calls from the host's view | Native `async` functions, `stream<T>` and `future<T>` in the Component Model: better composition and concurrency |
| Rust guest | `wasm32-wasip2` is tier 2 and ships with rustup | `wasm32-wasip3` is in rustc 1.97's target list but is tier 3 (not shipped by rustup). `wit-bindgen` async is experimental |
| Other guests | JS (componentize-js), TinyGo, componentize-dotnet and componentize-py all target p2 | Mostly not yet |
| Required host interfaces | Our contract is synchronous: validate, log, get-setting. It needs **none** of WASI's I/O; WASI is linked only because language standard libraries import CLI and IO interfaces | Async host calls, such as mediated connector calls and streaming record reads, are where p3 helps. Those come later (Gate 3 or after) |

**Decision:**

- Target WASI 0.2 now and keep the `component-model-async` and `p3` features compiled out.
- Write Esmbler's own WIT interfaces (`esmbler:extension/*`) so they do not depend on WASI I/O. A
  later p3 move then changes how the host links components, not the extension contract.
- Re-evaluate when the revisit triggers below fire.
- Any async hook (for example, a mediated outbound connector) gets a *new* WIT world version
  (`esmbler:extension@0.2.0`). Existing p2 worlds stay supported side by side.

## Toolchain compatibility matrix

As of 2026-10-10. "Verified" means it was built and run in the spike. The other rows were checked
against registry and package versions only, and need a conformance run before any support decision
changes.

| Guest toolchain | Component Model | WASI 0.2 | WASI 0.3 | Verified | Esmbler support |
|---|---|---|---|---|---|
| **Rust `wasm32-wasip2` + `wit-bindgen 0.62.0`** | Yes (native component output) | Yes (tier 2) | Tier 3 target only; experimental | **Yes** | **Supported. The only toolchain at first release** |
| JS/TS: `componentize-js 0.23.0` / `jco 1.37.0` | Yes (embedded SpiderMonkey) | Yes | Experimental | No | Candidate for a second toolchain after conformance, isolation and size/latency tests (components are several MB, with higher compile and instantiate cost) |
| Go: TinyGo `wasip2` + `wit-bindgen-go` | Yes | Yes | No | No | Not supported (language subset, GC and reflection limits) |
| Go: upstream toolchain (1.27.2) | No (`wasip1` core modules only) | No | No | No | Not supported |
| C#: `componentize-dotnet 0.8.0-preview` (NativeAOT-LLVM) | Yes | Yes | No | No | Not supported (preview, experimental AOT-LLVM). Revisit at 1.0 |
| Python: `componentize-py 0.25.1` | Yes (embedded CPython) | Yes | No | No | Not supported (large components, slow cold start) |
| Host: `wasmtime` crate 48.0.5 LTS | Full | Yes (`p2`) | `p3` experimental | **Yes** | **Host runtime** |
| Host: NuGet `Wasmtime` 48.0.2 | **No managed API** | p1 only | No | **Probed** | Rejected |

Support is per toolchain *and* per world version. A toolchain is added only after the extension
conformance suite passes for it: contract tests for each world, the limits and isolation tests from
the spike, and the size and latency budgets.

## Isolation and limits design

These are the rules Gate 3 must implement. The spike demonstrated each item marked ✔.

- **Fresh `Store` per invocation** ✔. Each call creates a new `Store` and instantiates from a cached
  `InstancePre`, so no linear memory, globals, tables or resource handles survive between calls,
  tenants or environments. There is no instance reuse or pooling of *instances*. Only the pooling
  allocator's *slots* are reused, and Wasmtime resets (decommits or zeroes) them before reuse. Engine and compiled code are
  shared because they are immutable.
- **CPU and time** ✔. Fuel bounds deterministic CPU work (and serves as a metering signal). Epoch
  interruption bounds wall-clock time, with a 5 ms tick and a deadline of `ceil(timeout / tick) + 1`
  ticks. Effective limits are `min(server ceiling, manifest, request)`, so a request can only lower
  them. Both traps are mapped to stable statuses (`cpu_limit_exceeded`, `timeout`) and then to the
  hook's fail policy.
- **Memory** ✔. `StoreLimits` caps linear memory per store (16 MiB for the example package, 32 MiB
  server ceiling), plus instances (16), tables (8), memories (2) and table elements, with
  `trap_on_grow_failure(true)`. The pooling allocator adds hard per-slot caps and bounds the total
  footprint. The wasm stack is capped at 512 KiB. The container itself has a cgroup memory limit.
- **Capability-gated host imports** ✔:
  - `log` requires the `log` grant and is limited to 64 lines and 16 KiB per invocation.
  - `get-setting` requires a `settings_read` pattern that matches the key. Otherwise it returns
    `none` and the denial is reported for audit.
  - Grants come from the signed package manifest, approved at install time. Unknown or ungranted
    imports should fail at install time (link check), not at run time.
- **Ambient authority removed** ✔. The WASI 0.2 context grants nothing: no preopens, env or args,
  stdin closed, stdout discarded, stderr captured to a bounded buffer, TCP/UDP/DNS disabled.
  `wasi:http` is not linked.
- **Egress denied by default.** The sidecar's network policy denies all egress. Future outbound
  calls go through a host-mediated connector import (`esmbler:extension/connector`) that the
  *platform* executes. Destination, credentials (by vault reference), quotas and audit stay on the
  platform side, and extensions never receive raw secrets.
- **Host calls mediated by the platform.** The sidecar runs as its own container with **no database
  or vault credentials**. Data reaches a guest only in the invocation payload: the record being
  validated, plus the settings snapshot for that tenant and environment, already filtered by the
  manifest's grants. Any call back to the platform uses an invocation-scoped token bound to tenant,
  environment, package and correlation ID, and goes through the normal authorization pipeline, where
  agent and extension access is intersected with the user's.
- **Per-tenant concurrency quotas** ✔. An in-flight cap per tenant (HTTP 429 when exceeded) and a
  global cap on executing guests (`max_blocking_threads`) stop one tenant or extension from
  exhausting the pool. Gate 3 adds:
  - per-tenant, per-package rate and fuel budgets per time window (the "governors");
  - queue-time budgets;
  - metrics for quota rejections and limit traps per tenant and package.
- **Tenant/environment attribution.** The platform, not the guest, supplies `hook-context` (tenant,
  environment, package, correlation, actor). Logs and metrics are labelled with it. Separate
  environments are separate packages and settings snapshots, even when they share a node.
- **Artifact integrity.** Components are installed from the package registry by digest and signature.
  `.cwasm` precompilation happens only inside the sidecar, into a private cache keyed by
  `Engine::precompile_compatibility_hash()` plus the component digest. Precompiled artifacts are
  never accepted from uploads, because `Component::deserialize` is `unsafe`.
- **Container hardening.** Distroless image, non-root uid 65532, `--read-only` rootfs with a tmpfs
  cache, `--cap-drop=ALL`, `no-new-privileges`, cgroup memory and pids limits, and seccomp default.
  Base images are pinned by digest in production.

## Revisit triggers

- **`wasmtime-dotnet` ships a supported Component Model API** (component loading, a typed WIT
  linker, and ideally C# bindgen). Re-evaluate in-process hosting for latency, still weighing the
  blast-radius argument.
- **The WASI 0.3 `p3` implementation is marked stable** in an LTS Wasmtime release and
  `wasm32-wasip3` reaches tier 2. Plan the p3 migration and async worlds.
- **Wasmtime LTS lifecycle.** Move to the next LTS line before 48.x leaves support, and apply any
  Wasmtime security advisory within the platform's patch SLA.
- **Customer demand for a second guest language.** Run the conformance suite for that toolchain
  (JS/TS first) before declaring support.
- **The sidecar hop breaks a budget.** If p99 for synchronous hooks exceeds budget in Gate 3 load
  tests, move to a Unix domain socket or gRPC/HTTP2, co-locate the sidecar per node, or reconsider
  in-process hosting.
- **A sandbox escape or limit-bypass CVE** in Wasmtime or Cranelift. Patch at once and re-run the
  isolation tests.
