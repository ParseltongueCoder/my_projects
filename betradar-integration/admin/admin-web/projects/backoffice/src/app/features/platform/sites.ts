import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Brand } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';

const MODULE_NAMES: Record<string, string> = {
  CAT: 'Catalog', I18N: 'Translations', ODDS: 'Odds & margins', CFG: 'Configuration', CMS: 'Messages', BET: 'Tickets', LIM: 'Limits',
  CASH: 'Cash-out', CUS: 'Customers', MON: 'Bet monitoring', REP: 'Reports', ADM: 'Administration', PROMO: 'Promotions',
  NOTIF: 'Alerts', INT: 'PAM integration', WL: 'White-label',
};

/** Brands (sites / domains, docs/09 §2.1) and enabled modules of the operator in context. */
@Component({
  selector: 'bo-sites',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Brands & modules</h1><div class="sub">{{ session.operator()?.name }}</div></div></div>
    <div class="grid-2">
      <section class="panel">
        <h2>Brands (sites)</h2>
        <table>
          <thead><tr><th>Brand</th><th>Players</th><th>Domain</th><th>Status</th></tr></thead>
          <tbody>
            @for (b of brands(); track b.id) {
              <tr><td><b>{{ b.name }}</b> <span class="muted mono">{{ b.code }}</span></td><td>{{ audience[b.audience] }}</td>
                <td class="mono">{{ b.primaryDomain }}</td><td><ui-status-tag [status]="b.status" /></td></tr>
            }
          </tbody>
        </table>
        @if (canManage()) {
          <h2 style="margin-top: 1rem">Add brand</h2>
          <div class="filters">
            <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="nb.code" /></mat-form-field>
            <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="nb.name" /></mat-form-field>
            <mat-form-field>
              <mat-label>Players</mat-label>
              <mat-select [(ngModel)]="nb.audience">
                <mat-option value="all">All</mat-option><mat-option value="local">Georgian players</mat-option><mat-option value="foreign">Foreign players only</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field><mat-label>Domain</mat-label><input matInput [(ngModel)]="nb.primaryDomain" /></mat-form-field>
            <button matButton="filled" [disabled]="!nb.code || !nb.name" (click)="addBrand()">Add</button>
          </div>
        }
      </section>
      <section class="panel">
        <h2>Modules</h2>
        @for (m of moduleCodes(); track m) {
          <div class="module">
            <mat-slide-toggle [checked]="modules()[m]" [disabled]="!canManage()" (change)="toggle(m, $event.checked)">
              {{ names[m] ?? m }} <span class="muted mono">{{ m }}</span>
            </mat-slide-toggle>
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    .module { padding: .25rem 0; }
  `,
})
export class SitesPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly brands = signal<Brand[]>([]);
  protected readonly modules = signal<Record<string, boolean>>({});
  protected readonly moduleCodes = signal<string[]>([]);
  protected readonly names = MODULE_NAMES;
  protected readonly audience: Record<string, string> = { all: 'All', local: 'Georgian players', foreign: 'Foreign players' };
  protected nb = { code: '', name: '', audience: 'all', primaryDomain: '' };

  constructor() {
    this.load();
  }

  protected canManage(): boolean {
    return this.session.has('platform.operator.manage') && !this.session.me()?.readOnly;
  }

  protected async addBrand(): Promise<void> {
    try {
      await firstValueFrom(this.api.createBrand(this.nb));
      this.nb = { code: '', name: '', audience: 'all', primaryDomain: '' };
      this.load();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  protected async toggle(module: string, enabled: boolean): Promise<void> {
    const reason = await askReason(this.dialog, { title: `${enabled ? 'Enable' : 'Disable'} ${MODULE_NAMES[module] ?? module}`, confirm: 'Save' });
    if (!reason) {
      this.load();
      return;
    }
    try {
      await firstValueFrom(this.api.setModules({ [module]: enabled }, reason));
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
    this.load();
  }

  private load(): void {
    this.api.brands().subscribe((b) => this.brands.set(b));
    this.api.modules().subscribe((m) => {
      this.modules.set(m);
      this.moduleCodes.set(Object.keys(m).sort((a, b) => (MODULE_NAMES[a] ?? a).localeCompare(MODULE_NAMES[b] ?? b)));
    });
  }
}
