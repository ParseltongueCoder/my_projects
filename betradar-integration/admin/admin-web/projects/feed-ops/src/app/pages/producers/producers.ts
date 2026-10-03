import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatTableModule } from '@angular/material/table';
import { StatusTag } from '@admin/ui';
import { switchMap } from 'rxjs';
import { FeedOpsApi } from '../../core/feed-ops-api';
import { LiveChanges } from '../../core/live-changes';
import { Producer } from '../../core/models';

@Component({
  selector: 'app-producers',
  imports: [MatTableModule, StatusTag, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div>
        <h1>Producers</h1>
        <div class="sub">When a producer is down its markets are suspended until its recovery completes.</div>
      </div>
    </div>
    <section class="panel">
      <table mat-table [dataSource]="producers()">
        <ng-container matColumnDef="id"><th mat-header-cell *matHeaderCellDef>Id</th><td mat-cell *matCellDef="let p">{{ p.id }}</td></ng-container>
        <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th><td mat-cell *matCellDef="let p"><b>{{ p.name }}</b></td></ng-container>
        <ng-container matColumnDef="state"><th mat-header-cell *matHeaderCellDef>State</th><td mat-cell *matCellDef="let p"><ui-status-tag [status]="p.state" /></td></ng-container>
        <ng-container matColumnDef="reason"><th mat-header-cell *matHeaderCellDef>Reason</th><td mat-cell *matCellDef="let p" class="muted">{{ p.downReason ?? '–' }}</td></ng-container>
        <ng-container matColumnDef="processed"><th mat-header-cell *matHeaderCellDef>Last processed feed time</th><td mat-cell *matCellDef="let p">{{ p.lastProcessedFeedTs | date: 'dd MMM HH:mm:ss' }}</td></ng-container>
        <ng-container matColumnDef="changed"><th mat-header-cell *matHeaderCellDef>Changed</th><td mat-cell *matCellDef="let p">{{ p.updatedAt | date: 'dd MMM HH:mm:ss' }}</td></ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-row" *matNoDataRow><td class="empty" [attr.colspan]="columns.length">No producer has reported yet</td></tr>
      </table>
    </section>
  `,
})
export class ProducersPage implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly live = inject(LiveChanges);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly producers = signal<Producer[]>([]);
  protected readonly columns = ['id', 'name', 'state', 'reason', 'processed', 'changed'];

  ngOnInit(): void {
    this.live
      .refreshOn((c) => c.table === 'producer_status', 200)
      .pipe(switchMap(() => this.api.producers()), takeUntilDestroyed(this.destroyRef))
      .subscribe((p) => this.producers.set(p));
  }
}
