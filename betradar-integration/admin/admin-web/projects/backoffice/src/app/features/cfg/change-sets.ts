import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ChangeSet, SettingDef } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';
import { formatValue } from '../../shared/setting-value';

/** Change sets with their diffs; pending ones are approved or rejected by a second user (four-eyes). */
@Component({
  selector: 'bo-change-sets',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatIconModule, DatePipe, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Change sets</h1><div class="sub">Every configuration change, who made it, who approved it</div></div>
      <mat-button-toggle-group [value]="status()" (change)="status.set($event.value)" hideSingleSelectionIndicator>
        <mat-button-toggle value="pending_approval">Pending</mat-button-toggle>
        <mat-button-toggle value="">All</mat-button-toggle>
      </mat-button-toggle-group>
    </div>
    @for (c of sets(); track c.id) {
      <section class="panel" [attr.data-change-set]="c.id">
        <div class="head">
          <div>
            <b>#{{ c.id }} {{ c.title }}</b> <ui-status-tag [status]="c.status" />
            @if (c.operatorId === null) { <span class="chip warn">platform-wide</span> }
            <div class="muted">by {{ c.createdByName }} · {{ c.createdAt | date: 'medium' }}
              @if (c.decidedByName) { · {{ c.status === 'rejected' ? 'rejected' : 'approved' }} by {{ c.decidedByName }} {{ c.decidedAt | date: 'short' }} }
              @if (c.configVersion) { · config v{{ c.configVersion }} }
            </div>
            @if (c.decisionComment) { <div class="muted">“{{ c.decisionComment }}”</div> }
          </div>
          @if (c.status === 'pending_approval' && session.has('cfg.approve') && !session.me()?.readOnly) {
            <div class="toolbar-actions">
              @if (c.createdBy === session.me()?.user?.id) {
                <span class="muted">Needs another approver</span>
              } @else {
                <button matButton (click)="decide(c, false)">Reject</button>
                <button matButton="filled" (click)="decide(c, true)"><mat-icon>check</mat-icon>Approve</button>
              }
            </div>
          }
        </div>
        <table>
          <thead><tr><th>Setting</th><th>Where</th><th>Before</th><th>After</th></tr></thead>
          <tbody>
            @for (ch of c.changes; track $index) {
              <tr>
                <td class="mono">{{ ch.key }}</td>
                <td>{{ ch.scopeType }}{{ ch.scopeId !== null ? ' #' + ch.scopeId : '' }}{{ ch.marketTypeId !== null ? ' · market type ' + ch.marketTypeId : '' }}</td>
                <td class="muted">{{ fmt(ch.key, ch.oldValue) }}</td>
                <td><b>{{ ch.op === 'delete' ? '(reset to inherited)' : fmt(ch.key, ch.newValue) }}</b></td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    } @empty {
      <div class="panel empty">{{ status() ? 'Nothing waiting for approval' : 'No change sets yet' }}</div>
    }
  `,
  styles: `
    .head { display: flex; justify-content: space-between; gap: 1rem; flex-wrap: wrap; margin-bottom: .5rem; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .35rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; font-size: .85rem; }
  `,
})
export class ChangeSetsPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly status = signal('pending_approval');
  protected readonly sets = signal<ChangeSet[]>([]);
  private defs = new Map<string, SettingDef>();

  constructor() {
    this.api.settingDefs().subscribe((d) => (this.defs = new Map(d.map((x) => [x.key, x]))));
    effect(() => this.load(this.status()));
  }

  protected fmt(key: string, v: unknown): string {
    return formatValue(this.defs.get(key), v);
  }

  protected async decide(c: ChangeSet, approve: boolean): Promise<void> {
    const comment = await askReason(this.dialog, {
      title: `${approve ? 'Approve' : 'Reject'} change set #${c.id}`,
      message: c.title,
      confirm: approve ? 'Approve and apply' : 'Reject',
      danger: !approve,
      minLength: approve ? 0 : 5,
    });
    if (comment === null) {
      return;
    }
    try {
      const updated = await firstValueFrom(this.api.decideChangeSet(c.id, approve, comment));
      this.snack.open(`Change set #${c.id} ${updated.status}`, 'OK', { duration: 4000 });
      this.load(this.status());
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  private load(status: string): void {
    this.api.changeSets(status || undefined).subscribe({ next: (s) => this.sets.set(s), error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }) });
  }
}
