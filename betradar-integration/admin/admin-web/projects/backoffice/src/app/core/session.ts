import { computed, inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from './bo-api';
import { Me } from './models';
import { OperatorSelection } from './operator-selection';

/** Who is logged in, for which operator, and what they may do (permissions come from /api/bo/me, enforced by the API). */
@Injectable({ providedIn: 'root' })
export class UserSession {
  private readonly api = inject(BoApi);
  private readonly selection = inject(OperatorSelection);

  readonly me = signal<Me | null>(null);
  readonly error = signal<string | null>(null);
  readonly loaded = signal(false);
  readonly operator = computed(() => this.me()?.operator ?? null);
  readonly isPlatform = computed(() => this.me()?.isPlatform ?? false);
  private readonly granted = computed(() => new Set(this.me()?.permissions ?? []));

  has(permission: string): boolean {
    return this.granted().has(permission);
  }

  async load(): Promise<void> {
    try {
      const me = await firstValueFrom(this.api.me());
      // A stale selection (operator removed or no longer allowed) falls back to platform level.
      if (me.isPlatform && this.selection.selected() && !me.operator) {
        this.selection.set(null);
      }
      this.me.set(me);
      this.error.set(null);
    } catch (e) {
      this.me.set(null);
      this.error.set(problemMessage(e));
    } finally {
      this.loaded.set(true);
    }
  }

  async switchOperator(id: number | null): Promise<void> {
    this.selection.set(id);
    await this.load();
  }
}

/** Route guard for pages needing a permission (`data: { permission }`) and/or an operator (`data: { needsOperator }`). */
export const permissionGuard: CanActivateFn = async (route) => {
  const session = inject(UserSession);
  const router = inject(Router);
  if (!session.me()) {
    await session.load();
  }
  const permission = route.data['permission'] as string | undefined;
  const needsOperator = route.data['needsOperator'] as boolean | undefined;
  if ((permission && !session.has(permission)) || (needsOperator && !session.operator()) || (route.data['platformOnly'] && !session.isPlatform())) {
    return router.createUrlTree(['/']);
  }
  return true;
};
