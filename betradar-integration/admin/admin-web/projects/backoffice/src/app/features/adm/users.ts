import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { StatusTag } from '@admin/ui';
import { BoApi, problemMessage } from '../../core/bo-api';
import { AdminUser, Role } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';

interface UserDialogData {
  roles: Role[];
  user?: AdminUser;
}

interface UserDialogResult {
  username: string;
  email: string;
  displayName: string;
  roles: string[];
  reason: string;
}

@Component({
  selector: 'bo-user-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ data.user ? 'Roles of ' + (data.user.displayName ?? data.user.username) : 'Invite user' }}</h2>
    <mat-dialog-content>
      <div class="dialog-form">
        @if (!data.user) {
          <mat-form-field><mat-label>Username</mat-label><input matInput [(ngModel)]="v.username" name="username" /></mat-form-field>
          <mat-form-field><mat-label>Email</mat-label><input matInput type="email" [(ngModel)]="v.email" name="email" /></mat-form-field>
          <mat-form-field><mat-label>Display name</mat-label><input matInput [(ngModel)]="v.displayName" name="displayName" /></mat-form-field>
        }
        <mat-form-field>
          <mat-label>Roles</mat-label>
          <mat-select multiple [(ngModel)]="v.roles" name="roles">
            @for (r of data.roles; track r.id) { <mat-option [value]="r.code">{{ r.name }} <span class="muted">({{ r.permissions.length }} permissions)</span></mat-option> }
          </mat-select>
        </mat-form-field>
        @if (data.user) {
          <mat-form-field><mat-label>Reason (audit log)</mat-label><input matInput [(ngModel)]="v.reason" name="reason" /></mat-form-field>
        } @else {
          <p class="muted">The user gets a temporary password and must set a new one and enrol two-factor authentication at first login.</p>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="!valid()" (click)="ref.close(v)">{{ data.user ? 'Save roles' : 'Invite' }}</button>
    </mat-dialog-actions>
  `,
})
export class UserDialog {
  protected readonly data = inject<UserDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<UserDialog, UserDialogResult>);
  protected readonly v: UserDialogResult = {
    username: '', email: '', displayName: '', roles: this.data.user ? [...this.data.user.roles] : [], reason: '',
  };

  protected valid(): boolean {
    return this.data.user
      ? this.v.reason.trim().length >= 5
      : this.v.username.trim().length >= 3 && this.v.email.includes('@') && this.v.roles.length > 0;
  }
}

/** Back-office users of the operator in context (or platform staff at platform level), their roles and status. */
@Component({
  selector: 'bo-users',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatDialogModule, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Users</h1><div class="sub">{{ session.operator()?.name ?? 'Platform staff' }}</div></div>
      @if (canEdit()) { <button matButton="filled" (click)="invite()"><mat-icon>person_add</mat-icon>Invite user</button> }
    </div>
    @if (password(); as p) {
      <div class="panel notice">
        <mat-icon>key</mat-icon>
        <div>Temporary password for <b>{{ p.user }}</b>: <span class="mono">{{ p.password }}</span>
          <div class="muted">Shown once. Hand it over securely; it must be changed at first login.</div></div>
        <button matButton (click)="password.set(null)">Done</button>
      </div>
    }
    <section class="panel">
      <table>
        <thead><tr><th>User</th><th>Roles</th><th>Status</th><th>Last login</th><th></th></tr></thead>
        <tbody>
          @for (u of users(); track u.id) {
            <tr [attr.data-user]="u.username">
              <td><b>{{ u.displayName ?? u.username }}</b><div class="muted">{{ u.username }} · {{ u.email }}</div></td>
              <td>@for (r of u.roles; track r) { <span class="chip primary">{{ r }}</span> }</td>
              <td><ui-status-tag [status]="u.status" /></td>
              <td class="muted">{{ u.lastLoginAt ? (u.lastLoginAt | date: 'short') : 'never' }}</td>
              <td class="actions">
                @if (canEdit() && u.id !== session.me()?.user?.id) {
                  <button matButton (click)="editRoles(u)">Roles</button>
                  @if (u.status === 'disabled') { <button matButton (click)="setEnabled(u, true)">Enable</button> }
                  @else { <button matButton (click)="setEnabled(u, false)">Disable</button> }
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
    .notice { display: flex; gap: .75rem; align-items: center; background: var(--fo-warn-bg); }
    .notice > div { flex: 1; }
  `,
})
export class UsersPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly users = signal<AdminUser[]>([]);
  protected readonly roles = signal<Role[]>([]);
  protected readonly password = signal<{ user: string; password: string } | null>(null);

  constructor() {
    this.load();
    this.api.roles().subscribe((r) => this.roles.set(r));
  }

  protected canEdit(): boolean {
    return this.session.has('adm.user.edit') && !this.session.me()?.readOnly;
  }

  protected async invite(): Promise<void> {
    const v = await firstValueFrom(this.dialog.open(UserDialog, { data: { roles: this.roles() }, width: '32rem' }).afterClosed());
    if (!v) {
      return;
    }
    await this.run(async () => {
      const r = await firstValueFrom(this.api.createUser({ username: v.username, email: v.email, displayName: v.displayName, roles: v.roles }));
      if (r.temporaryPassword) {
        this.password.set({ user: r.user.username, password: r.temporaryPassword });
      }
      return `Invited ${r.user.username}`;
    });
  }

  protected async editRoles(u: AdminUser): Promise<void> {
    const v = await firstValueFrom(this.dialog.open(UserDialog, { data: { roles: this.roles(), user: u }, width: '32rem' }).afterClosed());
    if (v) {
      await this.run(async () => {
        await firstValueFrom(this.api.setRoles(u.id, v.roles, v.reason));
        return `Roles of ${u.username} saved`;
      });
    }
  }

  protected async setEnabled(u: AdminUser, enabled: boolean): Promise<void> {
    const reason = await askReason(this.dialog, {
      title: `${enabled ? 'Enable' : 'Disable'} ${u.displayName ?? u.username}`,
      message: enabled ? undefined : 'The user is logged out and can no longer sign in.',
      confirm: enabled ? 'Enable' : 'Disable',
      danger: !enabled,
    });
    if (reason) {
      await this.run(async () => {
        await firstValueFrom(this.api.setUserEnabled(u.id, enabled, reason));
        return `${u.username} ${enabled ? 'enabled' : 'disabled'}`;
      });
    }
  }

  private async run(action: () => Promise<string>): Promise<void> {
    try {
      this.snack.open(await action(), 'OK', { duration: 4000 });
      this.load();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  private load(): void {
    this.api.users().subscribe((u) => this.users.set(u));
  }
}
