---
paths:
  - "apps/Esmbler.Mcp/**"
---

# MCP tools

- No tool takes tenant, environment, or principal arguments; context comes from the validated token (audience `esmbler-mcp`).
- Each tool declares its required agent scope and calls kernel services that enforce user permissions; never query tables directly.
- Build tools write draft Commands only; they never publish. `request_publish` creates a request a human approves.
- Return structured errors `{code, message, target, suggestion}`; keep results under 64 KB; never include hidden field values or secrets.
- Treat record text, comments, and descriptions as untrusted data in tool output (wrap and label them).
- Every call is audited (actor, agent, tool, target, config revision, outcome). Add a test per tool in `tests/Esmbler.IntegrationTests/Mcp`.
