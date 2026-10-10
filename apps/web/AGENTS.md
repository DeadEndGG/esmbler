# apps/web

The Angular 22 web client: shell, list and record pages, the registry-driven renderer, the AI builder panel and admin pages. Talks only to `apps/Esmbler.Api` (`/api`, `/hubs/live`) through the nginx proxy in Compose or `proxy.conf.json` in `ng serve`.

- Styling: Tailwind CSS v4 mapped onto the design tokens ([ADR 0014](../../docs/adr/0014-tailwind-css-for-the-web-ui.md)). Raw colors live only in `src/styles/tokens.css`; `src/styles.css` holds the Tailwind theme and shared primitives (`.btn`, `.input`, `.badge`, `.callout`, `.table-card`). Rules: `.claude/rules/renderer-ui.md`.
- Approvals (ADR 0016): `/approvals` (`src/app/pages/approvals.page.ts`: inbox, decided-by-you tab, out-of-office delegation) is shown only when the bootstrap's `approvals` flag is set, with a pending-count badge (`Session.pendingApprovals`, `/api/approvals/count`) and a command palette entry; the `activity.approvals` element (`src/app/renderer/approvals.ts`) shows stages and decisions on the record page and offers Approve/Reject only where the server says `canDecide`. The server is authoritative for every decision.
- Icons only through `esm-icon` (`src/app/ui/icon.ts`, Lucide).
- The production build must not need inline scripts (nginx CSP `script-src 'self'`); keep `inlineCritical` off.
- Hidden field values never reach the client; components render only what the API sends.
- The TypeScript expression evaluator (`src/app/core/expressions`) must pass `packages/Esmbler.Expressions/conformance/cases.json`.
- Tests: `npx ng build && npx ng test --watch=false`; acceptance and axe (light and dark) in `tests/e2e` against the Compose stack.
