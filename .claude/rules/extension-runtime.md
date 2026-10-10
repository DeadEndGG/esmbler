---
paths:
  - "spikes/extension-runtime/**"
  - "apps/Esmbler.ExtensionRuntime/**"
---

# Extension runtime

- Wasmtime crate pinned exactly (ADR 0006); Rust `wasm32-wasip2` is the only supported guest toolchain until conformance tests exist for another.
- Fresh store per invocation; fuel and epoch deadlines; memory limits; per-tenant concurrency quotas; no WASI filesystem, network, or env grants.
- Host imports are capability-gated per package and tenant; secrets never cross into the guest.
- The sidecar has no database credentials; all data access is mediated by the platform.
