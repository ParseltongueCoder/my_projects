import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { CatalogEvent } from '../../core/models';

/** Entry to trading: live first, then upcoming events; a row opens the trading view. */
@Component({
  selector: 'bo-odds-events',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, RouterLink, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Trading</h1><div class="sub">Feed against offered prices, overrides, suspensions and manual markets per event</div></div>
      <div class="toolbar-actions">
        <a matButton routerLink="/odds/overrides"><mat-icon>schedule</mat-icon> Active overrides</a>
        <a matButton routerLink="/odds/market-types"><mat-icon>grid_on</mat-icon> Market types</a>
        <a matButton routerLink="/odds/margins"><mat-icon>calculate</mat-icon> Margin simulator</a>
      </div>
    </div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search team, URN or id</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <mat-form-field>
          <mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)">
            <mat-option value="">Any</mat-option>
            <mat-option value="live">live</mat-option>
            <mat-option value="not_started">not started</mat-option>
            <mat-option value="suspended">suspended</mat-option>
          </mat-select>
        </mat-form-field>
      </div>
      <table>
        <thead><tr><th>Start</th><th>Event</th><th>League</th><th>Status</th><th class="num">Open markets</th><th></th></tr></thead>
        <tbody>
          @for (e of items(); track e.id) {
            <tr class="clickable" [routerLink]="['/odds/events', e.id]" [attr.data-trading-row]="e.id">
              <td>{{ e.scheduledAt | date: 'short' }}</td>
              <td><b>{{ e.name }}</b></td>
              <td>{{ e.tournamentName }} <span class="muted">· {{ e.sportName }}</span></td>
              <td><ui-status-tag [status]="e.status" /></td>
              <td class="num">{{ e.openMarkets }}</td>
              <td><mat-icon>chevron_right</mat-icon></td>
            </tr>
          } @empty { <tr><td colspan="6" class="empty">No events</td></tr> }
        </tbody>
      </table>
      <div class="pager"><span class="muted">{{ total() }} events</span>
        <button matIconButton [disabled]="page() === 1" (click)="page.set(page() - 1)" aria-label="Previous"><mat-icon>chevron_left</mat-icon></button>
        <span>{{ page() }}</span>
        <button matIconButton [disabled]="page() * 50 >= total()" (click)="page.set(page() + 1)" aria-label="Next"><mat-icon>chevron_right</mat-icon></button>
      </div>
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.clickable { cursor: pointer; } tr.clickable:hover { background: var(--fo-surface-alt); }
  `,
})
export class OddsEventsPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  private readonly lang = inject(ContentLanguage);
  protected readonly q = signal('');
  protected readonly status = signal('');
  protected readonly page = signal(1);
  protected readonly items = signal<CatalogEvent[]>([]);
  protected readonly total = signal(0);

  constructor() {
    this.lang.load();
    effect(() => {
      this.api.events({ q: this.q(), status: this.status(), page: this.page(), lang: this.lang.lang() }).subscribe({
        next: (r) => (this.items.set(r.items), this.total.set(r.total)),
        error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
      });
    });
  }
}
