import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../core/bo-api';
import { MediaRef } from '../core/models';

/** Thumbnail of an entity's image with upload/replace; uploads then links the image for the current operator. */
@Component({
  selector: 'bo-media-picker',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="thumb" [class.empty]="!current()" [disabled]="disabled() || busy()" (click)="picker.click(); $event.stopPropagation()"
            [matTooltip]="current() ? (current()!.inherited ? 'Platform default ' + role() + ' — click to replace for this operator' : 'Replace ' + role()) : 'Upload ' + role()">
      @if (current(); as m) { <img [src]="m.url" [alt]="role()" /> } @else { <mat-icon>add_photo_alternate</mat-icon> }
    </button>
    <input #picker type="file" accept="image/png,image/jpeg,image/webp,image/svg+xml" hidden (change)="pick($event)" />
  `,
  styles: `
    .thumb { width: 28px; height: 28px; padding: 0; border: 1px dashed var(--fo-border); border-radius: 6px; background: transparent;
      display: inline-flex; align-items: center; justify-content: center; cursor: pointer; overflow: hidden; }
    .thumb:not(.empty) { border-style: solid; }
    .thumb img { width: 100%; height: 100%; object-fit: contain; }
    .thumb mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--fo-muted); }
  `,
})
export class MediaPicker {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  readonly entityType = input.required<string>();
  readonly entityId = input.required<number>();
  readonly role = input.required<string>();
  readonly media = input<MediaRef[]>([]);
  readonly disabled = input(false);
  readonly changed = output<void>();
  protected readonly busy = signal(false);

  protected current(): MediaRef | undefined {
    return this.media().find((m) => m.role === this.role());
  }

  protected async pick(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const f = input.files?.[0];
    input.value = '';
    if (!f) {
      return;
    }
    this.busy.set(true);
    try {
      const uploaded = await firstValueFrom(this.api.upload(f));
      await firstValueFrom(this.api.linkMedia(this.entityType(), this.entityId(), this.role(), uploaded.id));
      this.changed.emit();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 6000 });
    } finally {
      this.busy.set(false);
    }
  }
}
