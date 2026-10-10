---
title: "ADR 0014: Tailwind CSS for the web UI"
status: accepted
owner: design-system
last_reviewed: 2026-10-10
requirements: [REQ-DS-001, REQ-DS-002, REQ-DS-003, REQ-LAY-002]
---

# ADR 0014: Tailwind CSS for the web UI

## Context and problem statement

The Angular app (`apps/web`) was styled with one global SCSS file plus per-component style blocks over the design tokens. Two problems surfaced at the start of Gate 2:

- **The deployed app rendered mostly unstyled.** Angular's production build inlined critical CSS and loaded the full stylesheet with `media="print"` plus an inline `onload` handler that swaps it to `all`. The nginx Content Security Policy (`script-src 'self'`) blocks that handler, so buttons, inputs, badges, callouts, links and even `.sr-only` never applied outside `ng serve`.
- **Building screens was slow and inconsistent.** Every component re-declared layout, spacing and table styles, and the product owner asked for a more polished, "beautiful" interface built with Tailwind.

How should the web UI be styled so that screens are quick to build, look consistent, and still use only the semantic design tokens ([design principles](../explanation/design-principles.md))?

## Decision drivers

- Semantic tokens stay the only color, type, radius and shadow vocabulary (design lint `DL-001` to `DL-005`), with light and dark parity.
- No runtime CSS-in-JS and nothing that needs `unsafe-eval` or inline scripts (CSP).
- Works with the Angular application builder and emulated view encapsulation.
- Small CSS output; no per-tenant build.

## Considered options

1. **Tailwind CSS v4 through PostCSS, with a theme mapped onto the semantic tokens and the default palette removed.**
2. Keep hand-written SCSS and add more global utility classes.
3. A component library (Angular Material or PrimeNG) themed to the tokens.

## Decision outcome

**Chosen option 1.** Status: implemented.

- `apps/web/.postcssrc.json` enables `@tailwindcss/postcss` (4.3.x); the Angular builder picks it up for global and component styles.
- `apps/web/src/styles.css` imports Tailwind, the self-hosted Inter variable font (`@fontsource-variable/inter`, so no third-party font request) and `src/styles/tokens.css`. Its `@theme inline` block resets `--color-*`, `--radius-*`, `--shadow-*` and `--text-*` and maps them to tokens, so utilities read `bg-raised`, `text-muted`, `border-subtle`, `text-brand`, `bg-red`, `rounded-lg`, `shadow-card`, `text-title-lg` and `text-body-sm`. A raw palette class such as `bg-indigo-500` does not exist.
- `src/styles/tokens.css` is the only file with raw color values. It defines every token for light and dark (system preference, or `data-theme`), plus `prefers-reduced-motion`.
- Shared primitives (`.btn`, `.input`, `.badge`, `.callout`, `.surface`, `.avatar`, `.brand-mark`, `.admin-page`, `.page-head`, `.table-card`, `.skeleton`) live in `@layer components`, so utilities can override them.
- Component style blocks remain for state-heavy, registry-driven elements (renderer nodes and fields, chat panel, palette). Unlayered component styles beat Tailwind's layered utilities, so a template never sets the same property both ways.
- Icons come from Lucide (ISC license, `lucide` 1.48.0) through `esm-icon` (`src/app/ui/icon.ts`), which renders only icons listed in its approved set (`DL-010`). Icons are `aria-hidden`; the control carries the accessible name.
- The production build sets `optimization.styles.inlineCritical: false`, so the stylesheet is a plain `<link>` that the CSP allows. This fixes the unstyled deployment.

### Consequences

- Good: new screens compose from token utilities; the CSS bundle stays small (global styles about 36 KB raw, 7 KB compressed, including the Inter `@font-face` rules).
- Good: the token contract is enforced by construction for utilities: only semantic names exist.
- Good: the CSP stays strict; no inline scripts are needed to load styles.
- Bad: two styling mechanisms coexist (utilities and component blocks). The rule is "utilities for layout and one-off spacing; component blocks for stateful elements".
- Bad: arbitrary values (`max-w-[380px]`) can bypass the spacing scale. Stylelint design lint (KI-005) should flag them in templates.
- Neutral: critical-CSS inlining is off; first paint waits for one small stylesheet.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Tailwind v4 mapped to tokens | Fast composition, tiny output, token-only vocabulary, first-class Angular builder support | Utility-heavy templates; two mechanisms during migration |
| 2. More hand-written SCSS | No new dependency | Slow to build screens; duplication already visible across pages |
| 3. Component library | Ready-made widgets | Large bundle; fights the registry-driven renderer and our tokens; harder to keep "calm, precise" look |

## More information

- Tokens and rules: [design principles](../explanation/design-principles.md).
- Assumptions: ASM-039 (the request said "tailscale"), ASM-040 (approved icon set) in [assumptions](../decisions/assumptions.md).
- Verification: `tests/e2e/specs/a11y.spec.ts` runs axe in light and dark mode; `npx ng build` and `npx ng test` in CI.
