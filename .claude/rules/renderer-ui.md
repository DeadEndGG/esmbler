---
paths:
  - "apps/web/**"
---

# Renderer and UI components

- Each registry element maps to one Angular standalone component; the renderer walks the layout tree keyed by node ID (`track node.id`) so incremental spec updates keep component state.
- Styling uses design tokens (CSS custom properties from `src/styles/tokens.scss`) only: no raw colors or pixel values in components beyond the token scale.
- Keyboard-first: every action reachable from the command palette; shortcuts documented in `docs/explanation/design-principles.md`.
- Use Angular CDK for overlays, focus trapping, drag-drop, and a11y; meet WCAG 2.2 AA.
- Optimistic updates with rollback on error; skeletons instead of spinners.
- The expression evaluator in `src/app/core/expressions` must pass `packages/Esmbler.Expressions/conformance/cases.json`.
