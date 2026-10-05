import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Brand, MessageCell, MessageRow, RenderedMessage } from '../../core/models';
import { UserSession } from '../../core/session';

const CATEGORIES = ['bet_reject', 'validation', 'cashout', 'referral', 'system'];

/**
 * CMS messages (docs/06 §6.4): what players read when a bet, a cash-out or a review fails. Texts per language,
 * for every brand or one brand; empty = inherited (platform text, then our default).
 */
@Component({
  selector: 'bo-cms-messages',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>Messages</h1><div class="sub">Texts players see for rejected bets, cash-out and reviews; the code and parameters always go with them</div></div></div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search code or text</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <mat-form-field>
          <mat-label>Category</mat-label>
          <mat-select [ngModel]="category()" (ngModelChange)="category.set($event)">
            <mat-option value="">All</mat-option>
            @for (c of categories; track c) { <mat-option [value]="c">{{ c }}</mat-option> }
          </mat-select>
        </mat-form-field>
        @if (brands().length > 1) {
          <mat-form-field>
            <mat-label>Brand</mat-label>
            <mat-select [ngModel]="brandId()" (ngModelChange)="brandId.set($event)" data-cms-brand>
              <mat-option [value]="null">Every brand</mat-option>
              @for (b of brands(); track b.id) { <mat-option [value]="b.id">{{ b.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
        <mat-slide-toggle [ngModel]="missing()" (ngModelChange)="missing.set($event)">Missing translations only</mat-slide-toggle>
      </div>
      <table>
        <thead><tr><th>Code</th>@for (l of languages(); track l) { <th>{{ l }}</th> }</tr></thead>
        <tbody>
          @for (m of items(); track m.code) {
            <tr class="clickable" [class.selected]="selected()?.code === m.code" (click)="open(m)" [attr.data-message]="m.code">
              <td><span class="mono"><b>{{ m.code }}</b></span><div class="muted small">{{ m.category }} · {{ m.module }}</div></td>
              @for (l of languages(); track l) {
                <td [class.missing]="m.texts[l].text.source === 'missing'">
                  {{ m.texts[l].text.effective }} <span class="chip" [class.primary]="own(m.texts[l].text)">{{ m.texts[l].text.source }}</span>
                </td>
              }
            </tr>
          } @empty { <tr><td [attr.colspan]="languages().length + 1" class="empty">No messages</td></tr> }
        </tbody>
      </table>
    </section>

    @if (selected(); as m) {
      <section class="panel" data-message-editor>
        <h2 class="mono">{{ m.code }}</h2>
        <div class="muted">{{ m.description }} · {{ m.severity }}</div>
        <div>Parameters: @for (p of m.params; track p) { <span class="chip mono">{{ '{' + p + '}' }}</span> } @empty { <span class="muted">none</span> }
          @if (m.params.length) { <span class="muted"> — numbers: {{ '{' + m.params[0] + ', number}' }}</span> }</div>
        <div class="muted">Editing: <b>{{ brandName() }}</b>. Leave a field empty to use the inherited text (shown as placeholder).</div>
        @for (l of languages(); track l) {
          <div class="lang">
            <div class="lang-code">{{ l }}</div>
            <mat-form-field class="title">
              <mat-label>Title</mat-label>
              <input matInput [placeholder]="inherited(m, l, 'title')" [ngModel]="draft()[l]?.title ?? ''" (ngModelChange)="edit(l, 'title', $event)" [disabled]="!canEdit()" />
            </mat-form-field>
            <mat-form-field class="text">
              <mat-label>Text</mat-label>
              <textarea matInput rows="2" [placeholder]="inherited(m, l, 'text')" [ngModel]="draft()[l]?.text ?? ''" (ngModelChange)="edit(l, 'text', $event)"
                        [disabled]="!canEdit()" [attr.data-message-text]="l"></textarea>
            </mat-form-field>
            <button matButton (click)="preview(m, l)" [attr.data-preview]="l">Preview</button>
          </div>
          @if (previews()[l]; as p) { <div class="preview" [attr.data-preview-result]="l"><b>{{ p.title }}</b> — {{ p.message }} <span class="muted">({{ p.source }}, {{ p.lang }})</span></div> }
        }
        @if (canEdit()) { <div><button matButton="filled" (click)="save(m)" data-message-save>Save</button></div> }
      </section>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--fo-border); vertical-align: top; }
    th { color: var(--fo-muted); font-weight: 500; }
    tr.clickable { cursor: pointer; } tr.clickable:hover, tr.selected { background: var(--fo-surface-alt); }
    td.missing { color: var(--fo-danger); }
    .small { font-size: .75rem; }
    .lang { display: flex; gap: .75rem; align-items: center; }
    .lang-code { width: 2rem; font-weight: 600; }
    .title { flex: 1; } .text { flex: 3; }
    .preview { margin: 0 0 1rem 2.75rem; padding: .4rem .6rem; background: var(--fo-surface-alt); border-radius: 6px; }
  `,
})
export class CmsMessagesPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  private readonly session = inject(UserSession);
  protected readonly categories = CATEGORIES;
  protected readonly q = signal('');
  protected readonly category = signal('');
  protected readonly missing = signal(false);
  protected readonly brandId = signal<number | null>(null);
  protected readonly brands = signal<Brand[]>([]);
  protected readonly languages = signal<string[]>([]);
  protected readonly items = signal<MessageRow[]>([]);
  protected readonly selected = signal<MessageRow | null>(null);
  protected readonly draft = signal<Record<string, { title: string; text: string }>>({});
  protected readonly previews = signal<Record<string, RenderedMessage>>({});

  constructor() {
    this.api.brands().subscribe((b) => this.brands.set(b));
    effect(() => this.load(this.q(), this.category(), this.brandId(), this.missing()));
  }

  private load(q: string, category: string, brandId: number | null, missing: boolean): void {
    this.api.messages({ q, category, brandId, missing: missing || null }).subscribe({
      next: (r) => {
        this.languages.set(r.languages);
        this.items.set(r.items);
        const open = this.selected();
        if (open) {
          const fresh = r.items.find((i) => i.code === open.code);
          if (fresh) {
            this.open(fresh);
          }
        }
      },
      error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
    });
  }

  protected canEdit(): boolean {
    return this.session.has('cms.edit') && !this.session.me()?.readOnly;
  }

  protected brandName(): string {
    const id = this.brandId();
    return id === null ? 'every brand of the operator' : (this.brands().find((b) => b.id === id)?.name ?? `brand #${id}`);
  }

  /** The operator wrote this text (for every brand or for the selected one). */
  protected own(cell: MessageCell): boolean {
    return cell.source === 'brand' || cell.source === 'operator';
  }

  /** The text at the level being edited (brand or operator-wide). */
  private mine(cell: MessageCell): string {
    return (this.brandId() === null ? cell.operator : cell.brand) ?? '';
  }

  protected inherited(m: MessageRow, lang: string, field: 'title' | 'text'): string {
    const c = m.texts[lang][field];
    return (this.brandId() === null ? c.platform : (c.operator ?? c.platform)) ?? c.default ?? '';
  }

  protected open(m: MessageRow): void {
    this.selected.set(m);
    this.previews.set({});
    this.draft.set(Object.fromEntries(Object.entries(m.texts).map(([l, t]) => [l, { title: this.mine(t.title), text: this.mine(t.text) }])));
  }

  protected edit(lang: string, field: 'title' | 'text', value: string): void {
    this.draft.update((d) => ({ ...d, [lang]: { ...(d[lang] ?? { title: '', text: '' }), [field]: value } }));
  }

  protected preview(m: MessageRow, lang: string): void {
    this.api.previewMessage(m.code, lang, this.brandId(), '').subscribe({
      next: (p) => this.previews.update((x) => ({ ...x, [lang]: p })),
      error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }),
    });
  }

  protected async save(m: MessageRow): Promise<void> {
    // Send only what changed; an emptied field removes the override.
    const texts: Record<string, { title?: string; text?: string }> = {};
    for (const [lang, d] of Object.entries(this.draft())) {
      const t = m.texts[lang];
      const change: { title?: string; text?: string } = {};
      if (d.title.trim() !== this.mine(t.title)) {
        change.title = d.title.trim();
      }
      if (d.text.trim() !== this.mine(t.text)) {
        change.text = d.text.trim();
      }
      if (Object.keys(change).length) {
        texts[lang] = change;
      }
    }
    if (!Object.keys(texts).length) {
      this.snack.open('Nothing changed', 'OK', { duration: 2000 });
      return;
    }
    try {
      await firstValueFrom(this.api.saveMessage(m.code, { texts, brandId: this.brandId(), platform: false }));
      this.snack.open('Saved', 'OK', { duration: 3000 });
      this.load(this.q(), this.category(), this.brandId(), this.missing());
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }
}
