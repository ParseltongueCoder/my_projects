import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { StatusTag } from '@admin/ui';
import { BehaviorSubject, combineLatest, debounceTime, switchMap } from 'rxjs';
import { FeedOpsApi, MessageFilter } from '../../core/feed-ops-api';
import { LiveChanges } from '../../core/live-changes';
import { FeedMessage, PagedResult } from '../../core/models';
import { MessagePanel } from './message-panel';

const TYPES = ['odds_change', 'bet_stop', 'bet_settlement', 'rollback_bet_settlement', 'bet_cancel', 'rollback_bet_cancel', 'fixture_change'];
const STATUSES = ['processed', 'failed', 'skipped_duplicate', 'received'];

@Component({
  selector: 'app-messages',
  imports: [MatTableModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatIconModule,
            MatSlideToggleModule, FormsModule, StatusTag, DatePipe, DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './messages.html',
})
export class MessagesPage implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly live = inject(LiveChanges);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly panel = inject(MessagePanel);

  protected readonly types = TYPES;
  protected readonly statuses = STATUSES;
  protected readonly columns = ['received', 'type', 'event', 'producer', 'request', 'status', 'lag'];
  protected readonly result = signal<PagedResult<FeedMessage> | null>(null);
  protected type: string | null = null;
  protected status: string | null = null;
  protected eventUrn = '';
  /** Follow new messages while on page 1. */
  protected follow = true;
  private readonly filter$ = new BehaviorSubject<MessageFilter>({ page: 1, pageSize: 50 });

  ngOnInit(): void {
    const qp = this.route.snapshot.queryParamMap;
    this.status = qp.get('status');
    this.eventUrn = qp.get('eventUrn') ?? '';
    this.applyFilter();

    combineLatest([this.filter$, this.live.refreshOn(() => this.follow && (this.filter$.value.page ?? 1) === 1, 1500)])
      .pipe(debounceTime(50), switchMap(([f]) => this.api.messages(f)), takeUntilDestroyed(this.destroyRef))
      .subscribe((r) => this.result.set(r));
  }

  protected applyFilter(): void {
    this.filter$.next({ ...this.filter$.value, type: this.type, status: this.status, eventUrn: this.eventUrn.trim() || null, page: 1 });
  }

  protected page(delta: number): void {
    this.filter$.next({ ...this.filter$.value, page: Math.max(1, (this.filter$.value.page ?? 1) + delta) });
  }
}
