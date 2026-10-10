//! Example Esmbler extension: a before-save validator for tickets.
//!
//! Rule: a ticket with priority == 1 must carry a non-empty `impact` field.
//! The minimum `impact` length is read from the tenant setting
//! `ticket.impact.min-length` (capability-gated host import).
//!
//! SPIKE-ONLY chaos switches (to prove the host's limits), selected by the
//! record field `_spike`:
//!   "spin" -> infinite loop (must be killed by fuel / epoch deadline)
//!   "oom"  -> allocate until memory grow fails (must be stopped by StoreLimits)
//!   "secret" -> try to read a setting the package is not granted

wit_bindgen::generate!({
    path: "../wit",
    world: "record-validator",
});

use esmbler::extension::host::{get_setting, log};
use esmbler::extension::types::{HookContext, LogLevel, Violation};
use exports::esmbler::extension::validator::Guest;

struct TicketValidator;

impl Guest for TicketValidator {
    fn validate(ctx: HookContext, record: String) -> Result<Vec<Violation>, String> {
        let value: serde_json::Value =
            serde_json::from_str(&record).map_err(|e| format!("record is not valid JSON: {e}"))?;
        let obj = value
            .as_object()
            .ok_or_else(|| "record must be a JSON object".to_string())?;

        match obj.get("_spike").and_then(|v| v.as_str()) {
            Some("spin") => spin(),
            Some("oom") => return Err(format!("unreachable: allocated {} bytes", hog())),
            Some("secret") => {
                let leaked = get_setting("platform.db.connection-string");
                return Err(format!("denied setting returned: {leaked:?}"));
            }
            _ => {}
        }

        let mut violations = Vec::new();
        let priority = obj.get("priority").and_then(|v| v.as_i64());
        if priority == Some(1) {
            let min_len: usize = get_setting("ticket.impact.min-length")
                .and_then(|s| s.parse().ok())
                .unwrap_or(1);
            let impact = obj.get("impact").and_then(|v| v.as_str()).unwrap_or("").trim();
            if impact.len() < min_len {
                violations.push(Violation {
                    field: Some("impact".into()),
                    code: "ticket.impact.required".into(),
                    message: format!(
                        "Priority 1 tickets require an impact statement of at least {min_len} characters."
                    ),
                });
            }
        }

        log(
            LogLevel::Info,
            &format!(
                "validated {} for tenant {} ({} violations)",
                ctx.hook,
                ctx.tenant_id,
                violations.len()
            ),
        );
        Ok(violations)
    }
}

#[inline(never)]
fn spin() -> ! {
    let mut x: u64 = 0;
    loop {
        x = std::hint::black_box(x.wrapping_add(1));
    }
}

#[inline(never)]
fn hog() -> usize {
    // Grows linear memory 1 MiB at a time; never returns normally because the
    // host refuses memory.grow beyond the StoreLimits cap (trap_on_grow_failure).
    let mut chunks: Vec<Vec<u8>> = Vec::new();
    loop {
        let mut v = vec![0u8; 1 << 20];
        v[0] = 1; // touch
        chunks.push(std::hint::black_box(v));
        if chunks.len() == usize::MAX {
            return chunks.len();
        }
    }
}

export!(TicketValidator);
