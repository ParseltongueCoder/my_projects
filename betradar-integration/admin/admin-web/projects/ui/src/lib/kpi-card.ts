import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** A single number with a label; `tone` colours the value (e.g. failed messages > 0). */
@Component({
  selector: 'ui-kpi-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="kpi" [class]="'kpi tone-' + tone()">
      <div class="kpi-label">{{ label() }}</div>
      <div class="kpi-value">{{ value() ?? '—' }}</div>
      @if (hint()) {
        <div class="kpi-hint">{{ hint() }}</div>
      }
    </div>
  `,
  styles: `
    .kpi { border: 1px solid var(--fo-border); border-radius: 12px; padding: 1rem 1.25rem;
           background: var(--fo-surface); min-width: 10rem; }
    .kpi-label { color: var(--fo-muted); font-size: .85rem; }
    .kpi-value { font-size: 1.9rem; font-weight: 600; margin-top: .25rem; font-variant-numeric: tabular-nums; }
    .kpi-hint { color: var(--fo-muted); font-size: .75rem; margin-top: .25rem; }
    .tone-danger .kpi-value { color: var(--fo-danger); }
    .tone-warn .kpi-value { color: var(--fo-warn); }
    .tone-success .kpi-value { color: var(--fo-success); }
  `,
})
export class KpiCard {
  readonly label = input.required<string>();
  readonly value = input<string | number | null>();
  readonly hint = input<string>();
  readonly tone = input<'neutral' | 'success' | 'warn' | 'danger'>('neutral');
}
