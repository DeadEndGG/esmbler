import { Injectable, signal } from '@angular/core';

type Theme = 'system' | 'light' | 'dark';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<Theme>(this.load());

  constructor() {
    this.apply(this.theme());
  }

  cycle(): void {
    const next: Theme = this.theme() === 'system' ? 'dark' : this.theme() === 'dark' ? 'light' : 'system';
    this.theme.set(next);
    this.apply(next);
    try {
      localStorage.setItem('esm.theme', next);
    } catch {
      /* per-viewer convenience only */
    }
  }

  private apply(theme: Theme): void {
    if (theme === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', theme);
  }

  private load(): Theme {
    try {
      return (localStorage.getItem('esm.theme') as Theme) ?? 'system';
    } catch {
      return 'system';
    }
  }
}
