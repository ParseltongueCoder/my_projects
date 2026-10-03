import { computed, inject, Injectable, signal } from '@angular/core';
import { FeedOpsApi } from './feed-ops-api';
import { Me } from './models';

/** Who is logged in and what they may do (roles come from Keycloak via /api/me). */
@Injectable({ providedIn: 'root' })
export class UserSession {
  private readonly api = inject(FeedOpsApi);
  readonly me = signal<Me | null>(null);
  readonly isOperator = computed(() => this.me()?.roles.includes('feedops-operator') ?? false);

  load(): void {
    this.api.me().subscribe({ next: (me) => this.me.set(me), error: () => this.me.set(null) });
  }
}
