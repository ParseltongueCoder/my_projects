import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Brand, EffectiveSetting, MarketType, ScopeOption, SettingDef } from '../../core/models';
import { formatValue } from '../../shared/setting-value';
import { MarketTypeSearch, ScopeSearch } from './scope-picker';

const LEVEL: Record<string, string> = {
  platform: 'Platform', operator: 'Operator', brand: 'Brand', sport: 'Sport', category: 'Country', tournament: 'League', event: 'Event', market: 'Market',
};

/** "Why is this value X?" (docs/06 §5.6): effective values for a context with every candidate row on the path. */
@Component({
  selector: 'bo-effective',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, DatePipe, ScopeSearch, MarketTypeSearch],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Effective configuration</h1><div class="sub">What applies at a point of the offer, and why</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field>
          <mat-label>Brand</mat-label>
          <mat-select [ngModel]="brand()" (ngModelChange)="brand.set($event)">
            <mat-option [value]="null">Any</mat-option>
            @for (b of brands(); track b.id) { <mat-option [value]="b">{{ b.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <bo-scope-search [(selected)]="node" />
        <bo-market-type-search [(selected)]="marketType" />
        <mat-form-field>
          <mat-label>Keys (filter)</mat-label>
          <input matInput [ngModel]="filter()" (ngModelChange)="filter.set($event)" placeholder="margin, cashout…" />
        </mat-form-field>
      </div>
    </section>
    <section class="panel">
      <table>
        <thead><tr><th>Setting</th><th>Value</th><th>Decided by</th></tr></thead>
        <tbody>
          @for (e of visible(); track e.key) {
            <tr class="clickable" (click)="toggle(e.key)" [attr.data-key]="e.key">
              <td class="mono">{{ e.key }}</td>
              <td><b>{{ format(e) }}</b></td>
              <td>
                @if (e.winner; as w) {
                  {{ level(w.scopeType) }}{{ w.scopeId !== null ? ' #' + w.scopeId : '' }}{{ w.marketTypeId !== null ? ' · market type ' + w.marketTypeId : '' }}
                  <span class="muted">· change set #{{ w.changeSetId }}</span>
                } @else { <span class="muted">default</span> }
                @if (e.trace.length > 1) { <span class="chip">{{ e.trace.length }} candidates</span> }
              </td>
            </tr>
            @if (open() === e.key) {
              <tr><td colspan="3">
                <div class="trace">
                  @for (t of e.trace; track t.row.id) {
                    <div [class.loser]="!t.winner">
                      <mat-icon>{{ t.winner ? 'check_circle' : 'remove_circle_outline' }}</mat-icon>
                      {{ level(t.row.scopeType) }}{{ t.row.scopeId !== null ? ' #' + t.row.scopeId : '' }}{{ t.row.marketTypeId !== null ? ' · market type ' + t.row.marketTypeId : '' }}
                      = <b>{{ formatRow(e.key, t.row.value) }}</b>
                      <span class="muted">specificity {{ t.specificity }} · set {{ t.row.updatedAt | date: 'short' }} · change set #{{ t.row.changeSetId }}</span>
                    </div>
                  }
                  <div class="muted">Default: {{ formatRow(e.key, defs().get(e.key)?.default) }} · combine: {{ defs().get(e.key)?.combine }}</div>
                </div>
              </td></tr>
            }
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .45rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.clickable { cursor: pointer; } tr.clickable:hover { background: var(--fo-surface-alt); }
    .trace div { display: flex; align-items: center; gap: .4rem; padding: .2rem 0; }
    .trace mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--fo-success); }
    .trace .loser { text-decoration: line-through; color: var(--fo-muted); }
    .trace .loser mat-icon { color: var(--fo-muted); }
  `,
})
export class EffectivePage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  protected readonly brands = signal<Brand[]>([]);
  protected readonly brand = signal<Brand | null>(null);
  protected readonly node = signal<ScopeOption | null>(null);
  protected readonly marketType = signal<MarketType | null>(null);
  protected readonly filter = signal('');
  protected readonly rows = signal<EffectiveSetting[]>([]);
  protected readonly defs = signal<Map<string, SettingDef>>(new Map());
  protected readonly open = signal<string | null>(null);
  protected readonly visible = computed(() => {
    const q = this.filter().toLowerCase();
    return this.rows().filter((r) => !q || r.key.includes(q));
  });

  constructor() {
    this.api.brands().subscribe((b) => this.brands.set(b));
    this.api.settingDefs().subscribe((d) => this.defs.set(new Map(d.map((x) => [x.key, x]))));
    effect(() => {
      const n = this.node();
      const query: Record<string, number | null> = {
        brandId: this.brand()?.id ?? null,
        marketTypeId: this.marketType()?.id ?? null,
        sportId: n?.type === 'sport' ? n.id : null,
        categoryId: n?.type === 'category' ? n.id : null,
        tournamentId: n?.type === 'tournament' ? n.id : null,
        eventId: n?.type === 'event' ? n.id : null,
      };
      this.api.effective(query).subscribe({ next: (r) => this.rows.set(r), error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }) });
    });
  }

  protected level(t: string): string {
    return LEVEL[t] ?? t;
  }

  protected toggle(key: string): void {
    this.open.set(this.open() === key ? null : key);
  }

  protected format(e: EffectiveSetting): string {
    return formatValue(this.defs().get(e.key), e.value);
  }

  protected formatRow(key: string, value: unknown): string {
    return formatValue(this.defs().get(key), value);
  }
}
