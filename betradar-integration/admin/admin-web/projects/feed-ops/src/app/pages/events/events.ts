import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { StatusTag } from '@admin/ui';
import { BehaviorSubject, combineLatest, debounceTime, switchMap } from 'rxjs';
import { EventFilter, FeedOpsApi } from '../../core/feed-ops-api';
import { LiveChanges } from '../../core/live-changes';
import { EventSummary, PagedResult } from '../../core/models';

const STATUSES = ['live', 'not_started', 'suspended', 'ended', 'closed', 'cancelled', 'postponed', 'abandoned'];

@Component({
  selector: 'app-events',
  imports: [MatTableModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatIconModule,
            MatProgressBarModule, FormsModule, StatusTag, DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './events.html',
})
export class EventsPage implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly live = inject(LiveChanges);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statuses = STATUSES;
  protected readonly columns = ['kickoff', 'event', 'tournament', 'status', 'score', 'markets', 'lastFeed'];
  protected readonly result = signal<PagedResult<EventSummary> | null>(null);
  protected readonly loading = signal(true);
  protected status: string | null = null;
  protected q = '';
  private readonly filter$ = new BehaviorSubject<EventFilter>({ page: 1, pageSize: 50 });

  ngOnInit(): void {
    combineLatest([this.filter$, this.live.refreshOn((c) => c.table !== 'producer_status', 1000)])
      .pipe(
        debounceTime(50),
        switchMap(([f]) => {
          this.loading.set(true);
          return this.api.events(f);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((r) => {
        this.result.set(r);
        this.loading.set(false);
      });
  }

  protected applyFilter(): void {
    this.filter$.next({ ...this.filter$.value, status: this.status, q: this.q.trim() || null, page: 1 });
  }

  protected page(delta: number): void {
    this.filter$.next({ ...this.filter$.value, page: Math.max(1, (this.filter$.value.page ?? 1) + delta) });
  }

  protected open(e: EventSummary): void {
    void this.router.navigate(['/events', e.id]);
  }
}
