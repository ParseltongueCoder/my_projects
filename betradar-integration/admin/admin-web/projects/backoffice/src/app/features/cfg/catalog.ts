import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { BoApi } from '../../core/bo-api';
import { SettingDef } from '../../core/models';
import { formatValue } from '../../shared/setting-value';

/** Every setting the platform knows: type, default, levels it can be set on. */
@Component({
  selector: 'bo-settings-catalog',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Settings catalog</h1><div class="sub">{{ defs().length }} settings across modules</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <mat-form-field>
          <mat-label>Module</mat-label>
          <mat-select [ngModel]="module()" (ngModelChange)="module.set($event)">
            <mat-option value="">All</mat-option>
            @for (m of modules(); track m) { <mat-option [value]="m">{{ m }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      <table>
        <thead><tr><th>Key</th><th>Module</th><th>Type</th><th>Default</th><th>Levels</th><th>Notes</th></tr></thead>
        <tbody>
          @for (d of visible(); track d.key) {
            <tr>
              <td><div class="mono">{{ d.key }}</div><div class="muted small">{{ d.description }}</div></td>
              <td>{{ d.module }}</td>
              <td>{{ d.type }}{{ d.enumValues ? ': ' + d.enumValues.join(' | ') : '' }}</td>
              <td>{{ fmt(d) }}</td>
              <td>@for (s of d.allowedScopes; track s) { <span class="chip">{{ s }}</span> }@if (d.allowsMarketType) { <span class="chip primary">+ market type</span> }</td>
              <td>
                @if (d.requiresApproval) { <span class="chip warn">four-eyes</span> }
                @if (!d.operatorEditable) { <span class="chip">platform managed</span> }
                @if (d.combine !== 'override') { <span class="chip">{{ d.combine }}</span> }
                @if (d.customerCombine !== 'none') { <span class="chip">customer: {{ d.customerCombine }}</span> }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); vertical-align: top; }
    th { color: var(--fo-muted); font-weight: 500; }
    .small { font-size: .8rem; }
  `,
})
export class CatalogPage {
  protected readonly defs = signal<SettingDef[]>([]);
  protected readonly q = signal('');
  protected readonly module = signal('');
  protected readonly modules = computed(() => [...new Set(this.defs().map((d) => d.module))].sort());
  protected readonly visible = computed(() => {
    const q = this.q().toLowerCase();
    return this.defs().filter((d) => (!this.module() || d.module === this.module()) && (!q || d.key.includes(q) || d.description.toLowerCase().includes(q)));
  });

  constructor() {
    inject(BoApi).settingDefs().subscribe((d) => this.defs.set(d));
  }

  protected fmt(d: SettingDef): string {
    return formatValue(d, d.default);
  }
}
