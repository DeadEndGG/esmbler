import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Api } from '../core/api';
import { NodeSpec, SlaTimer } from '../core/models';
import { Icon, IconName } from '../ui/icon';
import { RenderContext } from './render-context';

/** Formats a number of seconds as "2 h 05 min", "3 d 4 h" or "12 min". */
export function formatDuration(seconds: number): string {
  const s = Math.abs(Math.round(seconds));
  const minutes = Math.floor(s / 60) % 60;
  const hours = Math.floor(s / 3600) % 24;
  const days = Math.floor(s / 86400);
  if (days > 0) return hours > 0 ? `${days} d ${hours} h` : `${days} d`;
  if (hours > 0) return `${hours} h ${String(minutes).padStart(2, '0')} min`;
  return `${Math.max(minutes, s > 0 ? 1 : 0)} min`;
}

type Tone = 'tone-green' | 'tone-amber' | 'tone-red' | 'tone-gray' | 'tone-blue';

/**
 * activity.sla (ADR 0018): the record's SLA targets, how much time is left or how late they are, whether the clock is
 * paused, and the outcome of stopped targets. Times are measured in business hours where the goal says so; the panel
 * names the calendar. Values come from the server; the panel never computes a deadline itself.
 */
@Component({
  selector: 'esm-sla-panel',
  imports: [DatePipe, Icon],
  template: `
    @if (timers().length > 0) {
      <section class="panel" [attr.aria-label]="title()">
        <header><span class="title"><esm-icon name="clock" [size]="15" /> {{ title() }}</span></header>
        <ul class="timers">
          @for (t of timers(); track t.id) {
            <li class="timer" [attr.data-state]="state(t)">
              <esm-icon [name]="icon(t)" [size]="16" class="icon" />
              <div class="body">
                <div class="head">
                  <strong>{{ t.label }}</strong>
                  <span class="badge" [class]="tone(t)">{{ badge(t) }}</span>
                </div>
                <p class="line">{{ sentence(t) }}</p>
                @if (t.status === 'running' || t.status === 'paused') {
                  <div class="bar" role="meter" [attr.aria-label]="t.label + ' time used'" aria-valuemin="0" aria-valuemax="100" [attr.aria-valuenow]="used(t)">
                    <span [style.width.%]="used(t)"></span>
                  </div>
                }
                <p class="meta">
                  Goal {{ duration(t.goalMinutes * 60) }} {{ t.calendar ? 'in ' + t.calendar : 'around the clock' }}
                  @if (t.dueAt) { · due {{ t.dueAt | date: 'MMM d, HH:mm' }} }
                  @if (t.cycle > 0) { · restarted }
                </p>
              </div>
            </li>
          }
        </ul>
      </section>
    } @else if (preview()) {
      <section class="panel"><header><span class="title"><esm-icon name="clock" [size]="15" /> {{ title() }}</span></header><p class="muted">Response and resolution targets appear here once the record exists.</p></section>
    }
  `,
  styles: `
    .panel { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); margin-bottom: var(--esm-space-4); }
    header { display: flex; align-items: center; justify-content: space-between; margin-bottom: var(--esm-space-3); }
    .title { display: inline-flex; align-items: center; gap: 6px; font: var(--esm-text-title-sm); }
    .timers { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-3); }
    .timer { display: flex; gap: var(--esm-space-2); }
    .icon { flex: none; margin-top: 2px; color: var(--esm-text-muted); }
    .timer[data-state="ok"] .icon, .timer[data-state="met"] .icon { color: var(--esm-accent-green); }
    .timer[data-state="risk"] .icon { color: var(--esm-accent-amber); }
    .timer[data-state="late"] .icon { color: var(--esm-accent-red); }
    .body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; font: var(--esm-text-body-sm); }
    .head { display: flex; align-items: center; justify-content: space-between; gap: var(--esm-space-2); }
    .line { margin: 0; }
    .meta { margin: 0; font: var(--esm-text-caption); color: var(--esm-text-muted); }
    .bar { height: 6px; border-radius: var(--esm-radius-full); background: var(--esm-surface-sunken); overflow: hidden; }
    .bar span { display: block; height: 100%; border-radius: inherit; background: var(--esm-accent-green); }
    .timer[data-state="risk"] .bar span { background: var(--esm-accent-amber); }
    .timer[data-state="late"] .bar span { background: var(--esm-accent-red); }
    .timer[data-state="paused"] .bar span { background: var(--esm-border-strong); }
  `,
})
export class SlaPanel {
  readonly node = input.required<NodeSpec>();
  private readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  protected readonly timers = signal<SlaTimer[]>([]);
  protected readonly title = computed(() => (this.node().props['title'] as string | undefined) ?? 'Service levels');
  protected readonly preview = computed(() => this.ctx.mode() === 'draft' || !this.ctx.record()?.id || this.ctx.record()?.id === '00000000-0000-0000-0000-000000000000');
  private readonly key = computed(() => (this.preview() ? null : `${this.ctx.record()?.entity}/${this.ctx.record()?.id}/${this.ctx.record()?.revision}`));

  constructor() {
    effect(() => {
      const key = this.key();
      untracked(() => void this.load(key));
    });
  }

  protected duration(seconds: number): string {
    return formatDuration(seconds);
  }

  protected state(t: SlaTimer): 'ok' | 'risk' | 'late' | 'paused' | 'met' | 'missed' {
    if (t.status === 'stopped') return t.outcome === 'breached' ? 'missed' : 'met';
    if (t.breachedAt || t.remainingSeconds < 0) return 'late';
    if (t.status === 'paused') return 'paused';
    return t.atRisk ? 'risk' : 'ok';
  }

  protected icon(t: SlaTimer): IconName {
    switch (this.state(t)) {
      case 'met': return 'circle-check';
      case 'missed': case 'late': return 'circle-alert';
      case 'paused': return 'circle-dashed';
      case 'risk': return 'warning';
      default: return 'clock';
    }
  }

  protected tone(t: SlaTimer): Tone {
    return ({ ok: 'tone-blue', risk: 'tone-amber', late: 'tone-red', paused: 'tone-gray', met: 'tone-green', missed: 'tone-red' } as const)[this.state(t)];
  }

  protected badge(t: SlaTimer): string {
    return ({ ok: 'On track', risk: 'At risk', late: 'Breached', paused: 'Paused', met: 'Met', missed: 'Missed' } as const)[this.state(t)];
  }

  protected sentence(t: SlaTimer): string {
    const left = formatDuration(t.remainingSeconds);
    switch (this.state(t)) {
      case 'met': return `Met after ${formatDuration(t.elapsedSeconds)}.`;
      case 'missed': return `Missed: took ${formatDuration(t.elapsedSeconds)}.`;
      case 'late': return `${left} past the goal${t.status === 'paused' ? ' (clock paused)' : ''}.`;
      case 'paused': return `Clock paused with ${left} left.`;
      default: return `${left} left.`;
    }
  }

  protected used(t: SlaTimer): number {
    const goal = t.goalMinutes * 60;
    return goal <= 0 ? 100 : Math.min(100, Math.max(0, Math.round((t.elapsedSeconds / goal) * 100)));
  }

  private async load(key: string | null): Promise<void> {
    if (!key) {
      this.timers.set([]);
      return;
    }
    try {
      const list = await this.api.get<SlaTimer[]>(`/api/records/${key.split('/').slice(0, 2).join('/')}/sla`);
      if (key === this.key()) this.timers.set(list);
    } catch {
      this.timers.set([]);
    }
  }
}
