import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Auth } from '../core/auth';
import { Session } from '../core/session';
import { Icon } from '../ui/icon';

interface StarterQuestion {
  id: string;
  label: string;
  help?: string | null;
  example?: string | null;
}

interface StarterSurface {
  kind: string;
  label: string;
  items: string[];
}

interface Starter {
  id: string;
  name: string;
  description: string;
  module: string;
  moduleName: string;
  version: string;
  keywords: string[];
  questions: StarterQuestion[];
  surfaces: StarterSurface[];
}

interface StartersResponse {
  enabled: boolean;
  starters: Starter[];
}

type Step = 'describe' | 'questions' | 'review';

/**
 * Guided workspace creation (REQ-WS-001): describe the goal, pick (or accept) the matching starter, answer only the
 * questions without a default, review everything the starter connects, and create it in one step. The server decides
 * what is created; this page only collects the answers.
 */
@Component({
  selector: 'esm-create-workspace',
  imports: [FormsModule, RouterLink, Icon],
  template: `
    <main class="backdrop-glow grid min-h-dvh place-items-center p-6">
      <section class="surface w-full max-w-[720px] p-6" aria-labelledby="t">
        <div class="mb-5 flex items-center gap-3">
          <span class="brand-mark" aria-hidden="true"><esm-icon name="layers" [size]="16" [stroke]="2.25" /></span>
          <div class="flex-1">
            <h1 id="t" class="text-title-md">Create a workspace</h1>
            <p class="text-body-sm text-muted">Start from a complete, connected workspace. You can change anything later.</p>
          </div>
          <ol class="steps flex gap-1.5 text-caption" aria-label="Progress">
            @for (s of stepList; track s.id; let i = $index) {
              <li [class.current]="step() === s.id" [attr.aria-current]="step() === s.id ? 'step' : null">{{ i + 1 }}. {{ s.label }}</li>
            }
          </ol>
        </div>

        @if (error()) {
          <div class="callout tone-red mb-4" role="alert">{{ error() }}</div>
        }

        @if (loading()) {
          <div class="grid gap-3" aria-busy="true"><span class="skeleton h-10"></span><span class="skeleton h-24"></span></div>
        } @else if (!enabled()) {
          <p class="callout tone-amber">Creating workspaces is turned off on this platform. Ask a platform operator to create one for you.</p>
        } @else {
          @switch (step()) {
            @case ('describe') {
              <form class="grid gap-4" (ngSubmit)="next()">
                <label class="grid gap-1.5">
                  <span class="text-body-sm font-medium">Workspace name</span>
                  <input class="input" name="name" [ngModel]="name()" (ngModelChange)="name.set($event)" maxlength="60" required placeholder="For example: Riverside IT" />
                </label>
                <label class="grid gap-1.5">
                  <span class="text-body-sm font-medium">What do you want to run? <span class="font-normal text-muted">(optional)</span></span>
                  <textarea class="input min-h-20" name="goal" [ngModel]="goal()" (ngModelChange)="goal.set($event)" maxlength="500"
                    placeholder="For example: IT ticketing with priorities, categories, assignment, an explorer and a dashboard of workload and SLA issues"></textarea>
                </label>
                <fieldset class="grid gap-2">
                  <legend class="mb-1.5 text-body-sm font-medium">Starter</legend>
                  @for (s of starters(); track s.id) {
                    <label class="starter flex cursor-pointer gap-3 rounded-md border p-3" [class.chosen]="starterId() === s.id">
                      <input type="radio" name="starter" [value]="s.id" [checked]="starterId() === s.id" (change)="starterId.set(s.id)" />
                      <span class="grid gap-0.5">
                        <span class="font-semibold">{{ s.name }} @if (suggested()?.id === s.id && goal()) { <span class="badge tone-blue">Matches your description</span> }</span>
                        <span class="text-body-sm text-muted">{{ s.description }}</span>
                      </span>
                    </label>
                  }
                </fieldset>
                <div class="flex justify-between gap-2">
                  <a class="btn btn-ghost" routerLink="/workspaces">Cancel</a>
                  <button class="btn btn-primary" type="submit" [disabled]="!canContinue()">Continue <esm-icon name="chevron-right" [size]="14" /></button>
                </div>
              </form>
            }
            @case ('questions') {
              <form class="grid gap-4" (ngSubmit)="next()">
                <p class="text-body-sm text-muted">Only what has no sensible default. Everything else starts ready to use.</p>
                @for (q of starter()?.questions ?? []; track q.id) {
                  <label class="grid gap-1.5">
                    <span class="text-body-sm font-medium">{{ q.label }}</span>
                    <input class="input" [name]="q.id" [ngModel]="answers()[q.id] ?? ''" (ngModelChange)="answer(q.id, $event)" maxlength="60" required [placeholder]="q.example ? 'For example: ' + q.example : ''" />
                    @if (q.help) { <span class="text-caption text-muted">{{ q.help }}</span> }
                  </label>
                } @empty {
                  <p>Nothing to ask: this starter has a default for everything.</p>
                }
                <div class="flex justify-between gap-2">
                  <button class="btn btn-ghost" type="button" (click)="step.set('describe')">Back</button>
                  <button class="btn btn-primary" type="submit" [disabled]="!answered()">Review <esm-icon name="chevron-right" [size]="14" /></button>
                </div>
              </form>
            }
            @case ('review') {
              <div class="grid gap-4">
                <p class="text-body-sm">
                  <strong>{{ name() }}</strong> starts from <strong>{{ starter()?.name }}</strong> ({{ starter()?.moduleName }} {{ starter()?.version }}). These pieces are created together and stay connected:
                </p>
                <dl class="review grid gap-3">
                  @for (s of starter()?.surfaces ?? []; track s.kind) {
                    <div class="rounded-md border border-subtle p-3">
                      <dt class="mb-1 text-body-sm font-semibold">{{ s.label }}</dt>
                      <dd class="m-0"><ul class="m-0 grid gap-0.5 pl-4 text-body-sm">@for (item of s.items; track item) { <li>{{ item }}</li> }</ul></dd>
                    </div>
                  }
                  @for (q of starter()?.questions ?? []; track q.id) {
                    <div class="rounded-md border border-subtle p-3">
                      <dt class="mb-1 text-body-sm font-semibold">{{ q.label }}</dt>
                      <dd class="m-0 text-body-sm">{{ answers()[q.id] }}. You join this team as the workspace administrator.</dd>
                    </div>
                  }
                </dl>
                <p class="text-caption text-muted">The workspace starts empty. Its dashboard shows clearly labeled preview figures until the first records arrive.</p>
                <div class="flex justify-between gap-2">
                  <button class="btn btn-ghost" type="button" (click)="step.set(starter()?.questions?.length ? 'questions' : 'describe')" [disabled]="creating()">Back</button>
                  <button class="btn btn-primary" type="button" (click)="create()" [disabled]="creating()">
                    @if (creating()) { Creating… } @else { <esm-icon name="check" [size]="14" /> Create workspace }
                  </button>
                </div>
              </div>
            }
          }
        }
      </section>
    </main>
  `,
  styles: `
    .steps li { padding: 2px 8px; border-radius: var(--esm-radius-full); color: var(--esm-text-muted); background: var(--esm-surface-sunken); white-space: nowrap; }
    .steps li.current { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); }
    .starter { border-color: var(--esm-border-subtle); }
    .starter.chosen { border-color: var(--esm-accent-brand); background: var(--esm-state-selected); }
    @media (max-width: 640px) { .steps { display: none; } }
  `,
})
export class CreateWorkspacePage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected readonly stepList: { id: Step; label: string }[] = [
    { id: 'describe', label: 'Describe' },
    { id: 'questions', label: 'Questions' },
    { id: 'review', label: 'Review' },
  ];
  protected readonly step = signal<Step>('describe');
  protected readonly loading = signal(true);
  protected readonly enabled = signal(true);
  protected readonly creating = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly starters = signal<Starter[]>([]);
  protected readonly name = signal('');
  protected readonly goal = signal('');
  protected readonly starterId = signal<string | null>(null);
  protected readonly answers = signal<Record<string, string>>({});

  protected readonly starter = computed(() => this.starters().find((s) => s.id === this.starterId()) ?? null);
  /** The starter whose keywords best match the description (a suggestion; the person chooses). */
  protected readonly suggested = computed(() => {
    const text = this.goal().toLowerCase();
    let best: Starter | null = null;
    let score = 0;
    for (const s of this.starters()) {
      const hits = s.keywords.filter((k) => text.includes(k)).length;
      if (hits > score) {
        best = s;
        score = hits;
      }
    }
    return best;
  });
  protected readonly canContinue = computed(() => this.name().trim().length >= 2 && !!this.starter());
  protected readonly answered = computed(() => (this.starter()?.questions ?? []).every((q) => !!this.answers()[q.id]?.trim()));

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      const res = await this.api.get<StartersResponse>('/api/starters');
      this.enabled.set(res.enabled);
      this.starters.set(res.starters);
      if (res.starters.length === 1) this.starterId.set(res.starters[0].id);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load the starters.');
    } finally {
      this.loading.set(false);
    }
  }

  protected answer(id: string, value: string): void {
    this.answers.update((a) => ({ ...a, [id]: value }));
  }

  protected next(): void {
    if (this.step() === 'describe') {
      if (!this.starterId() && this.suggested()) this.starterId.set(this.suggested()!.id);
      if (!this.canContinue()) return;
      this.step.set(this.starter()?.questions.length ? 'questions' : 'review');
    } else if (this.step() === 'questions' && this.answered()) {
      this.step.set('review');
    }
  }

  protected async create(): Promise<void> {
    this.creating.set(true);
    this.error.set(null);
    try {
      const created = await this.api.post<{ tenantId: string; slug: string; name: string }>('/api/workspaces', {
        name: this.name().trim(),
        starter: this.starterId(),
        answers: this.answers(),
      });
      await this.auth.switchTo(created.slug, 'prod');
      this.session.app.set(null);
      await this.router.navigateByUrl('/');
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message + (e.problem.suggestion ? ' ' + e.problem.suggestion : '') : 'Could not create the workspace.');
      this.creating.set(false);
    }
  }
}
