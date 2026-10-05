import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Simulation } from '../../core/models';
import { fmtOdds, fmtPct, parseOdds, reasonLabel } from './odds-format';

/**
 * Margin simulator (docs/06 §4.8 /odds/margins): feed prices in, this operator's prices out — with what-if settings,
 * through the same Offer.Core pipeline the offer uses. Margin rules themselves are settings (margin.*), edited in CFG.
 */
@Component({
  selector: 'bo-margins',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, RouterLink, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Margin simulator</h1><div class="sub">What would players be offered? Same pipeline as the live offer; nothing is saved</div></div>
      <div class="toolbar-actions"><a matButton routerLink="/cfg/scope">Edit margin settings (margin.*) →</a></div>
    </div>
    <div class="grid-2">
      <section class="panel dialog-form">
        <mat-form-field>
          <mat-label>Feed odds</mat-label>
          <input matInput [ngModel]="odds()" (ngModelChange)="odds.set($event)" data-sim-odds />
          <mat-hint>"2.10 3.40 3.60" or "1=2.10 X=3.40 2=3.60"; or give a market id below</mat-hint>
        </mat-form-field>
        <mat-form-field><mat-label>…or market id (live prices)</mat-label><input matInput type="number" [ngModel]="marketId()" (ngModelChange)="marketId.set($event)" /></mat-form-field>
        <mat-slide-toggle [ngModel]="closedSet()" (ngModelChange)="closedSet.set($event)">Outcomes cover every result (1x2, totals …)</mat-slide-toggle>
        <div class="filters">
          <mat-form-field>
            <mat-label>Mode</mat-label>
            <mat-select [ngModel]="mode()" (ngModelChange)="mode.set($event)" data-sim-mode>
              <mat-option value="feed">feed (unchanged)</mat-option>
              <mat-option value="target">target margin</mat-option>
              <mat-option value="delta">delta per outcome</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field><mat-label>Target margin %</mat-label><input matInput type="number" step="0.5" [ngModel]="pct()" (ngModelChange)="pct.set($event)" data-sim-pct /></mat-form-field>
          <mat-form-field><mat-label>Delta %</mat-label><input matInput type="number" step="0.5" [ngModel]="delta()" (ngModelChange)="delta.set($event)" /></mat-form-field>
        </div>
        <div class="filters">
          <mat-form-field>
            <mat-label>Method</mat-label>
            <mat-select [ngModel]="method()" (ngModelChange)="method.set($event)">
              @for (m of methods; track m) { <mat-option [value]="m">{{ m }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Ladder</mat-label>
            <mat-select [ngModel]="ladder()" (ngModelChange)="ladder.set($event)">
              @for (l of ladders; track l) { <mat-option [value]="l">{{ l }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
        <div><button matButton="filled" (click)="run()" data-sim-run>Simulate</button></div>
      </section>
      <section class="panel">
        @if (result(); as r) {
          <div>
            <ui-status-tag [status]="r.priced.status" /> mode <b>{{ r.priced.modeUsed }}</b>
            @for (x of r.priced.reasons; track x) { <span class="chip">{{ label(x) }}</span> }
          </div>
          <div class="muted">feed margin {{ fmtPct(r.priced.feedOverround) }} → offered {{ fmtPct(r.priced.offerOverround) }}
            · method {{ r.settings.method }} · ladder {{ r.settings.ladder }} · min {{ r.settings.minOdds }} max {{ r.settings.maxOdds }}</div>
          <table>
            <thead><tr><th>Outcome</th><th class="num">Feed</th><th class="num">Fair %</th><th class="num">Offered</th></tr></thead>
            <tbody>
              @for (o of r.priced.outcomes; track o.code) {
                <tr [attr.data-sim-outcome]="o.code">
                  <td class="mono">{{ o.code }}</td>
                  <td class="num">{{ fmtOdds(o.feedOdds) }}</td>
                  <td class="num">{{ o.fairProbability === null ? '—' : fmtPct(o.fairProbability, 1) }}</td>
                  <td class="num"><b>{{ fmtOdds(o.odds) }}</b> @if (!o.visible) { <span class="muted">{{ o.hiddenReason }}</span> }</td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <div class="empty">Enter prices and simulate</div>
        }
      </section>
    </div>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; margin-top: .5rem; }
    th, td { text-align: left; padding: .35rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
  `,
})
export class MarginsPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  protected readonly methods = ['power', 'proportional', 'shin'];
  protected readonly ladders = ['std', 'fine', 'none'];
  protected readonly odds = signal('2.10 3.40 3.60');
  protected readonly marketId = signal<number | null>(null);
  protected readonly closedSet = signal(true);
  protected readonly mode = signal('target');
  protected readonly pct = signal(7);
  protected readonly delta = signal(2);
  protected readonly method = signal('power');
  protected readonly ladder = signal('std');
  protected readonly result = signal<Simulation | null>(null);
  protected readonly fmtOdds = fmtOdds;
  protected readonly fmtPct = fmtPct;
  protected readonly label = reasonLabel;

  protected run(): void {
    const outcomes = this.marketId() ? undefined : parseOdds(this.odds());
    if (!this.marketId() && !outcomes) {
      this.snack.open('Enter odds above 1, separated by spaces', 'OK', { duration: 4000 });
      return;
    }
    const settings = {
      mode: this.mode(), pct: Number(this.pct()) / 100, deltaPct: Number(this.delta()) / 100,
      method: this.method(), removeMethod: this.method(), ladder: this.ladder(),
    };
    this.api.simulate({ marketId: this.marketId() || null, outcomes: outcomes ?? undefined, closedSet: this.closedSet(), settings }).subscribe({
      next: (r) => this.result.set(r),
      error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
    });
  }
}
