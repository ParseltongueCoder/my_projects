import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { BoApi } from '../../core/bo-api';
import { AuditEntry } from '../../core/models';

/** Append-only audit log (docs/08 §3.5): who did what, before/after, and why. */
@Component({
  selector: 'bo-audit',
  imports: [DatePipe, JsonPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Audit log</h1><div class="sub">Every change, kept append-only</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Action starts with</mat-label><input matInput [(ngModel)]="action" placeholder="cfg., adm.user" (keyup.enter)="reload()" /></mat-form-field>
        <mat-form-field><mat-label>Entity type</mat-label><input matInput [(ngModel)]="entityType" (keyup.enter)="reload()" /></mat-form-field>
        <mat-form-field><mat-label>Entity id</mat-label><input matInput [(ngModel)]="entityId" (keyup.enter)="reload()" /></mat-form-field>
        <button matButton="tonal" (click)="reload()"><mat-icon>search</mat-icon>Search</button>
      </div>
      <table>
        <thead><tr><th>Time</th><th>Who</th><th>Action</th><th>Entity</th><th>Reason</th></tr></thead>
        <tbody>
          @for (a of items(); track a.id) {
            <tr class="clickable" (click)="open.set(open() === a.id ? null : a.id)">
              <td class="muted">{{ a.ts | date: 'medium' }}</td>
              <td>{{ a.actorName }} <span class="chip">{{ a.actorType }}</span></td>
              <td class="mono">{{ a.action }}</td>
              <td>{{ a.entityType }} <span class="mono">{{ a.entityId }}</span></td>
              <td>{{ a.reason }}</td>
            </tr>
            @if (open() === a.id) {
              <tr><td colspan="5"><div class="grid-2">
                <div><div class="muted">Before</div><pre class="json">{{ a.before | json }}</pre></div>
                <div><div class="muted">After</div><pre class="json">{{ a.after | json }}</pre></div>
              </div></td></tr>
            }
          } @empty { <tr><td colspan="5" class="empty">No entries</td></tr> }
        </tbody>
      </table>
      @if (next()) { <div class="pager"><button matButton (click)="more()">Older entries</button></div> }
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); vertical-align: top; }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.clickable { cursor: pointer; } tr.clickable:hover { background: var(--fo-surface-alt); }
  `,
})
export class AuditPage {
  private readonly api = inject(BoApi);
  protected readonly items = signal<AuditEntry[]>([]);
  protected readonly next = signal<string | null>(null);
  protected readonly open = signal<number | null>(null);
  protected action = '';
  protected entityType = '';
  protected entityId = '';

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.fetch(null, false);
  }

  protected more(): void {
    this.fetch(this.next(), true);
  }

  private fetch(cursor: string | null, append: boolean): void {
    this.api.audit({ action: this.action, entityType: this.entityType, entityId: this.entityId, cursor, limit: 50 }).subscribe((p) => {
      this.items.set(append ? [...this.items(), ...p.items] : p.items);
      this.next.set(p.nextCursor);
    });
  }
}
