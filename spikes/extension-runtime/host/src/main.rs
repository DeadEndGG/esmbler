//! Esmbler extension runtime sidecar (Gate 0 spike — throwaway code).
//!
//! Hosts WebAssembly components implementing `esmbler:extension/record-validator@0.1.0`
//! with the `wasmtime` crate (Component Model + WASI 0.2), and exposes a tiny HTTP API:
//!
//!   GET  /healthz
//!   GET  /packages
//!   POST /invoke   {tenant, environment, package, hook, input, settings?, limits?}
//!
//! Isolation/limits demonstrated:
//!   * fresh `Store` per invocation (no state shared between calls, tenants or environments)
//!   * CPU limit via fuel AND wall-clock limit via epoch interruption
//!   * linear-memory / instance / table limits via `StoreLimits` (ResourceLimiter)
//!   * WASI context with no preopens, no env, no args, no stdin, no sockets, no DNS
//!   * capability-gated host imports (`log`, `get-setting`) driven by the package manifest
//!   * per-tenant concurrency quota (HTTP 429 when exceeded)
//!
//! Sub-commands:
//!   serve   --packages DIR [--cache DIR] [--listen ADDR] [--workers N] [--pooling]
//!   bench   --packages DIR [--cache DIR] [--iterations N] [--pooling]
//!   inspect FILE.wasm      (prints the component's imports/exports)

use anyhow::{Context as _, Result, bail};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use wasmtime::component::{Component, HasSelf, Linker, ResourceTable};
use wasmtime::{Config, Engine, InstanceAllocationStrategy, Store, StoreLimits, StoreLimitsBuilder, Trap};
use wasmtime_wasi::p2::pipe::MemoryOutputPipe;
use wasmtime_wasi::{WasiCtx, WasiCtxView, WasiView};

wasmtime::component::bindgen!({
    path: "../wit",
    world: "record-validator",
});

use esmbler::extension::types::{HookContext, LogLevel, Violation};

// ---------------------------------------------------------------------------
// Configuration / manifest
// ---------------------------------------------------------------------------

/// Epoch tick period. Timeouts are enforced with this granularity.
const EPOCH_TICK: Duration = Duration::from_millis(5);

/// Server-wide ceilings. A package manifest or request may only LOWER these.
#[derive(Clone, Copy, Debug, Serialize)]
struct Ceilings {
    fuel: u64,
    timeout_ms: u64,
    memory_bytes: usize,
    max_log_lines: usize,
    max_log_bytes: usize,
    per_tenant_concurrency: usize,
    max_request_bytes: usize,
}

const CEILINGS: Ceilings = Ceilings {
    fuel: 500_000_000,
    timeout_ms: 250,
    memory_bytes: 32 << 20,
    max_log_lines: 64,
    max_log_bytes: 16 << 10,
    per_tenant_concurrency: 4,
    max_request_bytes: 1 << 20,
};

#[derive(Deserialize, Serialize, Clone, Debug)]
struct Manifest {
    id: String,
    version: String,
    world: String,
    component: String,
    hooks: Vec<String>,
    #[serde(default)]
    capabilities: Capabilities,
    #[serde(default)]
    limits: PackageLimits,
}

#[derive(Deserialize, Serialize, Clone, Debug, Default)]
struct Capabilities {
    /// Grants `host.log`.
    #[serde(default)]
    log: bool,
    /// Grants `host.get-setting` for keys matching these patterns (exact or `prefix.*`).
    #[serde(default)]
    settings_read: Vec<String>,
}

impl Capabilities {
    fn may_read_setting(&self, key: &str) -> bool {
        self.settings_read.iter().any(|p| match p.strip_suffix('*') {
            Some(prefix) => key.starts_with(prefix),
            None => p == key,
        })
    }
}

#[derive(Deserialize, Serialize, Clone, Debug, Default)]
struct PackageLimits {
    fuel: Option<u64>,
    timeout_ms: Option<u64>,
    memory_bytes: Option<usize>,
}

// ---------------------------------------------------------------------------
// Per-invocation store state (one per call; dropped afterwards)
// ---------------------------------------------------------------------------

struct InvocationState {
    wasi: WasiCtx,
    table: ResourceTable,
    limits: StoreLimits,
    caps: Arc<Capabilities>,
    settings: HashMap<String, String>,
    logs: Vec<LogLine>,
    log_bytes: usize,
    denied: Vec<String>,
}

#[derive(Serialize, Clone, Debug)]
struct LogLine {
    level: &'static str,
    msg: String,
}

impl WasiView for InvocationState {
    fn ctx(&mut self) -> WasiCtxView<'_> {
        WasiCtxView { ctx: &mut self.wasi, table: &mut self.table }
    }
}

impl esmbler::extension::types::Host for InvocationState {}

impl esmbler::extension::host::Host for InvocationState {
    fn log(&mut self, level: LogLevel, msg: String) {
        if !self.caps.log {
            self.denied.push("log".into());
            return;
        }
        if self.logs.len() >= CEILINGS.max_log_lines || self.log_bytes + msg.len() > CEILINGS.max_log_bytes {
            return; // silently drop: rate/size limited
        }
        self.log_bytes += msg.len();
        let level = match level {
            LogLevel::Trace => "trace",
            LogLevel::Debug => "debug",
            LogLevel::Info => "info",
            LogLevel::Warn => "warn",
            LogLevel::Error => "error",
        };
        self.logs.push(LogLine { level, msg });
    }

    fn get_setting(&mut self, key: String) -> Option<String> {
        if !self.caps.may_read_setting(&key) {
            self.denied.push(format!("settings_read:{key}"));
            return None;
        }
        self.settings.get(&key).cloned()
    }
}

// ---------------------------------------------------------------------------
// Runtime: engine + precompiled, pre-linked packages
// ---------------------------------------------------------------------------

struct LoadedPackage {
    manifest: Manifest,
    caps: Arc<Capabilities>,
    pre: RecordValidatorPre<InvocationState>,
}

struct Runtime {
    engine: Engine,
    packages: HashMap<String, Arc<LoadedPackage>>,
    tenant_inflight: Mutex<HashMap<String, usize>>,
}

#[derive(Default, Serialize, Debug, Clone)]
struct LoadTimings {
    engine_ms: f64,
    per_package: Vec<PackageLoadTiming>,
}

#[derive(Serialize, Debug, Clone)]
struct PackageLoadTiming {
    package: String,
    source: &'static str, // "compiled" | "cache"
    compile_or_deserialize_ms: f64,
    serialize_ms: Option<f64>,
    cwasm_bytes: usize,
    wasm_bytes: usize,
    instance_pre_ms: f64,
}

fn build_engine(pooling: bool) -> Result<Engine> {
    let mut cfg = Config::new();
    cfg.wasm_component_model(true)
        .consume_fuel(true)
        .epoch_interruption(true)
        .max_wasm_stack(512 << 10)
        .cranelift_opt_level(wasmtime::OptLevel::Speed)
        .parallel_compilation(true);
    // Threads, GC, component-model-async (WASI 0.3) etc. are compiled out via
    // Cargo features (see Cargo.toml) to shrink the attack surface.
    if pooling {
        let mut p = wasmtime::PoolingAllocationConfig::default();
        p.total_component_instances(256)
            .total_memories(256)
            .total_tables(256)
            .max_memory_size(CEILINGS.memory_bytes)
            .max_component_instance_size(1 << 20)
            .max_core_instances_per_component(16)
            .max_memories_per_component(2)
            .max_tables_per_component(4);
        cfg.allocation_strategy(InstanceAllocationStrategy::Pooling(p));
    }
    Ok(Engine::new(&cfg)?)
}

fn build_linker(engine: &Engine) -> Result<Linker<InvocationState>> {
    let mut linker = Linker::<InvocationState>::new(engine);
    // WASI 0.2 interfaces are linked so that std-based guests (which import
    // wasi:cli/*, wasi:io/*, wasi:clocks/*, wasi:random/*, wasi:filesystem/* ...)
    // instantiate, but the per-invocation WasiCtx grants nothing: no preopens,
    // no env/args, no stdin, sockets/DNS disabled.
    wasmtime_wasi::p2::add_to_linker_sync(&mut linker)?;
    RecordValidator::add_to_linker::<_, HasSelf<_>>(&mut linker, |s| s)?;
    Ok(linker)
}

impl Runtime {
    fn load(packages_dir: &Path, cache_dir: &Path, pooling: bool) -> Result<(Self, LoadTimings)> {
        let mut timings = LoadTimings::default();
        let t = Instant::now();
        let engine = build_engine(pooling)?;
        timings.engine_ms = ms(t.elapsed());
        std::fs::create_dir_all(cache_dir).ok();
        let linker = build_linker(&engine)?;

        let mut packages = HashMap::new();
        for entry in std::fs::read_dir(packages_dir).with_context(|| format!("reading {packages_dir:?}"))? {
            let dir = entry?.path();
            let mpath = dir.join("manifest.json");
            if !mpath.is_file() {
                continue;
            }
            let manifest: Manifest = serde_json::from_slice(&std::fs::read(&mpath)?)
                .with_context(|| format!("parsing {mpath:?}"))?;
            if manifest.world != "esmbler:extension/record-validator@0.1.0" {
                bail!("{}: unsupported world {}", manifest.id, manifest.world);
            }
            let wasm = std::fs::read(dir.join(&manifest.component))
                .with_context(|| format!("{}: reading component {}", manifest.id, manifest.component))?;

            // Cache key = engine compatibility hash + component bytes.
            let mut h = std::collections::hash_map::DefaultHasher::new();
            engine.precompile_compatibility_hash().hash(&mut h);
            wasm.hash(&mut h);
            let cpath = cache_dir.join(format!("{}-{:016x}.cwasm", manifest.id, h.finish()));

            let t = Instant::now();
            let (component, source, serialize_ms, cwasm_bytes) = if cpath.is_file() {
                // SAFETY: the cache dir is private to this process/container and only
                // ever written by `Component::serialize` below with a key that includes
                // the engine compatibility hash. Never deserialize untrusted bytes.
                let c = unsafe { Component::deserialize_file(&engine, &cpath)? };
                let len = std::fs::metadata(&cpath)?.len() as usize;
                (c, "cache", None, len)
            } else {
                let c = Component::from_binary(&engine, &wasm)?;
                let ts = Instant::now();
                let bytes = c.serialize()?;
                let s = ms(ts.elapsed());
                std::fs::write(&cpath, &bytes).ok();
                (c, "compiled", Some(s), bytes.len())
            };
            let load_ms = ms(t.elapsed()) - serialize_ms.unwrap_or(0.0);

            let t = Instant::now();
            let pre = RecordValidatorPre::new(linker.instantiate_pre(&component)?)?;
            let instance_pre_ms = ms(t.elapsed());

            timings.per_package.push(PackageLoadTiming {
                package: manifest.id.clone(),
                source,
                compile_or_deserialize_ms: load_ms,
                serialize_ms,
                cwasm_bytes,
                wasm_bytes: wasm.len(),
                instance_pre_ms,
            });
            let caps = Arc::new(manifest.capabilities.clone());
            packages.insert(manifest.id.clone(), Arc::new(LoadedPackage { manifest, caps, pre }));
        }
        Ok((Runtime { engine, packages, tenant_inflight: Mutex::new(HashMap::new()) }, timings))
    }

    fn start_epoch_ticker(&self) {
        let engine = self.engine.clone();
        std::thread::Builder::new()
            .name("epoch-ticker".into())
            .spawn(move || loop {
                std::thread::sleep(EPOCH_TICK);
                engine.increment_epoch();
            })
            .expect("spawn epoch ticker");
    }

    fn invoke(&self, req: InvokeRequest) -> (u16, InvokeResponse) {
        let started = Instant::now();
        let Some(pkg) = self.packages.get(&req.package).cloned() else {
            return (404, InvokeResponse::failure("unknown_package", format!("package {} not installed", req.package)));
        };
        if !pkg.manifest.hooks.iter().any(|h| h == &req.hook) || req.hook != "validate" {
            return (400, InvokeResponse::failure("unknown_hook", format!("hook {} not exported by {}", req.hook, req.package)));
        }
        let _slot = match TenantSlot::acquire(&self.tenant_inflight, &req.tenant) {
            Some(s) => s,
            None => return (429, InvokeResponse::failure("tenant_concurrency_exceeded", "per-tenant concurrency quota reached".into())),
        };

        // Effective limits: min(server ceiling, package manifest, request).
        let rl = req.limits.clone().unwrap_or_default();
        let fuel = [Some(CEILINGS.fuel), pkg.manifest.limits.fuel, rl.fuel].into_iter().flatten().min().unwrap();
        let timeout_ms = [Some(CEILINGS.timeout_ms), pkg.manifest.limits.timeout_ms, rl.timeout_ms].into_iter().flatten().min().unwrap();
        let memory_bytes = [Some(CEILINGS.memory_bytes), pkg.manifest.limits.memory_bytes, rl.memory_bytes].into_iter().flatten().min().unwrap();

        let record = match &req.input.record {
            serde_json::Value::String(s) => s.clone(),
            other => other.to_string(),
        };
        let ctx = HookContext {
            tenant_id: req.tenant.clone(),
            environment: req.environment.clone(),
            package_id: req.package.clone(),
            hook: req.hook.clone(),
            correlation_id: req.input.correlation_id.clone().unwrap_or_default(),
            actor_id: req.input.actor_id.clone(),
        };

        // Fresh, empty WASI context: nothing granted.
        let stderr = MemoryOutputPipe::new(8 << 10);
        let wasi = WasiCtx::builder()
            .stderr(stderr.clone())
            .allow_tcp(false)
            .allow_udp(false)
            .allow_ip_name_lookup(false)
            .build();
        let state = InvocationState {
            wasi,
            table: ResourceTable::new(),
            limits: StoreLimitsBuilder::new()
                .memory_size(memory_bytes)
                .instances(16)
                .tables(8)
                .memories(2)
                .table_elements(10_000)
                .trap_on_grow_failure(true)
                .build(),
            caps: pkg.caps.clone(),
            // The platform sends only this tenant+environment's settings snapshot.
            settings: req.settings.clone().unwrap_or_default(),
            logs: Vec::new(),
            log_bytes: 0,
            denied: Vec::new(),
        };

        let mut store = Store::new(&self.engine, state);
        store.limiter(|s| &mut s.limits);
        store.set_fuel(fuel).expect("fuel enabled");
        store.set_epoch_deadline(timeout_ms.div_ceil(EPOCH_TICK.as_millis() as u64) + 1);
        store.epoch_deadline_trap();

        let t_inst = Instant::now();
        let result = pkg.pre.instantiate(&mut store).and_then(|bindings| {
            let instantiate_us = t_inst.elapsed().as_micros() as u64;
            let t_call = Instant::now();
            let r = bindings.esmbler_extension_validator().call_validate(&mut store, &ctx, &record);
            Ok((instantiate_us, t_call.elapsed().as_micros() as u64, r))
        });
        let fuel_consumed = fuel - store.get_fuel().unwrap_or(0);
        let state = store.into_data();
        let stderr = String::from_utf8_lossy(&stderr.contents()).into_owned();

        let mut resp = InvokeResponse {
            status: String::new(),
            violations: None,
            error: None,
            logs: state.logs,
            denied_capabilities: state.denied,
            stderr: if stderr.is_empty() { None } else { Some(stderr) },
            metrics: Metrics {
                instantiate_us: 0,
                call_us: 0,
                total_us: 0,
                fuel_consumed,
                limits: EffectiveLimits { fuel, timeout_ms, memory_bytes },
            },
        };
        let code = match result {
            Ok((inst, call, r)) => {
                resp.metrics.instantiate_us = inst;
                resp.metrics.call_us = call;
                match r {
                    Ok(Ok(v)) => {
                        resp.status = if v.is_empty() { "accepted".into() } else { "rejected".into() };
                        resp.violations = Some(v.into_iter().map(ViolationDto::from).collect());
                        200
                    }
                    Ok(Err(msg)) => {
                        resp.status = "extension_error".into();
                        resp.error = Some(msg);
                        200
                    }
                    Err(e) => {
                        let (status, msg) = classify_trap(&e);
                        resp.status = status.into();
                        resp.error = Some(msg);
                        200
                    }
                }
            }
            Err(e) => {
                let (status, msg) = classify_trap(&e);
                resp.status = status.into();
                resp.error = Some(format!("instantiation failed: {msg}"));
                200
            }
        };
        resp.metrics.total_us = started.elapsed().as_micros() as u64;
        (code, resp)
    }
}

fn classify_trap(e: &wasmtime::Error) -> (&'static str, String) {
    // Root cause only: the full wasm backtrace goes to host logs, not to the caller.
    let msg = e.root_cause().to_string();
    match e.downcast_ref::<Trap>() {
        Some(Trap::OutOfFuel) => ("cpu_limit_exceeded", msg),
        Some(Trap::Interrupt) => ("timeout", msg),
        _ if msg.contains("forcing trap when growing memory") || msg.contains("memory growth failure") => {
            ("memory_limit_exceeded", msg)
        }
        _ => ("trap", msg),
    }
}

/// RAII per-tenant concurrency slot.
struct TenantSlot<'a> {
    map: &'a Mutex<HashMap<String, usize>>,
    tenant: String,
}

impl<'a> TenantSlot<'a> {
    fn acquire(map: &'a Mutex<HashMap<String, usize>>, tenant: &str) -> Option<Self> {
        let mut m = map.lock().unwrap();
        let n = m.entry(tenant.to_string()).or_insert(0);
        if *n >= CEILINGS.per_tenant_concurrency {
            return None;
        }
        *n += 1;
        Some(TenantSlot { map, tenant: tenant.to_string() })
    }
}

impl Drop for TenantSlot<'_> {
    fn drop(&mut self) {
        let mut m = self.map.lock().unwrap();
        if let Some(n) = m.get_mut(&self.tenant) {
            *n -= 1;
            if *n == 0 {
                m.remove(&self.tenant);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Wire types
// ---------------------------------------------------------------------------

#[derive(Deserialize, Clone, Debug)]
struct InvokeRequest {
    tenant: String,
    environment: String,
    package: String,
    hook: String,
    input: InvokeInput,
    /// Tenant/environment-scoped settings snapshot supplied by the platform.
    #[serde(default)]
    settings: Option<HashMap<String, String>>,
    /// Optional per-call limits; can only lower the effective limits.
    #[serde(default)]
    limits: Option<PackageLimits>,
}

#[derive(Deserialize, Clone, Debug)]
struct InvokeInput {
    record: serde_json::Value,
    #[serde(default)]
    correlation_id: Option<String>,
    #[serde(default)]
    actor_id: Option<String>,
}

#[derive(Serialize, Debug)]
struct InvokeResponse {
    status: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    violations: Option<Vec<ViolationDto>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    logs: Vec<LogLine>,
    denied_capabilities: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    stderr: Option<String>,
    metrics: Metrics,
}

impl InvokeResponse {
    fn failure(status: &str, error: String) -> Self {
        InvokeResponse {
            status: status.into(),
            violations: None,
            error: Some(error),
            logs: vec![],
            denied_capabilities: vec![],
            stderr: None,
            metrics: Metrics::default(),
        }
    }
}

#[derive(Serialize, Debug, Default)]
struct Metrics {
    instantiate_us: u64,
    call_us: u64,
    total_us: u64,
    fuel_consumed: u64,
    limits: EffectiveLimits,
}

#[derive(Serialize, Debug, Default)]
struct EffectiveLimits {
    fuel: u64,
    timeout_ms: u64,
    memory_bytes: usize,
}

#[derive(Serialize, Debug)]
struct ViolationDto {
    field: Option<String>,
    code: String,
    message: String,
}

impl From<Violation> for ViolationDto {
    fn from(v: Violation) -> Self {
        ViolationDto { field: v.field, code: v.code, message: v.message }
    }
}

// ---------------------------------------------------------------------------
// HTTP server (axum on tokio; wasm runs on the blocking pool because the
// invocation path uses wasmtime's synchronous API)
// ---------------------------------------------------------------------------

fn serve(rt: Arc<Runtime>, listen: &str, workers: usize) -> Result<()> {
    use axum::{Json, Router, extract::{DefaultBodyLimit, State}, http::StatusCode, routing::{get, post}};
    let tokio_rt = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .max_blocking_threads(workers) // hard cap on concurrently executing guests
        .enable_all()
        .build()?;
    tokio_rt.block_on(async move {
        let app = Router::new()
            .route("/healthz", get(|| async { Json(serde_json::json!({"status":"ok"})) }))
            .route(
                "/packages",
                get(|State(rt): State<Arc<Runtime>>| async move {
                    Json(rt.packages.values().map(|p| p.manifest.clone()).collect::<Vec<_>>())
                }),
            )
            .route(
                "/invoke",
                post(|State(rt): State<Arc<Runtime>>, Json(ir): Json<InvokeRequest>| async move {
                    let (tenant, env, pkg) = (ir.tenant.clone(), ir.environment.clone(), ir.package.clone());
                    let rt2 = rt.clone();
                    let (code, resp) = tokio::task::spawn_blocking(move || rt2.invoke(ir))
                        .await
                        .unwrap_or_else(|e| (500, InvokeResponse::failure("host_panic", e.to_string())));
                    eprintln!(
                        "[ext-host] tenant={tenant} env={env} pkg={pkg} status={} total_us={} fuel={}",
                        resp.status, resp.metrics.total_us, resp.metrics.fuel_consumed
                    );
                    (StatusCode::from_u16(code).unwrap_or(StatusCode::INTERNAL_SERVER_ERROR), Json(resp))
                }),
            )
            .layer(DefaultBodyLimit::max(CEILINGS.max_request_bytes))
            .with_state(rt);
        let listener = tokio::net::TcpListener::bind(listen).await.with_context(|| format!("bind {listen}"))?;
        eprintln!("[ext-host] listening on http://{listen} (max {workers} concurrent guests)");
        axum::serve(listener, app).await?;
        Ok(())
    })
}

// ---------------------------------------------------------------------------
// Bench
// ---------------------------------------------------------------------------

fn bench(packages: &Path, cache: &Path, iterations: usize, pooling: bool) -> Result<()> {
    // Cold: compile from .wasm (cache cleared) ...
    let _ = std::fs::remove_dir_all(cache);
    let t = Instant::now();
    let (_rt_cold, cold) = Runtime::load(packages, cache, pooling)?;
    let cold_total = ms(t.elapsed());
    drop(_rt_cold);
    // ... then warm start from the serialized cache.
    let t = Instant::now();
    let (rt, warm) = Runtime::load(packages, cache, pooling)?;
    let warm_total = ms(t.elapsed());
    rt.start_epoch_ticker();

    println!("allocator: {}", if pooling { "pooling" } else { "on-demand" });
    println!("cold start (compile .wasm -> native): total {cold_total:.1} ms, {}", serde_json::to_string(&cold)?);
    println!("warm start (deserialize .cwasm):     total {warm_total:.1} ms, {}", serde_json::to_string(&warm)?);

    let mk = |record: serde_json::Value, tenant: &str| InvokeRequest {
        tenant: tenant.into(),
        environment: "prod".into(),
        package: "ticket-validator".into(),
        hook: "validate".into(),
        input: InvokeInput { record, correlation_id: Some("bench".into()), actor_id: None },
        settings: Some(HashMap::from([("ticket.impact.min-length".into(), "10".into())])),
        limits: None,
    };

    // Warm-up
    for _ in 0..50 {
        rt.invoke(mk(serde_json::json!({"priority": 1}), "t-warm"));
    }
    let mut inst = Vec::with_capacity(iterations);
    let mut call = Vec::with_capacity(iterations);
    let mut total = Vec::with_capacity(iterations);
    let t = Instant::now();
    for i in 0..iterations {
        let rec = if i % 2 == 0 {
            serde_json::json!({"priority": 1, "title": "Mail down"})
        } else {
            serde_json::json!({"priority": 1, "title": "Mail down", "impact": "All users in EU cannot send email"})
        };
        let (_, r) = rt.invoke(mk(rec, &format!("tenant-{}", i % 8)));
        assert!(r.status == "accepted" || r.status == "rejected", "unexpected {r:?}");
        inst.push(r.metrics.instantiate_us);
        call.push(r.metrics.call_us);
        total.push(r.metrics.total_us);
    }
    let wall = t.elapsed();
    println!(
        "warm invocations: n={iterations}, throughput (1 thread) {:.0}/s",
        iterations as f64 / wall.as_secs_f64()
    );
    for (name, v) in [("instantiate_us", &mut inst), ("call_us", &mut call), ("total_us", &mut total)] {
        v.sort_unstable();
        println!("  {name:15} p50={:>6} p95={:>6} p99={:>6} max={:>6}", pct(v, 50.0), pct(v, 95.0), pct(v, 99.0), v[v.len() - 1]);
    }

    // Limits: (a) spin with the manifest limits -> fuel runs out first;
    // (b) spin with a tighter wall-clock budget -> epoch interruption fires first;
    // (c) unbounded allocation -> StoreLimits refuses memory.grow and traps.
    let tight = PackageLimits { fuel: None, timeout_ms: Some(10), memory_bytes: None };
    for (label, rec, limits) in [
        ("spin, manifest limits", serde_json::json!({"_spike":"spin"}), None),
        ("spin, timeout_ms=10", serde_json::json!({"_spike":"spin"}), Some(tight)),
        ("oom", serde_json::json!({"_spike":"oom"}), None),
        ("secret (denied cap)", serde_json::json!({"_spike":"secret"}), None),
    ] {
        let mut req = mk(rec, "t-chaos");
        req.limits = limits;
        let (_, r) = rt.invoke(req);
        println!(
            "{label}: status={} total_ms={:.1} fuel={} error={:?} denied={:?}",
            r.status,
            r.metrics.total_us as f64 / 1000.0,
            r.metrics.fuel_consumed,
            r.error.unwrap_or_default(),
            r.denied_capabilities
        );
    }
    Ok(())
}

fn pct(sorted: &[u64], p: f64) -> u64 {
    let idx = ((p / 100.0) * (sorted.len() as f64 - 1.0)).round() as usize;
    sorted[idx]
}

fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

fn arg(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1).cloned())
}

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let cmd = args.get(1).map(String::as_str).unwrap_or("serve");
    let packages = PathBuf::from(arg(&args, "--packages").unwrap_or_else(|| "packages".into()));
    let cache = PathBuf::from(arg(&args, "--cache").unwrap_or_else(|| std::env::temp_dir().join("esmbler-ext-cache").display().to_string()));
    let pooling = args.iter().any(|a| a == "--pooling");
    match cmd {
        "serve" => {
            let listen = arg(&args, "--listen").unwrap_or_else(|| "127.0.0.1:8080".into());
            let workers: usize = arg(&args, "--workers").and_then(|s| s.parse().ok()).unwrap_or(8);
            let t = Instant::now();
            let (rt, timings) = Runtime::load(&packages, &cache, pooling)?;
            eprintln!("[ext-host] startup {:.1} ms {}", ms(t.elapsed()), serde_json::to_string(&timings)?);
            eprintln!("[ext-host] ceilings {}", serde_json::to_string(&CEILINGS)?);
            rt.start_epoch_ticker();
            serve(Arc::new(rt), &listen, workers)
        }
        "bench" => {
            let n: usize = arg(&args, "--iterations").and_then(|s| s.parse().ok()).unwrap_or(2000);
            bench(&packages, &cache, n, pooling)
        }
        "inspect" => {
            let file = args.get(2).context("usage: inspect FILE.wasm")?;
            let engine = build_engine(false)?;
            let c = Component::from_file(&engine, file)?;
            let ty = c.component_type();
            for (name, _) in ty.imports(&engine) {
                println!("import {name}");
            }
            for (name, _) in ty.exports(&engine) {
                println!("export {name}");
            }
            Ok(())
        }
        other => bail!("unknown command {other}"),
    }
}
