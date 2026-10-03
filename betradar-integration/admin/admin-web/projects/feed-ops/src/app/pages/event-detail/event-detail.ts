import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { StatusTag } from '@admin/ui';
import { combineLatest, forkJoin, switchMap } from 'rxjs';
import { FeedOpsApi } from '../../core/feed-ops-api';
import { LiveChanges } from '../../core/live-changes';
import { BetStop, EventDetail, FeedMessage, Settlement } from '../../core/models';
import { MessagePanel } from '../messages/message-panel';

@Component({
  selector: 'app-event-detail',
  imports: [StatusTag, MatTableModule, MatTabsModule, MatTooltipModule, MatIconModule, DatePipe, DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './event-detail.html',
  styleUrl: './event-detail.scss',
})
export class EventDetailPage implements OnInit {
  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  private readonly api = inject(FeedOpsApi);
  private readonly live = inject(LiveChanges);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly detail = signal<EventDetail | null>(null);
  protected readonly settlements = signal<Settlement[]>([]);
  protected readonly betStops = signal<BetStop[]>([]);
  protected readonly messages = signal<FeedMessage[]>([]);
  protected readonly notFound = signal(false);
  protected readonly panel = inject(MessagePanel);
  protected readonly settlementColumns = ['time', 'market', 'outcome', 'result', 'certainty', 'state'];
  protected readonly betStopColumns = ['time', 'source', 'groups', 'target', 'markets', 'producer'];
  protected readonly messageColumns = ['received', 'type', 'producer', 'request', 'status', 'lag'];
  protected readonly divergent = computed(() => this.detail()?.markets.filter((m) => m.status !== m.feedStatus).length ?? 0);

  private readonly eventId = computed(() => Number(this.id()));
  // toObservable needs an injection context, so it is created with the component, not in ngOnInit.
  private readonly eventId$ = toObservable(this.eventId);

  ngOnInit(): void {
    combineLatest([this.eventId$, this.live.refreshOn()])
      .pipe(
        switchMap(([id]) =>
          forkJoin({
            detail: this.api.event(id),
            settlements: this.api.settlements(id),
            betStops: this.api.betStops(id),
          }),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ detail, settlements, betStops }) => {
          this.detail.set(detail);
          this.settlements.set(settlements);
          this.betStops.set(betStops);
          this.loadMessages(detail.event.urn);
        },
        error: () => this.notFound.set(true),
      });
  }

  private loadMessages(urn: string | null): void {
    if (urn) {
      this.api.messages({ eventUrn: urn, pageSize: 100 }).subscribe((p) => this.messages.set(p.items));
    }
  }

  protected outcomeTitle(o: { probability: number | null; code: string }): string {
    return `outcome ${o.code}` + (o.probability !== null ? ` · p=${(o.probability * 100).toFixed(1)}%` : '');
  }
}
