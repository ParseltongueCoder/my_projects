import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, Injectable, OnInit, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { formatXml, StatusTag } from '@admin/ui';
import { FeedOpsApi } from '../../core/feed-ops-api';
import { FeedMessageDetail } from '../../core/models';

/** Opens one archived feed message (with its raw XML) in a right-hand side panel. */
@Injectable({ providedIn: 'root' })
export class MessagePanel {
  private readonly dialog = inject(MatDialog);

  open(messageId: number): void {
    this.dialog.open(MessagePanelComponent, {
      data: messageId,
      position: { right: '0', top: '0' },
      height: '100vh',
      width: 'min(48rem, 100vw)',
      maxWidth: '100vw',
      panelClass: 'side-panel',
      autoFocus: false,
    });
  }
}

@Component({
  selector: 'app-message-panel',
  imports: [MatDialogModule, MatButtonModule, MatIconModule, StatusTag, DatePipe, DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <h2 mat-dialog-title>Feed message</h2>
      <button matIconButton mat-dialog-close aria-label="Close"><mat-icon>close</mat-icon></button>
    </div>
    <mat-dialog-content>
      @if (detail(); as d) {
        <dl class="facts">
          <dt>Type</dt><dd class="mono">{{ d.message.type }}</dd>
          <dt>Status</dt><dd><ui-status-tag [status]="d.message.status" /></dd>
          @if (d.message.error) { <dt>Error</dt><dd class="error">{{ d.message.error }}</dd> }
          <dt>Event</dt>
          <dd>
            @if (d.message.eventId) {
              <a class="link mono" [routerLink]="['/events', d.message.eventId]" (click)="ref.close()">{{ d.message.eventUrn }}</a>
            } @else { <span class="mono">{{ d.message.eventUrn }}</span> }
          </dd>
          <dt>Producer</dt><dd>{{ d.message.producerId }}</dd>
          <dt>Request id</dt><dd class="mono">{{ d.message.requestId ?? '–' }}</dd>
          <dt>Feed time</dt><dd>{{ d.message.feedTs | date: 'dd MMM HH:mm:ss.SSS' }}</dd>
          <dt>Received</dt>
          <dd>{{ d.message.receivedAt | date: 'dd MMM HH:mm:ss.SSS' }}
            @if (d.message.lagMs !== null) { <span class="muted">({{ d.message.lagMs | number: '1.0-0' }} ms)</span> }</dd>
        </dl>
        <div class="xml-head">
          <b>Raw XML</b>
          <button matButton (click)="copy(d.payload)"><mat-icon>content_copy</mat-icon>Copy</button>
        </div>
        <pre class="xml mono">{{ pretty() }}</pre>
      } @else {
        <div class="empty">Loading…</div>
      }
    </mat-dialog-content>
  `,
  styles: `
    .head { display: flex; align-items: center; justify-content: space-between; padding-right: .5rem; }
    .facts { display: grid; grid-template-columns: 8rem 1fr; gap: .4rem 1rem; margin: 0 0 1rem; }
    dt { color: var(--fo-muted); } dd { margin: 0; }
    .error { color: var(--fo-danger); }
    .xml-head { display: flex; justify-content: space-between; align-items: center; }
    .xml { background: #14171d; color: #e6e9ef; padding: 1rem; border-radius: 8px; overflow: auto; white-space: pre; margin: 0; }
    mat-dialog-content { max-height: calc(100vh - 5rem) !important; }
  `,
})
export class MessagePanelComponent implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly snack = inject(MatSnackBar);
  private readonly id = inject<number>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<MessagePanelComponent>);
  protected readonly detail = signal<FeedMessageDetail | null>(null);
  protected readonly pretty = computed(() => formatXml(this.detail()?.payload ?? ''));

  ngOnInit(): void {
    this.api.message(this.id).subscribe((d) => this.detail.set(d));
  }

  protected copy(xml: string): void {
    void navigator.clipboard?.writeText(xml).then(() => this.snack.open('Copied', undefined, { duration: 1500 }));
  }
}
