import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { OverrideRow, TradingRow } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';
import { expiresIn, fmtOdds, fmtPct } from './odds-format';

/** Everything traders changed by hand that is still in force (docs/06 §4.8 /odds/overrides). */
@Component({
  selector: 'bo-odds-overrides',
  imports: [DatePipe, MatButtonModule, MatIconModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Active overrides</h1><div class="sub">Manual prices and suspensions in force; they end at their expiry</div></div>
      <div class="toolbar-actions"><button matButton (click)="load()"><mat-icon>refresh</mat-icon> Refresh</button></div>
    </div>
    <section class="panel">
      <h2>Odds overrides</h2>
      <table>
        <thead><tr><th>Event</th><th>Market</th><th>Outcome</th><th class="num">Override</th><th class="num">Feed now</th><th>Ends</th><th>By</th><th>Reason</th><th></th></tr></thead>
        <tbody>
          @for (o of overrides(); track o.id) {
            <tr [class.soon]="soon(o.expiresAt)" [attr.data-override-row]="o.id">
              <td><a class="link" [routerLink]="['/odds/events', o.eventId]">{{ o.eventName }}</a></td>
              <td class="mono">{{ o.marketTypeCode }} {{ o.specifiers }}</td>
              <td class="mono">{{ o.outcomeCode }}</td>
              <td class="num"><b>{{ o.kind === 'absolute' ? odds(o.value) : pct(o.value, 0) }}</b></td>
              <td class="num">{{ odds(o.feedOdds) }} @if (o.clearOn === 'feed_change') { <span class="chip">feed change</span> }</td>
              <td>{{ expires(o.expiresAt) }} <span class="muted">{{ o.expiresAt | date: 'shortTime' }}</span></td>
              <td>{{ o.createdByName }}</td>
              <td>{{ o.reason }}</td>
              <td>@if (canOverride()) { <button matButton (click)="clearOverride(o)">Clear</button> }</td>
            </tr>
          } @empty { <tr><td colspan="9" class="empty">No active odds overrides</td></tr> }
        </tbody>
      </table>
    </section>
    <section class="panel">
      <h2>Suspended and closed</h2>
      <table>
        <thead><tr><th>Event</th><th>Scope</th><th>Action</th><th>Ends</th><th>By</th><th>Reason</th><th></th></tr></thead>
        <tbody>
          @for (x of trading(); track x.id) {
            <tr [attr.data-trading-row]="x.id">
              <td><a class="link" [routerLink]="['/odds/events', x.eventId]">{{ x.eventName ?? '#' + x.eventId }}</a></td>
              <td>{{ x.scopeType === 'event' ? 'whole event' : x.marketTypeCode + ' ' + (x.specifiers ?? '') }}</td>
              <td>{{ x.action }} @if (x.platform) { <span class="chip warn">platform</span> }</td>
              <td>{{ expires(x.expiresAt) }}</td>
              <td>{{ x.createdByName }}</td>
              <td>{{ x.reason }}</td>
              <td>@if (canSuspend() && (!x.platform || session.isPlatform())) { <button matButton (click)="lift(x)">Lift</button> }</td>
            </tr>
          } @empty { <tr><td colspan="7" class="empty">Nothing suspended or closed</td></tr> }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.soon { background: var(--fo-warn-bg); }
  `,
})
export class OddsOverridesPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly lang = inject(ContentLanguage);
  protected readonly session = inject(UserSession);
  protected readonly overrides = signal<OverrideRow[]>([]);
  protected readonly trading = signal<TradingRow[]>([]);
  protected readonly odds = fmtOdds;
  protected readonly pct = fmtPct;
  protected readonly expires = (iso: string | null) => expiresIn(iso);

  constructor() {
    this.load();
  }

  load(): void {
    const lang = this.lang.lang();
    this.api.overrides(undefined, lang).subscribe({ next: (r) => this.overrides.set(r), error: (e) => this.error(e) });
    this.api.trading(undefined, lang).subscribe({ next: (r) => this.trading.set(r), error: (e) => this.error(e) });
  }

  /** Ending within five minutes (docs/06 §4.8). */
  protected soon(iso: string): boolean {
    return new Date(iso).getTime() - Date.now() < 5 * 60000;
  }

  protected canOverride(): boolean {
    return this.session.has('odds.override') && !this.session.me()?.readOnly;
  }

  protected canSuspend(): boolean {
    return this.session.has('odds.suspend') && !this.session.me()?.readOnly;
  }

  protected async clearOverride(o: OverrideRow): Promise<void> {
    const reason = await askReason(this.dialog, { title: `Clear override ${o.outcomeCode}`, confirm: 'Clear', minLength: 0 });
    if (reason !== null) {
      await this.run(this.api.clearOverride(o.id, reason || 'cleared'));
    }
  }

  protected async lift(x: TradingRow): Promise<void> {
    const reason = await askReason(this.dialog, { title: `Lift ${x.action}`, confirm: 'Lift', minLength: 0 });
    if (reason !== null) {
      await this.run(this.api.clearTrading(x.id, reason || 'lifted'));
    }
  }

  private async run(call: Observable<unknown>): Promise<void> {
    try {
      await firstValueFrom(call);
      this.load();
    } catch (e) {
      this.error(e);
    }
  }

  private error(e: unknown): void {
    this.snack.open(problemMessage(e), 'OK', { duration: 6000 });
  }
}
