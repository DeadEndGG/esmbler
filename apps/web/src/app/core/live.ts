import { Injectable, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { Auth } from './auth';

export interface DraftEvent {
  t: string;
  e: string;
  d: string;
  v: number;
  s: number;
  k: 'apply' | 'undo' | 'redo' | 'discard' | 'publish_requested' | 'published';
  targets?: string[];
  agent?: boolean;
  r?: number;
}

export interface AssistantEvent {
  conversationId: string;
  type: 'thinking' | 'tool' | 'step' | 'message' | 'question' | 'plan' | 'done' | 'error' | 'stopped';
  text?: string;
  tool?: string;
  seq?: number;
  targets?: string[];
  options?: string[];
  ok?: boolean;
}

/** Real-time channel (ADR 0008): draft changes and assistant progress, pushed by the API's SignalR hub. */
@Injectable({ providedIn: 'root' })
export class Live {
  private readonly auth = inject(Auth);
  private connection: HubConnection | null = null;
  private watchingDraft = false;
  readonly draft$ = new Subject<DraftEvent>();
  readonly assistant$ = new Subject<AssistantEvent>();
  readonly connected = signal(false);

  async start(): Promise<void> {
    if (this.connection) return;
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/live', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.on('draftChanged', (e: DraftEvent) => this.draft$.next(e));
    this.connection.on('assistantEvent', (e: AssistantEvent) => this.assistant$.next(e));
    this.connection.onreconnected(async () => {
      this.connected.set(true);
      if (this.watchingDraft) await this.connection!.invoke('WatchDraft').catch(() => undefined);
      // A reconnect may have missed events: listeners reload a full snapshot.
      this.draft$.next({ t: '', e: '', d: '', v: -1, s: 0, k: 'apply' });
    });
    this.connection.onreconnecting(() => this.connected.set(false));
    try {
      await this.connection.start();
      this.connected.set(true);
    } catch {
      this.connected.set(false);
    }
  }

  async watchDraft(): Promise<void> {
    await this.start();
    if (this.connection?.state === HubConnectionState.Connected) {
      await this.connection.invoke('WatchDraft');
      this.watchingDraft = true;
    }
  }

  async unwatchDraft(): Promise<void> {
    this.watchingDraft = false;
    if (this.connection?.state === HubConnectionState.Connected) await this.connection.invoke('UnwatchDraft').catch(() => undefined);
  }

  async stop(): Promise<void> {
    await this.connection?.stop();
    this.connection = null;
    this.connected.set(false);
  }
}
