---
paths:
  - "apps/Esmbler.Mcp/**"
---

# MCP tools

- No tool takes tenant, environment, or principal arguments; context comes from the validated token (audience `esmbler-mcp`, or the canonical `Esmbler:Mcp:PublicUrl` when the MCP server is public, ADR 0025).
- Every `[McpServerTool]` declares `ReadOnly = true` or an explicit `Destructive = true|false` (architecture test `Every_mcp_tool_says_whether_it_only_reads_or_may_destroy`); the destructive set is exactly `discard_draft`, `remove_metric`, `remove_node`, `remove_rule` and `reset_to_default`, so update the test when a tool removes or resets something. External hosts ask for confirmation from these hints.
- Run every tool through `ToolRunner.RunAsync`: it checks the scope, an external connection's `cid` (`connection_revoked`) and the per-token or per-connection limits, and audits the call.
- Each tool declares its required agent scope and calls kernel services that enforce user permissions; never query tables directly.
- Build tools write draft Commands only; they never publish. `request_publish` creates a request a human approves.
- Return structured errors `{code, message, target, suggestion}`; keep results under 64 KB; never include hidden field values or secrets.
- Treat record text, comments, and descriptions as untrusted data in tool output (wrap and label them).
- Every call is audited (actor, agent, tool, target, config revision, outcome). Add a test per tool in `tests/Esmbler.IntegrationTests/Mcp`.
