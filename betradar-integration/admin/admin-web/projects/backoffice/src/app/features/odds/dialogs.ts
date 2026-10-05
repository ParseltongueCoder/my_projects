import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { BoApi } from '../../core/bo-api';
import { MarketOffer, MarketTemplate, MarketType, OutcomeOffer } from '../../core/models';
import { fmtOdds, fmtPct } from './odds-format';

// ------------------------------------------------------------------ odds override

export interface OverrideDialogData {
  market: MarketOffer;
  outcome: OutcomeOffer;
  live: boolean;
}

export interface OverrideDialogResult {
  kind: 'absolute' | 'shift_pct';
  value: number;
  ttlMinutes: number;
  clearOn: 'expiry' | 'feed_change';
  reason: string;
}

/** Manual price for one outcome (docs/06 §4.3): always with a TTL and a reason. */
@Component({
  selector: 'bo-override-dialog',
  imports: [MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Override {{ data.outcome.name }}</h2>
    <mat-dialog-content class="dialog-form">
      <div class="muted">{{ data.market.name }} · feed {{ odds(data.outcome.feedOdds) }} · offered {{ odds(data.outcome.odds) }}</div>
      <mat-button-toggle-group [ngModel]="kind()" (ngModelChange)="kind.set($event)" aria-label="Kind">
        <mat-button-toggle value="absolute">Fixed odds</mat-button-toggle>
        <mat-button-toggle value="shift_pct">Shift %</mat-button-toggle>
      </mat-button-toggle-group>
      @if (kind() === 'absolute') {
        <mat-form-field><mat-label>Odds</mat-label><input matInput type="number" step="0.01" min="1.01" [ngModel]="value()" (ngModelChange)="value.set($event)" data-override-odds /></mat-form-field>
      } @else {
        <mat-form-field>
          <mat-label>Shift of the offered price, %</mat-label>
          <input matInput type="number" step="1" min="-50" max="50" [ngModel]="shift()" (ngModelChange)="shift.set($event)" />
          <mat-hint>-5 = 5% lower than the priced odds</mat-hint>
        </mat-form-field>
      }
      @if (kind() === 'absolute' && driftText()) { <div class="warn-text">{{ driftText() }}</div> }
      <mat-form-field>
        <mat-label>Valid for (minutes)</mat-label>
        <input matInput type="number" min="1" [ngModel]="ttl()" (ngModelChange)="ttl.set($event)" data-override-ttl />
        <mat-hint>{{ data.live ? 'Live: at most 120 min by default' : 'Prematch: at most 24 h by default' }} (odds.override_max_ttl_min)</mat-hint>
      </mat-form-field>
      <mat-slide-toggle [ngModel]="feedChange()" (ngModelChange)="feedChange.set($event)">End early when the feed price moves (odds.override_feed_tolerance_pct)</mat-slide-toggle>
      <mat-form-field><mat-label>Reason (audit log)</mat-label><textarea matInput rows="2" [ngModel]="reason()" (ngModelChange)="reason.set($event)" data-override-reason></textarea></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="!valid()" (click)="close()" data-override-save>Set override</button>
    </mat-dialog-actions>
  `,
})
export class OverrideDialog {
  protected readonly data = inject<OverrideDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<OverrideDialog, OverrideDialogResult>);
  protected readonly kind = signal<'absolute' | 'shift_pct'>('absolute');
  protected readonly value = signal<number | null>(this.data.outcome.odds ?? this.data.outcome.feedOdds);
  protected readonly shift = signal<number | null>(-5);
  protected readonly ttl = signal<number>(this.data.live ? 15 : 60);
  protected readonly feedChange = signal(false);
  protected readonly reason = signal('');
  protected readonly odds = fmtOdds;

  protected readonly driftText = computed(() => {
    const feed = this.data.outcome.feedOdds;
    const v = this.value();
    if (!feed || !v) {
      return '';
    }
    const d = (v - feed) / feed;
    return Math.abs(d) > this.data.market.settings.overrideFeedTolerancePct ? `${fmtPct(d, 1)} from the feed price` : '';
  });

  protected readonly valid = computed(() =>
    this.reason().trim().length >= 3 && this.ttl() >= 1
    && (this.kind() === 'absolute' ? (this.value() ?? 0) >= 1.01 : this.shift() !== null && this.shift() !== 0 && Math.abs(this.shift()!) <= 50));

  protected close(): void {
    this.ref.close({
      kind: this.kind(),
      value: this.kind() === 'absolute' ? Number(this.value()) : Number(this.shift()) / 100,
      ttlMinutes: Number(this.ttl()),
      clearOn: this.feedChange() ? 'feed_change' : 'expiry',
      reason: this.reason().trim(),
    });
  }
}

// ------------------------------------------------------------------ suspend / close

export interface TradingDialogData {
  title: string;
  action: 'suspend' | 'close';
  canPlatform: boolean;
}

export interface TradingDialogResult {
  ttlMinutes: number | null;
  reason: string;
  platform: boolean;
}

@Component({
  selector: 'bo-trading-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content class="dialog-form">
      <p class="muted">
        {{ data.action === 'suspend' ? 'Prices stay visible, no bets are accepted.' : 'Removed from the offer; placed bets settle as usual.' }}
      </p>
      <mat-form-field>
        <mat-label>Until</mat-label>
        <mat-select [ngModel]="ttl()" (ngModelChange)="ttl.set($event)">
          <mat-option [value]="null">Lifted by hand</mat-option>
          @for (m of ttls; track m) { <mat-option [value]="m">{{ m < 60 ? m + ' min' : m / 60 + ' h' }}</mat-option> }
        </mat-select>
      </mat-form-field>
      @if (data.canPlatform) {
        <mat-slide-toggle [ngModel]="platform()" (ngModelChange)="platform.set($event)">For every operator (platform incident)</mat-slide-toggle>
      }
      <mat-form-field><mat-label>Reason (audit log)</mat-label><textarea matInput rows="2" [ngModel]="reason()" (ngModelChange)="reason.set($event)" data-trading-reason></textarea></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="reason().trim().length < 3" (click)="close()" data-trading-save>{{ data.action === 'suspend' ? 'Suspend' : 'Close' }}</button>
    </mat-dialog-actions>
  `,
})
export class TradingDialog {
  protected readonly data = inject<TradingDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<TradingDialog, TradingDialogResult>);
  protected readonly ttls = [5, 15, 30, 60, 240, 1440];
  protected readonly ttl = signal<number | null>(null);
  protected readonly platform = signal(false);
  protected readonly reason = signal('');

  protected close(): void {
    this.ref.close({ ttlMinutes: this.ttl(), reason: this.reason().trim(), platform: this.platform() });
  }
}

// ------------------------------------------------------------------ manual market

export interface ManualMarketResult {
  marketTypeId: number;
  specifiers: string;
  outcomes: { code: string; odds: number | null }[];
  status: string;
  reason: string;
}

/** A manual market from a feed market type (docs/06 §4.6): pick the type, its specifiers, price the outcomes. */
@Component({
  selector: 'bo-manual-market-dialog',
  imports: [MatDialogModule, MatButtonModule, MatAutocompleteModule, MatFormFieldModule, MatInputModule, MatSelectModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Add manual market</h2>
    <mat-dialog-content class="dialog-form">
      <mat-form-field>
        <mat-label>Market type (template)</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="find($event)" [matAutocomplete]="auto" data-manual-type />
        <mat-autocomplete #auto="matAutocomplete" (optionSelected)="pick($event.option.value)">
          @for (t of options(); track t.id) { <mat-option [value]="t">{{ t.name }} <span class="muted">#{{ t.id }}</span></mat-option> }
        </mat-autocomplete>
      </mat-form-field>
      @if (template(); as t) {
        @if (t.isVariant) { <div class="warn-text">Variant market types cannot be used as templates yet.</div> }
        @for (s of t.specifiers; track s.name) {
          <mat-form-field>
            <mat-label>{{ s.name }} ({{ s.type }})</mat-label>
            <input matInput [ngModel]="specs()[s.name] ?? ''" (ngModelChange)="setSpec(s.name, $event)" [attr.data-spec]="s.name" />
          </mat-form-field>
        }
        <table>
          <thead><tr><th>Outcome</th><th>Odds</th></tr></thead>
          <tbody>
            @for (o of t.outcomes; track o.code) {
              <tr>
                <td>{{ o.name }} <span class="muted mono">{{ o.code }}</span></td>
                <td><input class="odds-input" type="number" step="0.01" min="1.01" [ngModel]="prices()[o.code] ?? null"
                           (ngModelChange)="setPrice(o.code, $event)" [attr.data-price]="o.code" /></td>
              </tr>
            }
          </tbody>
        </table>
        <div class="muted">Booksum {{ booksum() }} · leave a price empty to leave the outcome out</div>
        <mat-form-field>
          <mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)">
            <mat-option value="active">Open for bets</mat-option>
            <mat-option value="suspended">Suspended (publish later)</mat-option>
          </mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>Reason (audit log)</mat-label><input matInput [ngModel]="reason()" (ngModelChange)="reason.set($event)" data-manual-reason /></mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="!valid()" (click)="close()" data-manual-save>Create</button>
    </mat-dialog-actions>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .25rem .4rem; border-bottom: 1px solid var(--fo-border); }
    .odds-input { width: 6rem; font: inherit; padding: .2rem .3rem; }
  `,
})
export class ManualMarketDialog {
  protected readonly data = inject<{ lang: string }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ManualMarketDialog, ManualMarketResult>);
  private readonly api = inject(BoApi);
  protected readonly search = signal('');
  protected readonly options = signal<MarketType[]>([]);
  protected readonly template = signal<MarketTemplate | null>(null);
  protected readonly specs = signal<Record<string, string>>({});
  protected readonly prices = signal<Record<string, number | null>>({});
  protected readonly status = signal('active');
  protected readonly reason = signal('');

  protected readonly booksum = computed(() => {
    const values = Object.values(this.prices()).filter((v): v is number => !!v && v > 1);
    return values.length ? fmtPct(values.reduce((s, v) => s + 1 / v, 0), 1) : '—';
  });

  protected readonly valid = computed(() => {
    const t = this.template();
    return !!t && !t.isVariant && t.specifiers.every((s) => (this.specs()[s.name] ?? '').trim().length > 0)
      && Object.values(this.prices()).some((v) => !!v && v >= 1.01) && this.reason().trim().length >= 3;
  });

  protected find(q: string | MarketType): void {
    if (typeof q !== 'string') {
      return;
    }
    this.search.set(q);
    this.api.marketTypes(q).subscribe((list) => this.options.set(list));
  }

  protected pick(t: MarketType): void {
    this.search.set(t.name);
    this.api.marketTemplate(t.id, this.data.lang).subscribe((template) => {
      this.template.set(template);
      this.specs.set({});
      this.prices.set({});
    });
  }

  protected setSpec(name: string, value: string): void {
    this.specs.update((s) => ({ ...s, [name]: value }));
  }

  protected setPrice(code: string, value: number | null): void {
    this.prices.update((p) => ({ ...p, [code]: value }));
  }

  protected close(): void {
    const t = this.template()!;
    this.ref.close({
      marketTypeId: t.id,
      specifiers: t.specifiers.map((s) => `${s.name}=${this.specs()[s.name].trim()}`).join('|'),
      outcomes: Object.entries(this.prices()).filter(([, v]) => !!v && v >= 1.01).map(([code, odds]) => ({ code, odds })),
      status: this.status(),
      reason: this.reason().trim(),
    });
  }
}
