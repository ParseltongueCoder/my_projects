import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Operator } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';

@Component({
  selector: 'bo-operator-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>New operator</h2>
    <mat-dialog-content>
      <div class="dialog-form">
        <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="v.code" placeholder="acmebet" />
          <mat-hint>Also the Keycloak organization alias</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="v.name" /></mat-form-field>
        <mat-form-field><mat-label>Base currency</mat-label><input matInput [(ngModel)]="v.baseCurrency" maxlength="3" /></mat-form-field>
        <mat-form-field><mat-label>Currencies (comma separated)</mat-label><input matInput [(ngModel)]="v.currencies" /></mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="!v.code || !v.name" (click)="ref.close(v)">Create</button>
    </mat-dialog-actions>
  `,
})
export class OperatorDialog {
  protected readonly ref = inject(MatDialogRef<OperatorDialog>);
  protected readonly v = { code: '', name: '', baseCurrency: 'GEL', currencies: 'GEL' };
}

/** Tenants of the platform (platform staff only). Creating one also creates its Keycloak organization and default brand. */
@Component({
  selector: 'bo-operators',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatMenuModule, MatDialogModule, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Operators</h1><div class="sub">Tenants of the platform</div></div>
      @if (session.has('platform.operator.manage')) { <button matButton="filled" (click)="create()"><mat-icon>add_business</mat-icon>New operator</button> }
    </div>
    <section class="panel">
      <table>
        <thead><tr><th>Operator</th><th>Status</th><th>Currencies</th><th>Languages</th><th>Since</th><th></th></tr></thead>
        <tbody>
          @for (o of operators(); track o.id) {
            <tr [attr.data-operator]="o.code">
              <td><b>{{ o.name }}</b> <span class="muted mono">{{ o.code }}</span></td>
              <td><ui-status-tag [status]="o.status" /></td>
              <td>{{ o.baseCurrency }} @for (c of o.currencies; track c) { @if (c !== o.baseCurrency) { <span class="chip">{{ c }}</span> } }</td>
              <td>{{ o.languages.join(', ') }}</td>
              <td class="muted">{{ o.createdAt | date: 'mediumDate' }}</td>
              <td class="actions">
                <button matButton (click)="actAs(o)"><mat-icon>login</mat-icon>Open</button>
                @if (session.has('platform.operator.manage')) {
                  <button matIconButton [matMenuTriggerFor]="menu" aria-label="Status"><mat-icon>more_vert</mat-icon></button>
                  <mat-menu #menu>
                    @for (s of statuses; track s) { @if (s !== o.status) { <button mat-menu-item (click)="setStatus(o, s)">Set {{ s }}</button> } }
                  </mat-menu>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .45rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    .actions { text-align: right; white-space: nowrap; }
  `,
})
export class OperatorsPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);
  protected readonly session = inject(UserSession);
  protected readonly operators = signal<Operator[]>([]);
  protected readonly statuses = ['onboarding', 'active', 'suspended', 'terminated'];

  constructor() {
    this.load();
  }

  protected async create(): Promise<void> {
    const v = await firstValueFrom(this.dialog.open(OperatorDialog, { width: '30rem' }).afterClosed());
    if (!v) {
      return;
    }
    try {
      const op = await firstValueFrom(this.api.createOperator({
        code: v.code, name: v.name, baseCurrency: v.baseCurrency.toUpperCase(),
        currencies: v.currencies.split(',').map((c: string) => c.trim().toUpperCase()).filter((c: string) => c),
      }));
      this.snack.open(`Operator ${op.name} created`, 'OK', { duration: 4000 });
      this.load();
      await this.session.load();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  protected async setStatus(o: Operator, status: string): Promise<void> {
    const reason = await askReason(this.dialog, { title: `Set ${o.name} to ${status}`, confirm: 'Change status', danger: status !== 'active' });
    if (!reason) {
      return;
    }
    try {
      await firstValueFrom(this.api.updateOperator(o.id, { status, reason }));
      this.load();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  protected async actAs(o: Operator): Promise<void> {
    await this.session.switchOperator(o.id);
    await this.router.navigateByUrl('/sites');
  }

  private load(): void {
    this.api.operators().subscribe((o) => this.operators.set(o));
  }
}
