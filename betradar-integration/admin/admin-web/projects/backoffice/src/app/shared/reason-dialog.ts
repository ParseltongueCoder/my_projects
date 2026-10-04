import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';

export interface ReasonDialogData {
  title: string;
  message?: string;
  confirm: string;
  danger?: boolean;
  /** Minimum reason length; 0 = optional comment. */
  minLength?: number;
}

/** Risky actions ask for a reason that goes into the audit log (docs/08 §1.6). */
@Component({
  selector: 'bo-reason-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      @if (data.message) { <p>{{ data.message }}</p> }
      <mat-form-field style="width: 100%">
        <mat-label>{{ min > 0 ? 'Reason (audit log)' : 'Comment (optional)' }}</mat-label>
        <textarea matInput rows="3" [ngModel]="reason()" (ngModelChange)="reason.set($event)" cdkFocusInitial></textarea>
        @if (min > 0) { <mat-hint>At least {{ min }} characters</mat-hint> }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [class.danger]="data.danger" [disabled]="reason().trim().length < min" (click)="ref.close(reason().trim())">
        {{ data.confirm }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `.danger { --mat-button-filled-container-color: var(--fo-danger); }`,
})
export class ReasonDialog {
  protected readonly data = inject<ReasonDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<ReasonDialog, string>);
  protected readonly reason = signal('');
  protected readonly min = this.data.minLength ?? 5;
}

/** Opens the reason dialog; resolves to the reason, or null when cancelled. */
export async function askReason(dialog: MatDialog, data: ReasonDialogData): Promise<string | null> {
  const ref = dialog.open<ReasonDialog, ReasonDialogData, string>(ReasonDialog, { data, width: '32rem' });
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
