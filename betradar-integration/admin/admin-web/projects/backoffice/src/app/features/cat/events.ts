import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { CatalogEvent, EventDetail } from '../../core/models';
import { UserSession } from '../../core/session';

/** ISO string → value for <input type="datetime-local"> in the browser's time zone, and back. */
function toLocal(iso: string | null): string {
  if (!iso) {
    return '';
  }
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fromLocal(value: string): string | null {
  return value ? new Date(value).toISOString() : null;
}

/** Events of the feed with this operator's display overrides (featured, display start time) (docs/06 §2.5.3). */
@Component({
  selector: 'bo-catalog-events',
  imports: [DatePipe, FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSlideToggleModule, RouterLink, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Events</h1><div class="sub">Featured events and display overrides; betting always follows the feed</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search team, URN or id</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <mat-form-field>
          <mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)">
            <mat-option value="">Any</mat-option>
            @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-slide-toggle [ngModel]="featuredOnly()" (ngModelChange)="featuredOnly.set($event)">Featured only</mat-slide-toggle>
        @if (tournamentId()) { <span class="chip primary">league #{{ tournamentId() }} <a class="link" routerLink="/cat/events">✕</a></span> }
      </div>
      <table>
        <thead><tr><th>Start</th><th>Event</th><th>League</th><th>Status</th><th class="num">Open markets</th><th></th></tr></thead>
        <tbody>
          @for (e of items(); track e.id) {
            <tr class="clickable" [class.selected]="detail()?.event?.id === e.id" (click)="open(e)" [attr.data-event]="e.id">
              <td>{{ (e.displayStartAt ?? e.scheduledAt) | date: 'short' }} @if (e.displayStartAt) { <span class="chip warn">display</span> }</td>
              <td><b>{{ e.name }}</b> @if (e.isFeatured) { <mat-icon class="star">star</mat-icon> } <div class="muted mono small">{{ e.urn }}</div></td>
              <td>{{ e.tournamentName }} <span class="muted">· {{ e.sportName }}</span></td>
              <td><ui-status-tag [status]="e.status" /></td>
              <td class="num">{{ e.openMarkets }}</td>
              <td><mat-icon>chevron_right</mat-icon></td>
            </tr>
          } @empty { <tr><td colspan="6" class="empty">No events</td></tr> }
        </tbody>
      </table>
      <div class="pager"><span class="muted">{{ total() }} events</span>
        <button matIconButton [disabled]="page() === 1" (click)="page.set(page() - 1)" aria-label="Previous"><mat-icon>chevron_left</mat-icon></button>
        <span>{{ page() }}</span>
        <button matIconButton [disabled]="page() * 50 >= total()" (click)="page.set(page() + 1)" aria-label="Next"><mat-icon>chevron_right</mat-icon></button>
      </div>
    </section>

    @if (detail(); as d) {
      <section class="panel" data-event-detail>
        <h2>{{ d.event.name }}</h2>
        <div class="muted">{{ d.event.sportName }} · {{ d.event.tournamentName }} · feed start {{ d.event.scheduledAt | date: 'medium' }} · {{ d.event.urn }}</div>
        <div class="grid-2" style="margin-top: .75rem">
          <div>
            <div class="dialog-form">
              <mat-slide-toggle [(ngModel)]="form.isFeatured" [disabled]="!canEdit()">Featured</mat-slide-toggle>
              <div class="filters">
                <mat-form-field><mat-label>Featured from</mat-label><input matInput type="datetime-local" [(ngModel)]="form.featuredFrom" [disabled]="!canEdit()" /></mat-form-field>
                <mat-form-field><mat-label>Featured until</mat-label><input matInput type="datetime-local" [(ngModel)]="form.featuredTo" [disabled]="!canEdit()" /></mat-form-field>
                <mat-form-field><mat-label>Order</mat-label><input matInput type="number" [(ngModel)]="form.featuredOrder" [disabled]="!canEdit()" /></mat-form-field>
              </div>
              <mat-form-field>
                <mat-label>Display start time (shown to players only)</mat-label>
                <input matInput type="datetime-local" [(ngModel)]="form.displayStartAt" [disabled]="!canEdit()" />
                <mat-hint>Betting closes by the feed's start time and status, never by this.</mat-hint>
              </mat-form-field>
              <mat-form-field><mat-label>Internal note</mat-label><input matInput [(ngModel)]="form.note" [disabled]="!canEdit()" /></mat-form-field>
              @if (canEdit()) { <div><button matButton="filled" (click)="save(d)">Save</button></div> }
            </div>
          </div>
          <div>
            <div class="muted">Competitors</div>
            @for (c of d.competitors; track c.id) { <div>{{ c.position }}. <b>{{ c.name }}</b> <span class="muted">{{ c.qualifier }}</span></div> }
            <div class="muted" style="margin-top: .75rem">Offer policy here (configuration)</div>
            @for (p of policyKeys; track p) {
              <div><span class="mono">{{ p }}</span> = <b>{{ d.policy[p] ? 'on' : 'off' }}</b></div>
            }
            <a class="link" routerLink="/cfg/scope" [queryParams]="{ scope: 'event' }">Change in settings →</a>
          </div>
        </div>
      </section>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.clickable { cursor: pointer; } tr.clickable:hover, tr.selected { background: var(--fo-surface-alt); }
    .star { color: var(--fo-warn); font-size: 18px; width: 18px; height: 18px; vertical-align: middle; }
    .small { font-size: .75rem; }
  `,
})
export class CatalogEventsPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  private readonly lang = inject(ContentLanguage);
  readonly tournamentId = input<string | undefined>();

  protected readonly statuses = ['not_started', 'live', 'suspended', 'ended', 'closed', 'cancelled', 'postponed'];
  protected readonly q = signal('');
  protected readonly status = signal('');
  protected readonly featuredOnly = signal(false);
  protected readonly page = signal(1);
  protected readonly items = signal<CatalogEvent[]>([]);
  protected readonly total = signal(0);
  protected readonly detail = signal<EventDetail | null>(null);
  protected readonly policyKeys = ['offer.visible', 'offer.prematch_enabled', 'offer.live_enabled'];
  protected form = { isFeatured: false, featuredFrom: '', featuredTo: '', featuredOrder: null as number | null, displayStartAt: '', note: '', version: null as number | null };

  constructor() {
    this.lang.load();
    effect(() => {
      const query = {
        q: this.q(), status: this.status(), featured: this.featuredOnly() ? true : null, tournamentId: this.tournamentId(),
        page: this.page(), lang: this.lang.lang(),
      };
      this.api.events(query).subscribe({
        next: (r) => (this.items.set(r.items), this.total.set(r.total)),
        error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
      });
    });
  }

  protected canEdit(): boolean {
    return this.session.has('cat.edit') && !this.session.me()?.readOnly;
  }

  protected open(e: CatalogEvent): void {
    this.api.event(e.id, this.lang.lang()).subscribe((d) => {
      const o = d.override;
      this.form = {
        isFeatured: o?.isFeatured ?? false, featuredFrom: toLocal(o?.featuredFrom ?? null), featuredTo: toLocal(o?.featuredTo ?? null),
        featuredOrder: o?.featuredOrder ?? null, displayStartAt: toLocal(o?.displayStartAt ?? null), note: o?.note ?? '', version: o?.version ?? null,
      };
      this.detail.set(d);
    });
  }

  protected async save(d: EventDetail): Promise<void> {
    try {
      await firstValueFrom(this.api.saveEventOverride(d.event.id, {
        isFeatured: this.form.isFeatured, featuredFrom: fromLocal(this.form.featuredFrom), featuredTo: fromLocal(this.form.featuredTo),
        featuredOrder: this.form.featuredOrder, displayStartAt: fromLocal(this.form.displayStartAt), note: this.form.note || null,
        version: this.form.version ?? undefined,
      }));
      this.snack.open('Saved', 'OK', { duration: 3000 });
      this.page.set(this.page()); // refresh list
      this.items.update((items) => items.map((i) => (i.id === d.event.id ? { ...i, isFeatured: this.form.isFeatured, displayStartAt: fromLocal(this.form.displayStartAt) } : i)));
      this.open(d.event);
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }
}
