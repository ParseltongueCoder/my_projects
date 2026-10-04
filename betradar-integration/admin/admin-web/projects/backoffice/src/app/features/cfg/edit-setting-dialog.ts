import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { Json, SettingDef } from '../../core/models';
import { formatValue, SettingValueEditor } from '../../shared/setting-value';

export interface EditSettingData {
  def: SettingDef;
  current: Json;
  currencies: string[];
  where: string;
}

@Component({
  selector: 'bo-edit-setting',
  imports: [MatDialogModule, MatButtonModule, SettingValueEditor],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title class="mono">{{ data.def.key }}</h2>
    <mat-dialog-content>
      <p>{{ data.def.description }}</p>
      <p class="muted">{{ data.where }} · default {{ defaultText }}</p>
      <bo-setting-value [def]="data.def" [initial]="data.current" [currencies]="data.currencies" (valueChange)="value.set($event)" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="value() === null" (click)="ref.close(value())">Stage change</button>
    </mat-dialog-actions>
  `,
})
export class EditSettingDialog {
  protected readonly data = inject<EditSettingData>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<EditSettingDialog, Json>);
  protected readonly value = signal<Json | null>(null);
  protected readonly defaultText = formatValue(this.data.def, this.data.def.default);
}
