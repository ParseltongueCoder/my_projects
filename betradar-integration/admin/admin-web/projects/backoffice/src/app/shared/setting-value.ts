import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Json, SettingDef } from '../core/models';

/** One-line text of a setting value: money as "GEL 5,000 · USD 1,500", lists comma-separated. */
export function formatValue(def: SettingDef | undefined, value: Json): string {
  if (value === null || value === undefined) {
    return '—';
  }
  if (def?.type === 'money' && typeof value === 'object') {
    return Object.entries(value as Record<string, number>)
      .map(([c, a]) => `${c} ${Number(a).toLocaleString('en-US')}`)
      .join(' · ');
  }
  if (def?.type === 'decimal' && typeof value === 'number' && /(^|[._])pct$/.test(def.key)) {
    return `${value} (${+(value * 100).toFixed(2)}%)`;
  }
  if (Array.isArray(value)) {
    return value.length ? value.join(', ') : '(empty)';
  }
  if (typeof value === 'boolean') {
    return value ? 'on' : 'off';
  }
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

/** Typed editor for a setting value (bool, number, enum, money per currency, list, JSON). Emits null while invalid. */
@Component({
  selector: 'bo-setting-value',
  imports: [NgTemplateOutlet, FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (def().type) {
      @case ('bool') {
        <mat-slide-toggle [ngModel]="bool()" (ngModelChange)="bool.set($event); emit()">{{ bool() ? 'On' : 'Off' }}</mat-slide-toggle>
      }
      @case ('enum') {
        <mat-form-field>
          <mat-label>Value</mat-label>
          <mat-select [ngModel]="text()" (ngModelChange)="text.set($event); emit()">
            @for (v of def().enumValues ?? []; track v) { <mat-option [value]="v">{{ v }}</mat-option> }
          </mat-select>
        </mat-form-field>
      }
      @case ('money') {
        @for (c of currencyList(); track c) {
          <mat-form-field>
            <mat-label>{{ c }}</mat-label>
            <input matInput type="number" min="0" [ngModel]="money()[c]" (ngModelChange)="setMoney(c, $event)" />
          </mat-form-field>
        }
      }
      @case ('int') { <ng-container *ngTemplateOutlet="number" /> }
      @case ('decimal') { <ng-container *ngTemplateOutlet="number" /> }
      @case ('stringList') {
        <mat-form-field style="width: 100%">
          <mat-label>Values (comma separated)</mat-label>
          <input matInput [ngModel]="text()" (ngModelChange)="text.set($event); emit()" />
        </mat-form-field>
      }
      @case ('json') {
        <mat-form-field style="width: 100%">
          <mat-label>JSON</mat-label>
          <textarea matInput rows="4" class="mono" [ngModel]="text()" (ngModelChange)="text.set($event); emit()"></textarea>
          @if (error()) { <mat-error>{{ error() }}</mat-error> }
        </mat-form-field>
      }
      @default {
        <mat-form-field style="width: 100%">
          <mat-label>Value</mat-label>
          <input matInput [ngModel]="text()" (ngModelChange)="text.set($event); emit()" />
        </mat-form-field>
      }
    }
    <ng-template #number>
      <mat-form-field>
        <mat-label>Value</mat-label>
        <input matInput type="number" [attr.min]="def().min" [attr.max]="def().max" [ngModel]="num()" (ngModelChange)="num.set($event); emit()" />
        @if (def().min !== null || def().max !== null) { <mat-hint>{{ def().min ?? '−∞' }} … {{ def().max ?? '∞' }}</mat-hint> }
      </mat-form-field>
    </ng-template>
    @if (error() && def().type !== 'json') { <div class="warn-text">{{ error() }}</div> }
  `,
  styles: `:host { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; } mat-form-field { min-width: 9rem; }`,
})
export class SettingValueEditor {
  readonly def = input.required<SettingDef>();
  readonly initial = input<Json>(null);
  readonly currencies = input<string[]>(['GEL']);
  readonly valueChange = output<Json | null>();

  protected readonly bool = signal(false);
  protected readonly num = signal<number | null>(null);
  protected readonly text = signal('');
  protected readonly money = signal<Record<string, number | null>>({});
  protected readonly error = signal<string | null>(null);
  protected readonly currencyList = computed(() => {
    const fromValue = Object.keys(this.money());
    return [...new Set([...this.currencies(), ...fromValue])];
  });

  constructor() {
    effect(() => {
      const d = this.def();
      const v = this.initial() ?? d.default;
      this.bool.set(v === true);
      this.num.set(typeof v === 'number' ? v : null);
      this.money.set(d.type === 'money' && v && typeof v === 'object' ? { ...v } : {});
      this.text.set(
        d.type === 'stringList' ? ((v as string[] | null) ?? []).join(', ') : d.type === 'json' ? JSON.stringify(v ?? {}, null, 2) : v == null ? '' : String(v),
      );
      queueMicrotask(() => this.emit());
    });
  }

  protected setMoney(currency: string, amount: number | null): void {
    this.money.update((m) => ({ ...m, [currency]: amount }));
    this.emit();
  }

  protected emit(): void {
    const value = this.current();
    this.valueChange.emit(value);
  }

  private current(): Json | null {
    const d = this.def();
    this.error.set(null);
    switch (d.type) {
      case 'bool':
        return this.bool();
      case 'int':
      case 'decimal': {
        const n = this.num();
        if (n === null || Number.isNaN(Number(n))) {
          return this.fail('Enter a number');
        }
        if (d.type === 'int' && !Number.isInteger(Number(n))) {
          return this.fail('Whole numbers only');
        }
        if ((d.min !== null && n < d.min) || (d.max !== null && n > d.max)) {
          return this.fail(`Allowed range ${d.min ?? '−∞'} … ${d.max ?? '∞'}`);
        }
        return Number(n);
      }
      case 'money': {
        const entries = Object.entries(this.money()).filter(([, a]) => a !== null && (a as unknown) !== '');
        if (!entries.length) {
          return this.fail('Enter at least one amount');
        }
        return Object.fromEntries(entries.map(([c, a]) => [c, Number(a)]));
      }
      case 'stringList':
        return this.text().split(',').map((s) => s.trim()).filter((s) => s);
      case 'json':
        try {
          return JSON.parse(this.text());
        } catch {
          return this.fail('Not valid JSON');
        }
      default:
        return this.text().trim() ? this.text().trim() : this.fail('Enter a value');
    }
  }

  private fail(message: string): null {
    this.error.set(message);
    return null;
  }
}
