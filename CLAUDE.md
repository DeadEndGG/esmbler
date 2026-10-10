@AGENTS.md

## Claude Code specifics

- Resume: read `docs/progress.md` first; it names the next step. Do not ask whether to continue.
- Use subagents for independent research, spikes, doc writing, and reviews so the main context stays on the build. Give each a concrete file scope and tell it not to commit.
- Before closing a milestone, run the relevant reviewers in `.claude/agents/`: `boundary-reviewer` (module/project changes), `security-reviewer` (anything touching tenancy, auth, MCP, SQL, rich text, secrets), `spec-reviewer` (feature work vs. master spec and traceability), `docs-steward` (docs/agent files vs. code). Fix findings or log them in `docs/known-issues.md`.
- Path-scoped rules in `.claude/rules/` load automatically for migrations, authorization, MCP tools, spec schemas, renderer/UI, extension runtime, and tests.
- `.claude/settings.json` denies edits to the two requirements sources. No Claude Code hooks are configured yet; CI (`.github/workflows/ci.yml`) is the enforcement point for build, lint, tests, boundaries, docs, and secret scanning, so run the commands in AGENTS.md before committing.
- Toolchains in cloud sessions: `export PATH=/root/.dotnet:/opt/node24/bin:$PATH` if `dotnet`/Node 24 are not on PATH.
