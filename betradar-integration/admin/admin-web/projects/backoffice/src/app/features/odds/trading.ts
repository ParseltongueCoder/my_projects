import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { EventOffer, MarketOffer, OutcomeOffer, Trading } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';
import {
  ManualMarketDialog, ManualMarketResult, OverrideDialog, OverrideDialogData, OverrideDialogResult, TradingDialog, TradingDialogData, TradingDialogResult,
} from './dialogs';
import { drift, expiresIn, fmtOdds, fmtPct, reasonLabel } from './odds-format';

/**
 * Trading view of one event (docs/06 §4.8): what the feed sends next to what this operator offers, why a market is
 * restricted, overrides with their expiry, suspend / close, manual markets. Refreshes every 5 s while live.
 */
@Component({
  selector: 'bo-trading-view',
  imports: [DatePipe, NgTemplateOutlet, FormsModule, MatButtonModule, MatIconModule, MatMenuModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule, RouterLink, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (offer(); as o) {
      <div class="page-head">
        <div>
          <a class="link" routerLink="/odds">← Events</a>
          <h1 data-trading-event>{{ o.event.name }}</h1>
          <div class="sub">
            {{ o.event.tournamentName }} · {{ o.event.scheduledAt | date: 'medium' }} · <ui-status-tag [status]="o.event.status" />
            @if (o.event.live) { <span class="chip warn">live</span> }
          </div>
        </div>
        <div class="toolbar-actions">
          <mat-slide-toggle [ngModel]="auto()" (ngModelChange)="auto.set($event)">Auto-refresh</mat-slide-toggle>
          <button matButton (click)="load()"><mat-icon>refresh</mat-icon> Refresh</button>
          @if (can('odds.suspend')) {
            <button matButton="tonal" (click)="trade('event', o.event.id, 'suspend', o.event.name)" data-suspend-event><mat-icon>pause_circle</mat-icon> Suspend event</button>
            <button matButton (click)="trade('event', o.event.id, 'close', o.event.name)"><mat-icon>visibility_off</mat-icon> Close event</button>
          }
          @if (can('cat.market.add_manual')) {
            <button matButton="filled" (click)="addManual(o)" data-add-manual><mat-icon>add</mat-icon> Manual market</button>
          }
        </div>
      </div>

      @if (o.trading.length) {
        <section class="panel">
          @for (x of o.trading; track x.id) { <ng-container [ngTemplateOutlet]="tradingChip" [ngTemplateOutletContext]="{ $implicit: x }" /> }
        </section>
      }

      @for (m of o.markets; track m.id) {
        <section class="panel market" [class.restricted]="m.status !== 'active'" [attr.data-market]="m.id">
          <div class="market-head">
            <div>
              <b>{{ m.name }}</b>
              @if (m.specifiers) { <span class="muted mono"> {{ m.specifiers }}</span> }
              @if (m.isManual) { <span class="chip primary">manual</span> }
              <ui-status-tag [status]="m.status" [attr.data-market-status]="m.status" />
              @for (r of m.reasons; track r) { <span class="chip" [matTooltip]="r">{{ label(r) }}</span> }
            </div>
            <div class="muted small">
              {{ m.mode }} @if (m.mode === 'target') { {{ pct(m.settings.pct, 1) }} · {{ m.settings.method }} }
              @if (m.mode === 'delta') { {{ pct(m.settings.deltaPct, 1) }} }
              · margin @if (!m.isManual) { feed {{ pct(m.feedOverround) }} → } offered {{ pct(m.offerOverround) }}
              @if (can('odds.suspend')) {
                <button matIconButton [matMenuTriggerFor]="menu" aria-label="Market actions"><mat-icon>more_vert</mat-icon></button>
                <mat-menu #menu="matMenu">
                  <button mat-menu-item (click)="trade('market', m.id, 'suspend', m.name)">Suspend market</button>
                  <button mat-menu-item (click)="trade('market', m.id, 'close', m.name)">Close market</button>
                </mat-menu>
              }
            </div>
          </div>
          @for (x of m.trading; track x.id) { <ng-container [ngTemplateOutlet]="tradingChip" [ngTemplateOutletContext]="{ $implicit: x }" /> }
          <table>
            <thead><tr><th>Outcome</th><th class="num">Feed</th><th class="num">Fair %</th><th class="num">Offered</th><th>Source</th><th>Override</th><th></th></tr></thead>
            <tbody>
              @for (x of m.outcomes; track x.code) {
                <tr [class.muted]="!x.visible" [attr.data-outcome]="x.code">
                  <td>{{ x.name }} <span class="muted mono">{{ x.code }}</span></td>
                  <td class="num">
                    @if (m.isManual && canManual()) {
                      <input class="odds-input" type="number" step="0.01" min="1.01" [ngModel]="draft()[m.id + ':' + x.code] ?? x.feedOdds"
                             (ngModelChange)="setDraft(m.id, x.code, $event)" [attr.data-manual-price]="x.code" />
                    } @else { {{ odds(x.feedOdds) }} }
                  </td>
                  <td class="num">{{ x.fairProbability === null ? '—' : pct(x.fairProbability, 1) }}</td>
                  <td class="num" [class.up]="(diff(x) ?? 0) > 0.0001" [class.down]="(diff(x) ?? 0) < -0.0001" [attr.data-offered]="x.code">
                    <b>{{ odds(x.odds) }}</b> @if (!x.visible) { <span class="muted small">{{ x.hiddenReason }}</span> }
                  </td>
                  <td><span class="chip" [class.warn]="x.source === 'override'">{{ x.source }}</span></td>
                  <td>
                    @if (x.override; as ov) {
                      <span [class.stale]="!x.overrideApplied" [matTooltip]="ov.reason + ' · ' + (ov.createdByName ?? '')">
                        {{ ov.kind === 'absolute' ? odds(ov.value) : pct(ov.value, 0) }} · {{ expires(ov.expiresAt) }}
                        @if (!x.overrideApplied) { (not applied) }
                      </span>
                      @if (can('odds.override')) { <button matIconButton (click)="clearOverride(ov.id)" aria-label="Clear override" data-clear-override><mat-icon>close</mat-icon></button> }
                    }
                  </td>
                  <td>
                    @if (!m.isManual && can('odds.override') && x.feedOdds) {
                      <button matButton (click)="override(o, m, x)" [attr.data-override]="x.code">Override</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
          @if (m.isManual && canManual()) {
            <div class="manual-bar">
              <mat-select class="status-select" [ngModel]="m.feedStatus" (ngModelChange)="manualStatus(m, $event)" aria-label="Manual market status">
                <mat-option value="active">active</mat-option>
                <mat-option value="suspended">suspended</mat-option>
                <mat-option value="deactivated">deactivated</mat-option>
              </mat-select>
              <button matButton="tonal" [disabled]="!hasDraft(m)" (click)="saveManual(m)" data-save-manual>Save prices</button>
            </div>
          }
        </section>
      } @empty {
        <section class="panel empty">No markets for this event yet</section>
      }

      <ng-template #tradingChip let-x>
        <div class="trading" [attr.data-trading]="x.action">
          <mat-icon>{{ x.action === 'suspend' ? 'pause_circle' : 'visibility_off' }}</mat-icon>
          <b>{{ x.action === 'suspend' ? 'Suspended' : 'Closed' }}</b> @if (x.platform) { <span class="chip danger">platform</span> }
          <span class="muted">{{ x.reason }} · {{ x.createdByName }} · {{ expires(x.expiresAt) }}</span>
          @if (can('odds.suspend') && (!x.platform || session.isPlatform())) {
            <button matButton (click)="lift(x)" data-lift>Lift</button>
          }
        </div>
      </ng-template>
    } @else {
      <section class="panel empty">Loading…</section>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; margin-top: .4rem; }
    th, td { text-align: left; padding: .3rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    .market.restricted { border-left: 3px solid var(--fo-warn); }
    .market-head { display: flex; justify-content: space-between; align-items: center; gap: .5rem; flex-wrap: wrap; }
    .small { font-size: .8rem; }
    .up { color: var(--fo-success); } .down { color: var(--fo-danger); }
    .stale { text-decoration: line-through; color: var(--fo-muted); }
    .trading { display: flex; align-items: center; gap: .5rem; padding: .2rem 0; }
    .odds-input { width: 5.5rem; font: inherit; padding: .15rem .3rem; text-align: right; }
    .manual-bar { display: flex; gap: .75rem; align-items: center; margin-top: .5rem; }
    .status-select { width: 10rem; }
    .chip.danger { color: var(--fo-danger); background: var(--fo-danger-bg); }
  `,
})
export class TradingViewPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  private readonly lang = inject(ContentLanguage);
  readonly id = input.required<string>();

  protected readonly offer = signal<EventOffer | null>(null);
  protected readonly auto = signal(true);
  protected readonly draft = signal<Record<string, number | null>>({});
  protected readonly odds = fmtOdds;
  protected readonly pct = fmtPct;
  protected readonly label = reasonLabel;
  protected readonly expires = (iso: string | null) => expiresIn(iso);

  constructor() {
    this.lang.load();
    effect(() => {
      this.id();
      this.lang.lang();
      this.load();
    });
    const timer = setInterval(() => {
      if (this.auto() && this.offer()?.event.live && Object.keys(this.draft()).length === 0) {
        this.load();
      }
    }, 5000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected can(permission: string): boolean {
    return this.session.has(permission) && !this.session.me()?.readOnly;
  }

  protected canManual(): boolean {
    return this.can('cat.market.add_manual');
  }

  protected diff(x: OutcomeOffer): number | null {
    return drift(x.feedOdds, x.odds);
  }

  load(): void {
    this.api.eventOffer(Number(this.id()), this.lang.lang()).subscribe({
      next: (o) => this.offer.set(o),
      error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
    });
  }

  protected async trade(scopeType: 'event' | 'market', scopeId: number, action: 'suspend' | 'close', name: string): Promise<void> {
    const ref = this.dialog.open<TradingDialog, TradingDialogData, TradingDialogResult>(TradingDialog, {
      data: { title: `${action === 'suspend' ? 'Suspend' : 'Close'} ${name}`, action, canPlatform: this.session.isPlatform() },
      width: '30rem',
    });
    const r = await firstValueFrom(ref.afterClosed());
    if (r) {
      await this.run(this.api.createTrading({ scopeType, scopeId, action, ...r }), action === 'suspend' ? 'Suspended' : 'Closed');
    }
  }

  protected async lift(x: Trading): Promise<void> {
    const reason = await askReason(this.dialog, { title: `Lift ${x.action}`, confirm: 'Lift', minLength: 0 });
    if (reason !== null) {
      await this.run(this.api.clearTrading(x.id, reason || 'lifted'), 'Lifted');
    }
  }

  protected async override(o: EventOffer, market: MarketOffer, outcome: OutcomeOffer): Promise<void> {
    const ref = this.dialog.open<OverrideDialog, OverrideDialogData, OverrideDialogResult>(OverrideDialog, {
      data: { market, outcome, live: o.event.live }, width: '32rem',
    });
    const r = await firstValueFrom(ref.afterClosed());
    if (!r) {
      return;
    }
    try {
      const res = await firstValueFrom(this.api.createOverride({ marketId: market.id, outcomeCode: outcome.code, ...r }));
      this.snack.open(['Override set', ...res.warnings].join(' · '), 'OK', { duration: res.warnings.length ? 9000 : 3000 });
      this.load();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  protected async clearOverride(id: number): Promise<void> {
    await this.run(this.api.clearOverride(id, 'cleared in trading view'), 'Override cleared');
  }

  protected async addManual(o: EventOffer): Promise<void> {
    const ref = this.dialog.open<ManualMarketDialog, { lang: string }, ManualMarketResult>(ManualMarketDialog, { data: { lang: this.lang.lang() }, width: '36rem' });
    const r = await firstValueFrom(ref.afterClosed());
    if (r) {
      await this.run(this.api.createManualMarket(o.event.id, r), 'Manual market created');
    }
  }

  protected setDraft(marketId: number, code: string, value: number | null): void {
    this.draft.update((d) => ({ ...d, [`${marketId}:${code}`]: value }));
  }

  protected hasDraft(m: MarketOffer): boolean {
    return Object.keys(this.draft()).some((k) => k.startsWith(`${m.id}:`));
  }

  protected async saveManual(m: MarketOffer): Promise<void> {
    const outcomes = Object.entries(this.draft()).filter(([k]) => k.startsWith(`${m.id}:`))
      .map(([k, v]) => ({ code: k.slice(String(m.id).length + 1), odds: v ? Number(v) : null, isActive: !!v }));
    if (await this.run(this.api.patchManualMarket(m.id, { outcomes, reason: 'prices' }), 'Prices saved')) {
      this.draft.update((d) => Object.fromEntries(Object.entries(d).filter(([k]) => !k.startsWith(`${m.id}:`))));
    }
  }

  protected async manualStatus(m: MarketOffer, status: string): Promise<void> {
    await this.run(this.api.patchManualMarket(m.id, { status, reason: `status ${status}` }), `Market ${status}`);
  }

  private async run(call: Observable<unknown>, done: string): Promise<boolean> {
    try {
      await firstValueFrom(call);
      this.snack.open(done, 'OK', { duration: 3000 });
      this.load();
      return true;
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
      return false;
    }
  }
}
