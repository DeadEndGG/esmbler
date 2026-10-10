---
paths:
  - "apps/web/**"
---

# Renderer and UI components

- Each registry element maps to one Angular standalone component; the renderer walks the layout tree keyed by node ID (`track node.id`) so incremental spec updates keep component state.
- Styling uses design tokens only (`src/styles/tokens.css`, the one file with raw colors). Tailwind utilities (ADR 0014) are mapped onto the tokens in `src/styles.css` (`bg-raised`, `text-muted`, `border-subtle`, `text-title-lg`, …); no raw colors, no palette classes, and arbitrary values only for layout sizes (widths, grid tracks, z-index, breakpoints such as `max-[900px]:`). Shared primitives (`.btn`, `.input`, `.badge`, `.callout`, `.table-card`) live in `@layer components`.
- Icons only through `esm-icon` (`src/app/ui/icon.ts`, Lucide); add a name to its approved set before use. Icons are `aria-hidden`; the control carries the name.
- Keep class names the tests use (`section.card`, `.field`, `.msg.assistant`, `.steps li`, `.chip`, `tr.cursor`).
- Keyboard-first: every action reachable from the command palette; shortcuts documented in `docs/explanation/design-principles.md`.
- Use Angular CDK for overlays, focus trapping, drag-drop, and a11y; meet WCAG 2.2 AA.
- Optimistic updates with rollback on error; skeletons instead of spinners.
- The expression evaluator in `src/app/core/expressions` must pass `packages/Esmbler.Expressions/conformance/cases.json`.
