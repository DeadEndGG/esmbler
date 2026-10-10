import { Injectable } from '@angular/core';

interface Remembered {
  url: string;
  scrollTop: number;
  cursor: number;
}

/**
 * Where people were in each explorer (REQ-WS-003): the list URL (filters, sort, columns, grouping and page are all in
 * the URL) and the scroll position, so opening a record and coming back lands exactly where they left.
 */
@Injectable({ providedIn: 'root' })
export class ExplorerState {
  private readonly byEntity = new Map<string, Remembered>();

  remember(entity: string, url: string, scrollTop: number, cursor: number): void {
    this.byEntity.set(entity, { url, scrollTop, cursor });
  }

  /** The explorer URL to return to from a record of this entity. */
  returnUrl(entity: string): string {
    return this.byEntity.get(entity)?.url ?? `/records/${entity}`;
  }

  /** Scroll position and cursor to restore when the explorer opens on the remembered URL. */
  restore(entity: string, url: string): Remembered | null {
    const r = this.byEntity.get(entity);
    return r && r.url === url ? r : null;
  }
}

/** The scrolling container of the shell. */
export function mainScroller(): HTMLElement | null {
  return document.getElementById('main');
}
