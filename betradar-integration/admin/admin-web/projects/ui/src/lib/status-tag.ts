import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { humanize, severityOf } from './status';

/** Coloured pill for any canonical status (market, event, producer, message, settlement). */
@Component({
  selector: 'ui-status-tag',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="tag" [class]="'tag tag-' + severity()">{{ label() }}</span>`,
  styles: `
    .tag { display: inline-block; padding: .1rem .55rem; border-radius: 999px; font-size: .78rem; font-weight: 500;
           line-height: 1.4; white-space: nowrap; text-transform: lowercase; }
    .tag-success { color: var(--fo-success); background: var(--fo-success-bg); }
    .tag-info { color: var(--fo-info); background: var(--fo-info-bg); }
    .tag-warn { color: var(--fo-warn); background: var(--fo-warn-bg); }
    .tag-danger { color: var(--fo-danger); background: var(--fo-danger-bg); }
    .tag-secondary { color: var(--fo-neutral); background: var(--fo-neutral-bg); }
  `,
})
export class StatusTag {
  readonly status = input.required<string | null | undefined>();
  protected readonly label = computed(() => humanize(this.status()));
  protected readonly severity = computed(() => severityOf(this.status()));
}
