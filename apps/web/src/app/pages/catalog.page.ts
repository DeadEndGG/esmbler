import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { CatalogItem, CatalogView } from '../core/models';
import { Icon, IconName } from '../ui/icon';

const ICONS: Record<string, IconName> = { laptop: 'laptop', shield: 'shield', users: 'users', package: 'package', building: 'building' };

/** The service catalog (REQ-BIZ-004, ADR 0015): requestable items as cards, grouped by category, searchable. */
@Component({
  selector: 'esm-catalog-page',
  imports: [RouterLink, Icon],
  template: `
    <div class="mx-auto max-w-[1160px] px-8 pt-8 pb-12 max-[700px]:px-4">
      <header class="hero">
        <p class="eyebrow"><esm-icon name="store" [size]="14" /> Service catalog</p>
        <h1 class="text-title-xl">How can we help?</h1>
        <p class="mt-1 max-w-[560px] text-body text-muted">Pick what you need. We'll ask a few questions and the right team takes it from there. You can follow every step on your request.</p>
        <div class="relative mt-5 w-full max-w-[420px]">
          <esm-icon name="search" [size]="16" class="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-muted" />
          <input class="input h-10 pl-9" type="search" placeholder="Search the catalog" aria-label="Search the catalog" [value]="query()" (input)="query.set($any($event.target).value)" />
        </div>
      </header>

      @if (categories().length > 1) {
        <nav class="mb-5 flex flex-wrap gap-2" aria-label="Categories">
          <button class="pill" [class.on]="!category()" [attr.aria-pressed]="!category()" (click)="category.set(null)">All</button>
          @for (c of categories(); track c) {
            <button class="pill" [class.on]="category() === c" [attr.aria-pressed]="category() === c" (click)="category.set(c)">{{ c }}</button>
          }
        </nav>
      }

      @if (error()) {
        <div class="callout tone-red" role="alert">{{ error() }}</div>
      } @else if (loading()) {
        <div class="grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-4" aria-busy="true">
          @for (i of [1, 2, 3]; track i) { <div class="item"><span class="skeleton block h-10 w-10"></span><span class="skeleton mt-4 block h-4 w-1/2"></span><span class="skeleton mt-2 block h-3 w-4/5"></span></div> }
        </div>
      } @else {
        <ul class="grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-4" aria-label="Catalog items">
          @for (item of filtered(); track item.id) {
            <li>
              <a class="item" [routerLink]="['/catalog', item.id]">
                <span class="item-icon" aria-hidden="true"><esm-icon [name]="icon(item)" [size]="20" /></span>
                <span class="mt-4 block text-title-sm text-fg">{{ item.label }}</span>
                @if (item.description) { <span class="mt-1 block text-body-sm text-muted">{{ item.description }}</span> }
                <span class="item-foot">
                  @if (item.category) { <span class="badge no-dot tone-gray">{{ item.category }}</span> }
                  <span class="flex-1"></span>
                  <span class="cta">Request <esm-icon name="arrow-up-right" [size]="14" /></span>
                </span>
              </a>
            </li>
          } @empty {
            <li class="col-span-full rounded-lg border border-dashed border-strong p-10 text-center">
              <strong class="text-title-sm">Nothing matches</strong>
              <p class="mt-1 text-body-sm text-muted">{{ items().length ? 'Try another search or category.' : 'There is nothing to request in this workspace yet.' }}</p>
            </li>
          }
        </ul>
      }
    </div>
  `,
  styles: `
    .hero { margin-bottom: var(--esm-space-6); padding: var(--esm-space-8); border-radius: var(--esm-radius-xl); border: 1px solid var(--esm-border-subtle);
      background: var(--esm-gradient-glow), var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); }
    .eyebrow { display: inline-flex; align-items: center; gap: 6px; margin-bottom: var(--esm-space-2); font: var(--esm-text-caption); letter-spacing: 0.06em; text-transform: uppercase; color: var(--esm-accent-brand); }
    .pill { height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); transition: all var(--esm-motion-fast) var(--esm-ease-out); }
    .pill:hover { color: var(--esm-text-default); border-color: var(--esm-border-input); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .item { display: flex; flex-direction: column; height: 100%; padding: var(--esm-space-5); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle);
      background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); color: inherit; text-decoration: none;
      transition: transform var(--esm-motion-base) var(--esm-ease-out), box-shadow var(--esm-motion-base) var(--esm-ease-out), border-color var(--esm-motion-base) var(--esm-ease-out); }
    a.item:hover { text-decoration: none; transform: translateY(-2px); box-shadow: var(--esm-shadow-raised); border-color: color-mix(in srgb, var(--esm-accent-brand) 35%, var(--esm-border-subtle)); }
    a.item:hover .cta { color: var(--esm-accent-brand); }
    .item-icon { display: grid; place-items: center; width: 40px; height: 40px; border-radius: var(--esm-radius-md); background: var(--esm-state-selected); color: var(--esm-accent-brand); }
    .item-foot { display: flex; align-items: center; gap: var(--esm-space-2); margin-top: auto; padding-top: var(--esm-space-4); }
    .cta { display: inline-flex; align-items: center; gap: 4px; font: var(--esm-text-body-sm); font-weight: 600; color: var(--esm-text-muted); transition: color var(--esm-motion-fast) var(--esm-ease-out); }
    @media (prefers-reduced-motion: reduce) { a.item:hover { transform: none; } }
  `,
})
export class CatalogPage {
  private readonly api = inject(Api);
  protected readonly items = signal<CatalogItem[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly query = signal('');
  protected readonly category = signal<string | null>(null);

  protected readonly categories = computed(() => [...new Set(this.items().map((i) => i.category).filter((c): c is string => !!c))]);
  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.items().filter(
      (i) => (!this.category() || i.category === this.category()) && (!q || i.label.toLowerCase().includes(q) || (i.description ?? '').toLowerCase().includes(q) || (i.category ?? '').toLowerCase().includes(q)),
    );
  });

  constructor() {
    void this.load();
  }

  protected icon(item: CatalogItem): IconName {
    return ICONS[item.icon ?? ''] ?? 'package';
  }

  private async load(): Promise<void> {
    try {
      const view = await this.api.get<CatalogView>('/api/catalog');
      this.items.set(view.items);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'The catalog could not be loaded.');
    } finally {
      this.loading.set(false);
    }
  }
}
