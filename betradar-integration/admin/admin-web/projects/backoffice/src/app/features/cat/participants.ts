import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { Participant, TranslationEdit } from '../../core/models';
import { UserSession } from '../../core/session';
import { MediaPicker } from '../../shared/media-picker';

/** Teams / competitors: name and short name in the content language, logo (docs/06 §2.5.5). */
@Component({
  selector: 'bo-participants',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MediaPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Participants</h1><div class="sub">Names in {{ lang.lang() }} and logos; empty = feed name</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <span class="muted">{{ total() }} participants</span>
      </div>
      <table>
        <thead><tr><th>Logo</th><th>Feed name</th><th>Name ({{ lang.lang() }})</th><th>Short name</th><th>Country</th><th class="num">Events</th><th></th></tr></thead>
        <tbody>
          @for (p of items(); track p.id) {
            <tr [attr.data-participant]="p.id">
              <td><bo-media-picker entityType="competitor" [entityId]="p.id" role="logo" [media]="p.media" [disabled]="!canEdit()" (changed)="load()" /></td>
              <td>{{ p.feedName }}</td>
              <td><input class="cell" [placeholder]="p.nameSource === 'operator' ? '' : p.name" [value]="p.nameSource === 'operator' ? p.name : ''"
                         [disabled]="!canEdit()" (change)="edit(p, 'name', $event)" /></td>
              <td><input class="cell short" maxlength="12" [value]="p.shortName ?? ''" [disabled]="!canEdit()" (change)="edit(p, 'short_name', $event)" /></td>
              <td>{{ p.countryCode }}</td>
              <td class="num">{{ p.events }}</td>
              <td>@if (dirty().has(p.id)) { <mat-icon class="dirty">edit_note</mat-icon> }</td>
            </tr>
          }
        </tbody>
      </table>
      @if (pending().length) {
        <div class="draft-bar"><b>{{ pending().length }} unsaved</b><span class="spacer"></span>
          <button matButton (click)="discard()">Discard</button>
          <button matButton="filled" (click)="save()">Save</button></div>
      }
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .35rem .5rem; border-bottom: 1px solid var(--fo-border); }
    th { color: var(--fo-muted); font-weight: 500; }
    .cell { font: inherit; width: 100%; box-sizing: border-box; padding: .3rem .4rem; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--fo-text); }
    .cell:hover, .cell:focus { border-color: var(--fo-border); background: var(--fo-surface); outline: none; }
    .cell.short { max-width: 9rem; }
    .dirty { color: var(--fo-warn); }
    .spacer { flex: 1; }
  `,
})
export class ParticipantsPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly lang = inject(ContentLanguage);
  protected readonly q = signal('');
  protected readonly items = signal<Participant[]>([]);
  protected readonly total = signal(0);
  protected readonly pending = signal<TranslationEdit[]>([]);
  protected readonly dirty = signal<Set<number>>(new Set());

  constructor() {
    this.lang.load();
    effect(() => {
      this.lang.lang();
      this.q();
      this.load();
    });
  }

  protected canEdit(): boolean {
    return this.session.has('i18n.edit') && this.session.has('cat.edit') && !this.session.me()?.readOnly;
  }

  protected edit(p: Participant, field: 'name' | 'short_name', event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    const item: TranslationEdit = { entityType: 'competitor', entityId: String(p.id), field, lang: this.lang.lang(), text: text || null, platform: false };
    this.pending.update((list) => [...list.filter((x) => !(x.entityId === item.entityId && x.field === field)), item]);
    this.dirty.update((s) => new Set(s).add(p.id));
  }

  protected discard(): void {
    this.pending.set([]);
    this.dirty.set(new Set());
    this.load();
  }

  protected async save(): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.saveTranslations(this.pending()));
      this.snack.open(`${r.saved} saved`, 'OK', { duration: 3000 });
      this.discard();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  load(): void {
    this.api.participants({ q: this.q(), lang: this.lang.lang() }).subscribe((r) => (this.items.set(r.items), this.total.set(r.total)));
  }
}
