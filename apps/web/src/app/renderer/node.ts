import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList } from '@angular/cdk/drag-drop';
import { Component, computed, inject, input } from '@angular/core';
import { BuilderState } from '../builder/builder-state';
import { DraftCommands } from '../builder/draft-commands';
import { NodeSpec } from '../core/models';
import { ActivityFeed, AuditHistory } from './activity';
import { ActionButton, ActionMenu, TransitionButtons } from './actions';
import { ApprovalsPanel } from './approvals';
import { SlaPanel } from './sla';
import { FieldPlacement } from './field';
import { BarChart, KpiTile, MetricTable } from './metrics';
import { RelatedList } from './related-list';
import { RenderContext } from './render-context';
import { visibleWhen } from './rules';
import { Icon, IconName } from '../ui/icon';

const BLOCK_TYPES = new Set(['layout.card', 'layout.tab_group', 'field', 'display.heading', 'display.label_value', 'display.badge', 'display.callout', 'display.empty_state', 'collection.related_list', 'activity.feed', 'activity.audit_history', 'action.transition_buttons', 'action.button', 'action.action_menu', 'display.kpi_tile', 'display.bar_chart', 'display.metric_table']);

/**
 * Renders one layout node and its children (Composite pattern). Children are tracked by stable node ID, so a new
 * spec from a draft change patches the DOM instead of rebuilding it. In draft mode every block gets a drag handle,
 * selection for point-and-prompt, and a highlight when a step touched it.
 */
@Component({
  selector: 'esm-node',
  imports: [Icon, FieldPlacement, ActivityFeed, AuditHistory, ApprovalsPanel, SlaPanel, TransitionButtons, ActionButton, ActionMenu, RelatedList, KpiTile, BarChart, MetricTable, CdkDropList, CdkDrag, CdkDragHandle],
  templateUrl: './node.html',
  styleUrl: './node.scss',
})
export class NodeComponent {
  readonly node = input.required<NodeSpec>();
  readonly viewId = input<string>('');
  protected readonly ctx = inject(RenderContext);
  protected readonly builder = inject(BuilderState);
  private readonly commands = inject(DraftCommands);

  protected readonly draft = computed(() => this.ctx.mode() === 'draft');
  protected readonly isBlock = computed(() => BLOCK_TYPES.has(this.node().type));
  protected readonly selected = computed(() => this.builder.selection().some((s) => s.nodeId === this.node().id));
  protected readonly flashing = computed(() => this.builder.flash().has(this.node().id) || (this.node().props['field'] && this.builder.flash().has(this.node().props['field'])));
  protected readonly changed = computed(() => this.ctx.isChanged(this.node().id));
  protected readonly isNew = computed(() => this.node().id.startsWith('tenant__'));
  protected readonly label = computed(() => {
    const p = this.node().props;
    return p['title'] ?? (p['field'] ? this.ctx.field(p['field'])?.label ?? p['field'] : undefined) ?? p['text'] ?? this.node().type.split('.').pop();
  });
  protected readonly visible = computed(() => {
    const n = this.node();
    if (n.type === 'display.callout') return visibleWhen(n.props['visibleWhen'], this.ctx.values());
    if (n.type === 'field') return !this.ctx.isHidden(n.props['field']);
    return true;
  });
  /** Containers whose children can be reordered by dragging in draft mode. */
  protected readonly dropContainer = computed(() => this.draft() && ['layout.column', 'layout.card', 'layout.tab'].includes(this.node().type));

  protected toneOf(tone: string | undefined): string {
    return ({ info: 'blue', success: 'green', warning: 'amber', critical: 'red' } as Record<string, string>)[tone ?? ''] ?? 'gray';
  }

  protected calloutIcon(tone: string | undefined): IconName {
    return ({ success: 'circle-check', warning: 'warning', critical: 'circle-alert' } as Record<string, IconName>)[tone ?? ''] ?? 'info';
  }

  protected spanClass(child: NodeSpec): string {
    return `span-${child.props['span'] ?? 12}`;
  }

  protected select(e: MouseEvent): void {
    if (!this.draft() || !this.isBlock()) return;
    e.stopPropagation();
    this.builder.toggleSelect({ nodeId: this.node().id, type: this.node().type, label: String(this.label()), view: this.viewId() }, e.shiftKey || e.metaKey || e.ctrlKey);
  }

  protected onKey(e: KeyboardEvent): void {
    if (!this.draft() || !this.isBlock()) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      this.builder.toggleSelect({ nodeId: this.node().id, type: this.node().type, label: String(this.label()), view: this.viewId() }, e.shiftKey);
    } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
      e.preventDefault();
      void this.commands.nudge(this.viewId(), this.node().id, e.key === 'ArrowUp' ? -1 : 1);
    }
  }

  protected drop(event: CdkDragDrop<NodeSpec>): void {
    const target = event.container.data;
    const moved = event.item.data as NodeSpec;
    const siblings = target.children.filter((c) => c.id !== moved.id);
    const after = event.currentIndex > 0 ? siblings[event.currentIndex - 1]?.id : undefined;
    void this.commands.move(this.viewId(), moved.id, target.id, after, event.currentIndex === 0 ? siblings[0]?.id : undefined);
  }

  protected activeTab(group: NodeSpec): string {
    return this.tabState.get(group.id) ?? group.props['defaultTab'] ?? group.children[0]?.id;
  }

  protected setTab(group: NodeSpec, tab: string): void {
    this.tabState.set(group.id, tab);
  }

  private readonly tabState = new Map<string, string>();

  protected tabKey(e: KeyboardEvent, group: NodeSpec, index: number): void {
    const tabs = group.children;
    const next = e.key === 'ArrowRight' ? (index + 1) % tabs.length : e.key === 'ArrowLeft' ? (index - 1 + tabs.length) % tabs.length : -1;
    if (next >= 0) {
      this.setTab(group, tabs[next].id);
      (e.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLElement>('[role=tab]')[next]?.focus();
    }
  }
}
