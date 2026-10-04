import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { KpiCard } from '@admin/ui';
import { BoApi } from '../../core/bo-api';
import { AuditEntry, Brand, ChangeSet } from '../../core/models';
import { UserSession } from '../../core/session';

@Component({
  selector: 'bo-home',
  imports: [MatIconModule, RouterLink, DatePipe, KpiCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session.me(); as me) {
      <div class="page-head">
        <div>
          <h1>{{ me.operator?.name ?? 'Platform' }}</h1>
          <div class="sub">Welcome, {{ me.user.displayName ?? me.user.username }}{{ me.isPlatform ? ' (platform staff)' : '' }}</div>
        </div>
      </div>

      @if (me.isPlatform && !me.operator) {
        <div class="panel">
          <h2>Platform level</h2>
          <p>Choose an operator in the header to see and manage its data. Platform-wide settings are under
            <a class="link" routerLink="/cfg/scope" [queryParams]="{ scope: 'platform' }">Settings → Platform</a>; tenants under
            <a class="link" routerLink="/platform/operators">Operators</a>.</p>
        </div>
      } @else {
        <div class="kpis">
          <ui-kpi-card label="Brands (sites)" [value]="brands().length" />
          <ui-kpi-card label="Pending approvals" [value]="pending().length" [tone]="pending().length ? 'warn' : 'neutral'" />
          <ui-kpi-card label="My permissions" [value]="me.permissions.length" />
        </div>
        <div class="grid-2">
          <section class="panel">
            <h2>Brands</h2>
            @for (b of brands(); track b.id) {
              <div class="row"><b>{{ b.name }}</b> <span class="chip">{{ b.audience }}</span> <span class="muted mono">{{ b.primaryDomain }}</span></div>
            } @empty { <div class="muted">No brands</div> }
          </section>
          <section class="panel">
            <h2>Waiting for approval</h2>
            @for (c of pending(); track c.id) {
              <div class="row"><a class="link" routerLink="/cfg/change-sets">#{{ c.id }} {{ c.title }}</a>
                <span class="muted">by {{ c.createdByName }}, {{ c.createdAt | date: 'short' }}</span></div>
            } @empty { <div class="muted">Nothing to approve</div> }
          </section>
        </div>
      }
      @if (session.has('adm.audit.view')) {
        <section class="panel">
          <h2>Recent activity</h2>
          @for (a of activity(); track a.id) {
            <div class="row"><span class="muted">{{ a.ts | date: 'short' }}</span> <b>{{ a.actorName }}</b> <span class="mono">{{ a.action }}</span>
              <span class="muted">{{ a.entityType }} {{ a.entityId }}</span></div>
          } @empty { <div class="muted">No activity yet</div> }
        </section>
      }
    }
  `,
  styles: `
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr)); gap: .75rem; margin-bottom: 1rem; }
    .row { display: flex; gap: .5rem; align-items: baseline; padding: .3rem 0; flex-wrap: wrap; border-bottom: 1px solid var(--fo-border); }
    .row:last-child { border-bottom: 0; }
  `,
})
export class HomePage {
  protected readonly session = inject(UserSession);
  private readonly api = inject(BoApi);
  protected readonly brands = signal<Brand[]>([]);
  protected readonly pending = signal<ChangeSet[]>([]);
  protected readonly activity = signal<AuditEntry[]>([]);

  constructor() {
    effect(() => {
      const me = this.session.me();
      if (!me) {
        return;
      }
      if (me.operator) {
        if (this.session.has('adm.brand.view')) {
          this.api.brands().subscribe((b) => this.brands.set(b));
        }
        if (this.session.has('cfg.view')) {
          this.api.changeSets('pending_approval').subscribe((c) => this.pending.set(c));
        }
      }
      if (this.session.has('adm.audit.view')) {
        this.api.audit({ limit: 10 }).subscribe((p) => this.activity.set(p.items));
      }
    });
  }
}
