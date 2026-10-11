import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface AiModel {
  id: string;
  label: string;
  provider: string;
  model: string;
  available: boolean;
  evaluated: boolean;
  inputPerMillion: number;
  outputPerMillion: number;
}

interface AiSettings {
  modelId?: string | null;
  effective?: AiModel | null;
  effort?: string | null;
  dailyBudgetUsd: number;
  spentTodayUsd: number;
  allowRecordsRead: boolean;
  externalAssistants: 'off' | 'non_production' | 'all';
  models: AiModel[];
  version: number;
}

const PROVIDERS: Record<string, string> = { anthropic: 'Anthropic', openai: 'OpenAI', 'azure-openai': 'Azure OpenAI' };

/**
 * AI assistant settings (ADR 0024, REQ-AI-006): which model from the operator's catalog the workspace's builder uses,
 * its effort and daily budget, and whether external assistants may connect (ADR 0025). Permission administrators only;
 * keys are never shown, and the server decides what can be chosen.
 */
@Component({
  selector: 'esm-ai-page',
  imports: [FormsModule, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="sparkles" [size]="18" /></span>
        <div class="flex-1">
          <h1 class="text-title-lg">AI assistant</h1>
          <p class="section-lead">Choose the model the AI builder uses in this workspace and how much it may spend each day.</p>
        </div>
      </header>

      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }

      @if (settings(); as s) {
        <div class="grid grid-cols-[minmax(0,1fr)_340px] items-start gap-6 max-[960px]:grid-cols-1">
          <form class="card-item grid gap-4" (ngSubmit)="save()" aria-label="AI assistant settings">
            <label class="field-row">Model
              <select class="input" name="model" [(ngModel)]="modelId">
                <option [ngValue]="null">Platform default{{ s.effective && !s.modelId ? ' (' + s.effective.label + ')' : '' }}</option>
                @for (m of s.models; track m.id) {
                  <option [ngValue]="m.id" [disabled]="!m.available || !m.evaluated">{{ m.label }} · {{ provider(m.provider) }}{{ !m.available ? ' (not configured)' : !m.evaluated ? ' (not evaluated yet)' : '' }}</option>
                }
              </select>
            </label>
            <label class="field-row">Effort
              <select class="input" name="effort" [(ngModel)]="effort">
                <option [ngValue]="null">Model default</option>
                <option value="low">Low (fastest)</option>
                <option value="medium">Medium</option>
                <option value="high">High</option>
                <option value="max">Max (most thorough)</option>
              </select>
            </label>
            <label class="field-row">Daily budget (US dollars, 0 turns the assistant off)
              <input class="input w-40" type="number" name="budget" min="0" max="100000" step="0.5" [(ngModel)]="budget" />
            </label>
            <label class="field-row">External assistants (ChatGPT, Claude) may connect
              <select class="input" name="external" [(ngModel)]="external">
                <option value="off">Off</option>
                <option value="non_production">Only to non-production environments</option>
                <option value="all">To all environments</option>
              </select>
            </label>
            <label class="flex items-center gap-2 text-body-sm"><input type="checkbox" name="records" [(ngModel)]="allowRecordsRead" /> Assistants may be given permission to read records (off keeps record text away from AI providers)</label>
            <div class="flex justify-end"><button class="btn btn-primary" type="submit">Save</button></div>
          </form>
          <aside class="card-item grid gap-2" aria-label="Today">
            <h2 class="text-title-sm">Today</h2>
            <p class="text-body-sm">Spent <strong>{{ money(s.spentTodayUsd) }}</strong> of {{ money(s.dailyBudgetUsd) }} (estimated, resets at midnight UTC).</p>
            @if (s.effective) {
              <p class="text-body-sm text-muted">Now using {{ s.effective.label }} ({{ provider(s.effective.provider) }}), {{ money(s.effective.inputPerMillion) }} per million input tokens and {{ money(s.effective.outputPerMillion) }} per million output tokens.</p>
            } @else {
              <p class="text-body-sm text-muted">No model is configured on this platform: the builder uses its built-in rules.</p>
            }
            <p class="text-caption text-muted">Keys are set up by the platform operator and are never shown here.</p>
          </aside>
        </div>
      } @else {
        <div class="card-item" aria-busy="true"><span class="skeleton block h-4 w-1/2"></span></div>
      }
    </div>
  `,
  styles: `
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    .field-row { display: grid; gap: 4px; font: var(--esm-text-body-sm); font-weight: 500; }
  `,
})
export class AiPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly settings = signal<AiSettings | null>(null);
  protected readonly error = signal<string | null>(null);
  protected modelId: string | null = null;
  protected effort: string | null = null;
  protected budget = 20;
  protected external: AiSettings['externalAssistants'] = 'off';
  protected allowRecordsRead = false;

  constructor() {
    void this.load();
  }

  protected provider(p: string): string {
    return PROVIDERS[p] ?? p;
  }

  protected money(v: number): string {
    return `$${v.toFixed(v < 1 ? 4 : 2)}`;
  }

  private apply(s: AiSettings): void {
    this.settings.set(s);
    this.modelId = s.modelId ?? null;
    this.effort = s.effort ?? null;
    this.budget = s.dailyBudgetUsd;
    this.external = s.externalAssistants;
    this.allowRecordsRead = s.allowRecordsRead;
  }

  private async load(): Promise<void> {
    try {
      this.apply(await this.api.get<AiSettings>('/api/admin/ai'));
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load the AI settings.');
    }
  }

  protected async save(): Promise<void> {
    try {
      this.apply(await this.api.put<AiSettings>('/api/admin/ai', {
        modelId: this.modelId, effort: this.effort, dailyBudgetUsd: Number(this.budget) || 0,
        allowRecordsRead: this.allowRecordsRead, externalAssistants: this.external, version: this.settings()?.version || undefined,
      }));
      this.toasts.show('AI settings saved.', 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The settings were not saved.', 'warning');
    }
  }
}
