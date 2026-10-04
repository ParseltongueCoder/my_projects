import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { BoApi } from '../../core/bo-api';
import { Permission, Role } from '../../core/models';

/** System roles and their permissions as a matrix (docs/08 §3.3). Custom roles come in P1. */
@Component({
  selector: 'bo-roles',
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Roles</h1><div class="sub">Permissions are checked by the API on every request</div></div></div>
    <section class="panel matrix">
      <table>
        <thead>
          <tr><th>Permission</th>@for (r of roles(); track r.id) { <th class="role" [matTooltip]="r.code">{{ r.name }}</th> }</tr>
        </thead>
        <tbody>
          @for (g of groups(); track g.module) {
            <tr class="group"><td [attr.colspan]="roles().length + 1">{{ g.module }}</td></tr>
            @for (p of g.permissions; track p.code) {
              <tr>
                <td><span class="mono">{{ p.code }}</span> @if (p.risk !== 'normal') { <span class="chip" [class.danger]="p.risk === 'critical'" [class.warn]="p.risk === 'sensitive'">{{ p.risk }}</span> }
                  <div class="muted small">{{ p.description }}</div></td>
                @for (r of roles(); track r.id) {
                  <td class="cell">@if (r.permissions.includes(p.code)) { <mat-icon>check</mat-icon> }</td>
                }
              </tr>
            }
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    .matrix { overflow-x: auto; }
    table { border-collapse: collapse; }
    th, td { padding: .3rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th.role { writing-mode: vertical-rl; transform: rotate(180deg); font-weight: 500; color: var(--fo-muted); height: 9rem; }
    tr.group td { font-weight: 700; background: var(--fo-surface-alt); }
    td.cell { text-align: center; } td.cell mat-icon { color: var(--fo-success); font-size: 18px; width: 18px; height: 18px; }
    .small { font-size: .78rem; }
  `,
})
export class RolesPage {
  protected readonly roles = signal<Role[]>([]);
  private readonly permissions = signal<Permission[]>([]);
  protected readonly groups = computed(() => {
    const byModule = new Map<string, Permission[]>();
    for (const p of this.permissions()) {
      byModule.set(p.module, [...(byModule.get(p.module) ?? []), p]);
    }
    return [...byModule.entries()].map(([module, permissions]) => ({ module, permissions }));
  });

  constructor() {
    const api = inject(BoApi);
    api.roles().subscribe((r) => this.roles.set(r));
    api.permissions().subscribe((p) => this.permissions.set(p));
  }
}
