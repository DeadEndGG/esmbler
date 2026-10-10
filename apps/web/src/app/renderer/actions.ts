import { Component, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { NodeSpec } from '../core/models';
import { RenderContext } from './render-context';

/** action.transition_buttons: only transitions valid now that the viewer may fire (server-computed). */
@Component({
  selector: 'esm-transition-buttons',
  template: `
    <div class="transitions" role="group" aria-label="Workflow actions">
      @for (t of visible(); track t.id) {
        <button class="btn" [class.btn-primary]="t.tone === 'primary'" [class.btn-danger]="t.tone === 'danger'" [disabled]="busy() || ctx.mode() === 'draft'" (click)="run(t.id)">{{ t.label }}</button>
      }
      @if (overflow().length) {
        <div class="more">
          <button class="btn" (click)="menu.set(!menu())" [attr.aria-expanded]="menu()">More</button>
          @if (menu()) {
            <ul class="menu" role="menu">
              @for (t of overflow(); track t.id) { <li role="menuitem" tabindex="0" (click)="run(t.id)" (keydown.enter)="run(t.id)">{{ t.label }}</li> }
            </ul>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .transitions { display: flex; gap: var(--esm-space-2); flex-wrap: wrap; margin-top: var(--esm-space-3); }
    .more { position: relative; }
    .menu { position: absolute; right: 0; z-index: 10; margin: 4px 0 0; padding: var(--esm-space-1); list-style: none; min-width: 160px; background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); box-shadow: var(--esm-shadow-overlay); }
    .menu li { padding: 6px var(--esm-space-2); border-radius: var(--esm-radius-sm); cursor: pointer; }
    .menu li:hover { background: var(--esm-state-hover); }
  `,
})
export class TransitionButtons {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(RenderContext);
  protected readonly busy = signal(false);
  protected readonly menu = signal(false);
  private readonly all = computed(() => this.ctx.record()?.transitions ?? []);
  protected readonly visible = computed(() => this.all().slice(0, this.node().props['max'] ?? 3));
  protected readonly overflow = computed(() => this.all().slice(this.node().props['max'] ?? 3));

  protected async run(id: string): Promise<void> {
    this.menu.set(false);
    this.busy.set(true);
    try {
      await this.ctx.runTransition(id);
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'esm-action-button',
  template: `<button class="btn" [class.btn-primary]="node().props['tone'] === 'primary'" [class.btn-danger]="node().props['tone'] === 'danger'" (click)="run()">{{ node().props['label'] }}</button>`,
})
export class ActionButton {
  readonly node = input.required<NodeSpec>();
  private readonly ctx = inject(RenderContext);
  private readonly router = inject(Router);

  protected run(): void {
    void runAction(this.node().props['action'], this.ctx, this.router, this.node().props['confirm']);
  }
}

@Component({
  selector: 'esm-action-menu',
  template: `
    <div class="wrap">
      <button class="btn" (click)="open.set(!open())" [attr.aria-expanded]="open()">{{ node().props['label'] }} ▾</button>
      @if (open()) {
        <ul class="menu" role="menu">
          @for (item of node().props['items']; track item.label) { <li role="menuitem" tabindex="0" (click)="run(item)" (keydown.enter)="run(item)">{{ item.label }}</li> }
        </ul>
      }
    </div>
  `,
  styles: `
    .wrap { position: relative; display: inline-block; }
    .menu { position: absolute; z-index: 10; margin: 4px 0 0; padding: var(--esm-space-1); list-style: none; min-width: 180px; background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); box-shadow: var(--esm-shadow-overlay); }
    .menu li { padding: 6px var(--esm-space-2); border-radius: var(--esm-radius-sm); cursor: pointer; }
  `,
})
export class ActionMenu {
  readonly node = input.required<NodeSpec>();
  protected readonly open = signal(false);
  private readonly ctx = inject(RenderContext);
  private readonly router = inject(Router);

  protected run(item: { action: string; confirm?: { title: string; text: string } }): void {
    this.open.set(false);
    void runAction(item.action, this.ctx, this.router, item.confirm);
  }
}

async function runAction(action: string, ctx: RenderContext, router: Router, confirmSpec?: { title: string; text: string }): Promise<void> {
  if (ctx.mode() === 'draft') return;
  if (confirmSpec && !window.confirm(`${confirmSpec.title}\n\n${confirmSpec.text}`)) return;
  if (action.startsWith('transition:')) await ctx.runTransition(action.slice('transition:'.length));
  else if (action.startsWith('quick_create:')) await router.navigate(['/records', ctx.entity()?.entity.id, 'new']);
}
