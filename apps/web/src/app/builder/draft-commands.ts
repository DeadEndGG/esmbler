import { Injectable, inject } from '@angular/core';
import { Api, ApiError } from '../core/api';
import { DraftState, NodeSpec } from '../core/models';
import { Toasts } from '../core/toast';
import { BuilderState } from './builder-state';

/**
 * Direct manipulation in draft mode (drag, keyboard nudge, slash insert) as typed overlay commands, the same
 * operations the AI sends over MCP. Every command carries the draft version; a conflict reloads the snapshot.
 */
@Injectable({ providedIn: 'root' })
export class DraftCommands {
  private readonly api = inject(Api);
  private readonly builder = inject(BuilderState);
  private readonly toasts = inject(Toasts);
  /** Current layout per view, registered by the page rendering it, so nudges can find siblings. */
  private readonly layouts = new Map<string, NodeSpec>();

  track(viewId: string, layout: NodeSpec): void {
    this.layouts.set(viewId, layout);
  }

  async apply(op: Record<string, unknown>): Promise<DraftState | null> {
    let draft = await this.builder.ensureDraft();
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const next = await this.api.post<DraftState>('/api/drafts/current/commands', { op, expectedVersion: draft.version });
        this.builder.draft.set(next);
        this.builder.revision.update((r) => r + 1);
        return next;
      } catch (e) {
        if (e instanceof ApiError && e.code === 'draft_conflict' && attempt === 0) {
          // Someone (often the AI) changed the draft since we loaded it: reload and retry once on the new version.
          await this.builder.refresh();
          draft = await this.builder.ensureDraft();
          continue;
        }
        this.toasts.show(e instanceof ApiError ? (e.problem.suggestion ? `${e.message} ${e.problem.suggestion}` : e.message) : 'The change could not be applied.', 'warning');
        await this.builder.refresh();
        return null;
      }
    }
    return null;
  }

  move(viewId: string, nodeId: string, parentId: string, after?: string, before?: string): Promise<DraftState | null> {
    const anchor = after ? { after } : before ? { before } : { position: 'start' };
    return this.apply({ op: 'node.move', view: viewId, node: nodeId, parent: parentId, anchor });
  }

  async nudge(viewId: string, nodeId: string, delta: -1 | 1): Promise<void> {
    const layout = this.layouts.get(viewId);
    const parent = layout ? findParent(layout, nodeId) : null;
    if (!parent) return;
    const index = parent.children.findIndex((c) => c.id === nodeId);
    const target = index + delta;
    if (target < 0 || target >= parent.children.length) return;
    const others = parent.children.filter((c) => c.id !== nodeId);
    if (target === 0) await this.move(viewId, nodeId, parent.id, undefined, others[0]?.id);
    else await this.move(viewId, nodeId, parent.id, others[target - 1].id);
  }

  insert(viewId: string, parentId: string, node: Omit<NodeSpec, 'children'> & { children?: NodeSpec[] }, after?: string): Promise<DraftState | null> {
    return this.apply({ op: 'node.insert', view: viewId, parent: parentId, node: { children: [], ...node }, anchor: after ? { after } : { position: 'end' } });
  }

  remove(viewId: string, nodeId: string): Promise<DraftState | null> {
    return this.apply({ op: 'node.remove', view: viewId, node: nodeId });
  }
}

export function findParent(root: NodeSpec, id: string): NodeSpec | null {
  for (const c of root.children) {
    if (c.id === id) return root;
    const found = findParent(c, id);
    if (found) return found;
  }
  return null;
}

export function findNode(root: NodeSpec, id: string): NodeSpec | null {
  if (root.id === id) return root;
  for (const c of root.children) {
    const found = findNode(c, id);
    if (found) return found;
  }
  return null;
}
