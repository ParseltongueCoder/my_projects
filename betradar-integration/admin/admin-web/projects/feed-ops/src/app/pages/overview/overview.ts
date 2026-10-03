import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { KpiCard, StatusTag } from '@admin/ui';
import { switchMap } from 'rxjs';
import { FeedOpsApi } from '../../core/feed-ops-api';
import { LiveChanges } from '../../core/live-changes';
import { FeedMessage, Overview } from '../../core/models';
import { MessagePanel } from '../messages/message-panel';

@Component({
  selector: 'app-overview',
  imports: [KpiCard, StatusTag, MatTableModule, DatePipe, DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview.html',
  styles: `
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); gap: .75rem; margin-bottom: 1rem; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
    @media (max-width: 960px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class OverviewPage implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly live = inject(LiveChanges);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly panel = inject(MessagePanel);

  protected readonly overview = signal<Overview | null>(null);
  protected readonly failed = signal<FeedMessage[]>([]);
  protected readonly producerColumns = ['name', 'state', 'last'];
  protected readonly failedColumns = ['received', 'type', 'error'];

  ngOnInit(): void {
    this.live
      .refreshOn(() => true, 1000)
      .pipe(switchMap(() => this.api.overview()), takeUntilDestroyed(this.destroyRef))
      .subscribe((o) => this.overview.set(o));
    this.live
      .refreshOn(() => true, 3000)
      .pipe(switchMap(() => this.api.messages({ status: 'failed', pageSize: 5 })), takeUntilDestroyed(this.destroyRef))
      .subscribe((p) => this.failed.set(p.items));
  }
}
