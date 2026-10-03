import { DestroyRef, inject, Injectable, signal } from '@angular/core';
import { Observable, Subject, debounceTime, filter, map, merge, share, startWith } from 'rxjs';
import { AuthSession } from './auth';
import { APP_CONFIG } from './config';
import { Change } from './models';

export type StreamState = 'connecting' | 'live' | 'offline';

/**
 * Canonical-model changes pushed by the API (Server-Sent Events). Uses fetch instead of EventSource
 * so the bearer token can travel in the Authorization header; reconnects with backoff.
 */
@Injectable({ providedIn: 'root' })
export class LiveChanges {
  private readonly auth = inject(AuthSession);
  private readonly url = `${inject(APP_CONFIG).apiBaseUrl}/api/stream`;
  private readonly changes = new Subject<Change>();
  private started = false;
  private abort?: AbortController;

  readonly state = signal<StreamState>('connecting');
  readonly changes$: Observable<Change> = this.changes.asObservable().pipe(share());

  constructor() {
    inject(DestroyRef).onDestroy(() => this.abort?.abort());
  }

  /**
   * Emits once immediately and then (debounced) whenever a matching change arrives - the usual
   * "load, then reload on change" pattern of every page.
   */
  refreshOn(match: (c: Change) => boolean = () => true, debounceMs = 400): Observable<void> {
    this.start();
    return merge(this.changes$.pipe(filter(match), debounceTime(debounceMs))).pipe(
      map(() => undefined),
      startWith(undefined),
    );
  }

  start(): void {
    if (this.started) {
      return;
    }
    this.started = true;
    void this.connectLoop();
  }

  private async connectLoop(): Promise<void> {
    let delay = 1000;
    for (;;) {
      this.abort = new AbortController();
      try {
        this.state.set('connecting');
        const token = await this.auth.accessToken();
        const response = await fetch(this.url, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
          signal: this.abort.signal,
        });
        if (!response.ok || !response.body) {
          throw new Error(`stream ${response.status}`);
        }
        this.state.set('live');
        delay = 1000;
        await this.read(response.body);
      } catch {
        if (this.abort.signal.aborted) {
          return;
        }
      }
      this.state.set('offline');
      await new Promise((r) => setTimeout(r, delay));
      delay = Math.min(delay * 2, 30_000);
    }
  }

  private async read(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
      const { value, done } = await reader.read();
      if (done) {
        return;
      }
      buffer += decoder.decode(value, { stream: true });
      let end: number;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const change = parseSseEvent(buffer.slice(0, end));
        buffer = buffer.slice(end + 2);
        if (change) {
          this.changes.next(change);
        }
      }
    }
  }
}

/** Parses one SSE event block; comments (": ping") and other event types yield null. */
export function parseSseEvent(block: string): Change | null {
  let event = 'message';
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) {
      event = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      data.push(line.slice(5).trim());
    }
  }
  if (event !== 'change' || data.length === 0) {
    return null;
  }
  try {
    return JSON.parse(data.join('\n')) as Change;
  } catch {
    return null;
  }
}
