# Start Here

Everything you need to kick off the autonomous build of the AI-native ESM platform.

## What's in this folder

| File | Purpose |
| --- | --- |
| `docs/spec/master-spec.md` | The full product vision and requirements (your Revision 2, now Revision 3 with live editing, Linear/Notion design direction, and the C#/.NET, Angular, PostgreSQL stack) |
| `docs/spec/execution-brief.md` | How the agent builds it: autonomy rules, product defaults, stack conventions, gate order, agent files, documentation standards |
| `AGENTS.md`, `CLAUDE.md` | Bootstrap instructions so any session starts or resumes correctly. The agent replaces them in Gate 0 |
| `docs/progress.md` | Where the build stands. The agent keeps it updated |

## Before you start

1. Review section 2 of `docs/spec/execution-brief.md` (buyer, scale, hosting, stack). The agent treats it as approved.
2. Install Docker, the current .NET LTS SDK, Node.js (for Angular), Git, and Claude Code.
3. Run unattended work on an isolated machine, VM, or dev container. Choose a Claude Code permission mode that doesn't stop for every command only in that isolated environment.

## Kick off

1. Unzip this folder where you want the repository and open a terminal in it.
2. Run `git init` and commit these files.
3. Start Claude Code in the folder and paste:

```text
Read AGENTS.md, docs/spec/execution-brief.md, and docs/spec/master-spec.md. Then build the platform autonomously, following the execution brief, starting from where docs/progress.md says. Work through every gate without stopping to ask whether to continue.
```

## If a session ends

Sessions can end on context or usage limits. Start a new session in the same folder and paste:

```text
Resume the autonomous build. Read AGENTS.md and docs/progress.md and continue exactly where work stopped.
```

## While it runs

- `docs/progress.md` shows the current gate and milestone.
- `docs/human-actions.md` lists what needs you (cloud accounts, API keys, email domain, payment provider).
- `docs/known-issues.md` lists problems the agent couldn't solve yet.

## Testing phase by phase

Each finished gate is tagged `gate-N-complete` with a guide at `docs/testing/gate-N-test-guide.md`. Check out a tag, follow its guide, and test that phase. Trying Gate 1 early is worth it: problems in the foundation carry into every later gate.
