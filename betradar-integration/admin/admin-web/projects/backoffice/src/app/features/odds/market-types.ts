import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { MarketTypeRow, MatrixCell } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';
import { fmtPct } from './odds-format';

/**
 * Market type × sport matrix of CFG <c>market.enabled</c> (docs/06 §4.5). Switching off writes false at that level;
 * switching on removes it again. Off higher up (all sports, or by the platform) wins: those cells cannot be ticked.
 */
@Component({
  selector: 'bo-market-types',
  imports: [FormsModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Market types</h1><div class="sub">Which market types are offered, overall and per sport (setting market.enabled)</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search market type</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
      </div>
      <div class="scroll">
        <table>
          <thead>
            <tr>
              <th>Market type</th><th>Margin</th><th class="c">All sports</th>
              @for (s of sports(); track s.id) { <th class="c">{{ s.name }}</th> }
            </tr>
          </thead>
          <tbody>
            @for (m of items(); track m.id) {
              <tr [attr.data-market-type]="m.id">
                <td><b>{{ m.name }}</b> <span class="muted mono">#{{ m.id }} {{ m.code }}</span>
                  @if (!m.platformEnabled) { <span class="chip warn">off by platform</span> }</td>
                <td class="muted">{{ m.marginMode }} @if (m.marginMode === 'target') { {{ pct(m.marginPct, 1) }} }</td>
                <td class="c">
                  <mat-checkbox [checked]="m.platformEnabled && m.operatorCell.enabled" [disabled]="!canEdit() || !togglable(m.operatorCell, m.platformEnabled)"
                                [matTooltip]="tip(m.operatorCell)" (change)="toggle(m, null, $event.checked)" [attr.data-cell]="m.id + ':all'" />
                </td>
                @for (s of sports(); track s.id) {
                  <td class="c">
                    <mat-checkbox [checked]="m.platformEnabled && m.sportCells[s.id].enabled"
                                  [disabled]="!canEdit() || !togglable(m.sportCells[s.id], m.platformEnabled)"
                                  [matTooltip]="tip(m.sportCells[s.id]) + ' · ' + (m.openMarkets[s.id] ?? 0) + ' open markets'"
                                  (change)="toggle(m, s.id, $event.checked)" [attr.data-cell]="m.id + ':' + s.id" />
                    @if (m.openMarkets[s.id]) { <span class="muted small">{{ m.openMarkets[s.id] }}</span> }
                  </td>
                }
              </tr>
            } @empty { <tr><td colspan="3" class="empty">No market types</td></tr> }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styles: `
    .scroll { overflow-x: auto; }
    table { border-collapse: collapse; min-width: 100%; }
    th, td { text-align: left; padding: .3rem .5rem; border-bottom: 1px solid var(--fo-border); white-space: nowrap; }
    th { color: var(--fo-muted); font-weight: 500; }
    .c { text-align: center; }
    .small { font-size: .75rem; }
  `,
})
export class MarketTypesPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly session = inject(UserSession);
  private readonly lang = inject(ContentLanguage);
  protected readonly q = signal('');
  protected readonly sports = signal<{ id: number; name: string }[]>([]);
  protected readonly items = signal<MarketTypeRow[]>([]);
  protected readonly pct = fmtPct;

  constructor() {
    this.lang.load();
    effect(() => this.load(this.q(), this.lang.lang()));
  }

  private load(q: string, lang: string): void {
    this.api.marketTypeMatrix(q, lang).subscribe({
      next: (r) => (this.sports.set(r.sports), this.items.set(r.items)),
      error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
    });
  }

  protected canEdit(): boolean {
    return this.session.has('cfg.edit') && this.session.has('odds.margin.edit') && !this.session.me()?.readOnly;
  }

  /** On: can be switched off here. Off: only where it was switched off at this very level. */
  protected togglable(cell: MatrixCell, platformEnabled: boolean): boolean {
    return platformEnabled && (cell.enabled || cell.setHere === false);
  }

  protected tip(cell: MatrixCell): string {
    return cell.setHere === false ? 'switched off here' : cell.enabled ? 'on (inherited)' : 'off higher up';
  }

  protected async toggle(m: MarketTypeRow, sportId: number | null, enabled: boolean): Promise<void> {
    const where = sportId === null ? 'all sports' : this.sports().find((s) => s.id === sportId)?.name;
    const reason = await askReason(this.dialog, { title: `${enabled ? 'Offer' : 'Stop offering'} ${m.name} — ${where}`, confirm: enabled ? 'Switch on' : 'Switch off' });
    if (reason === null) {
      this.load(this.q(), this.lang.lang()); // undo the checkbox
      return;
    }
    try {
      const set = await firstValueFrom(this.api.toggleMarketType(m.id, sportId, enabled, reason));
      this.snack.open(set.status === 'applied' ? 'Applied' : 'Waiting for approval', 'OK', { duration: 3000 });
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
    this.load(this.q(), this.lang.lang());
  }
}
