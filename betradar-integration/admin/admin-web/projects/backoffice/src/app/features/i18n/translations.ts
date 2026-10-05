import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { I18nEntityType, TemplatePreview, TranslationEdit, TranslationRow } from '../../core/models';
import { UserSession } from '../../core/session';

const TYPES: { value: I18nEntityType; label: string }[] = [
  { value: 'sport', label: 'Sports' },
  { value: 'category', label: 'Countries' },
  { value: 'tournament', label: 'Leagues' },
  { value: 'competitor', label: 'Teams' },
  { value: 'market_type', label: 'Market names' },
  { value: 'outcome_type', label: 'Outcome names' },
];

/**
 * Translation grid (docs/06 §3): source text, then one column per language with this operator's text; the placeholder
 * shows what players see without it (platform or provider). Templates keep their {placeholders}; the API lints them.
 */
@Component({
  selector: 'bo-translations',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Translations</h1><div class="sub">Your texts override the platform's and the feed's</div></div>
      <div class="toolbar-actions">
        <button matButton (click)="export()"><mat-icon>download</mat-icon>Export CSV</button>
        @if (canImport()) {
          <button matButton (click)="file.click()"><mat-icon>upload</mat-icon>Import CSV</button>
          <input #file type="file" accept=".csv,text/csv" hidden (change)="importFile($event)" />
        }
      </div>
    </div>
    <section class="panel">
      <div class="filters">
        <mat-button-toggle-group [value]="type()" (change)="setType($event.value)" hideSingleSelectionIndicator>
          @for (t of types; track t.value) { <mat-button-toggle [value]="t.value">{{ t.label }}</mat-button-toggle> }
        </mat-button-toggle-group>
        <mat-form-field><mat-label>Search</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event); page.set(1)" /></mat-form-field>
        <mat-slide-toggle [ngModel]="missing()" (ngModelChange)="missing.set($event); page.set(1)">Missing only</mat-slide-toggle>
      </div>
      @if (importResult(); as r) {
        <div class="notice">
          <mat-icon>rule</mat-icon>
          <div>Import preview: <b>{{ r.changes }}</b> changes in {{ r.rows }} rows @if (r.errors.length) { · <span class="warn-text">{{ r.errors.length }} errors: {{ r.errors[0] }}</span> }</div>
          <button matButton (click)="importResult.set(null)">Cancel</button>
          <button matButton="filled" [disabled]="r.errors.length > 0 || r.changes === 0" (click)="applyImport()">Apply</button>
        </div>
      }
      <table>
        <thead><tr><th>Source</th>@for (l of langs(); track l) { <th>{{ l }}</th> }<th></th></tr></thead>
        <tbody>
          @for (r of rows(); track r.entityId) {
            <tr [attr.data-entity]="r.entityId">
              <td><div [class.mono]="isTemplate()">{{ r.source }}</div><div class="muted small">{{ r.context }}</div></td>
              @for (l of langs(); track l) {
                <td>
                  <input class="cell" [class.mono]="isTemplate()" [class.changed]="changed(r, l)" [disabled]="!canEdit()"
                         [value]="r.values[l].operator ?? ''" [placeholder]="r.values[l].platform ?? r.values[l].provider ?? ''"
                         [attr.aria-label]="r.source + ' ' + l" (change)="edit(r, l, $event)" (focus)="focusTemplate(r)" />
                </td>
              }
              <td>@if (isTemplate()) { <button matIconButton matTooltip="Preview" (click)="focusTemplate(r)"><mat-icon>visibility</mat-icon></button> }</td>
            </tr>
          } @empty { <tr><td [attr.colspan]="langs().length + 2" class="empty">Nothing here</td></tr> }
        </tbody>
      </table>
      <div class="pager"><span class="muted">{{ total() }}</span>
        <button matIconButton [disabled]="page() === 1" (click)="page.set(page() - 1)" aria-label="Previous"><mat-icon>chevron_left</mat-icon></button>
        <span>{{ page() }}</span>
        <button matIconButton [disabled]="page() * 50 >= total()" (click)="page.set(page() + 1)" aria-label="Next"><mat-icon>chevron_right</mat-icon></button>
      </div>
      @if (pending().size) {
        <div class="draft-bar"><b>{{ pending().size }} unsaved</b><span class="spacer"></span>
          <button matButton (click)="discard()">Discard</button>
          <button matButton="filled" (click)="save()">Save</button></div>
      }
    </section>

    @if (isTemplate() && previewMarket()) {
      <section class="panel" data-preview>
        <h2>Preview · market type #{{ previewMarket() }}</h2>
        <div class="filters">
          <mat-form-field><mat-label>Specifiers</mat-label><input matInput [ngModel]="specifiers()" (ngModelChange)="specifiers.set($event)" placeholder="total=2.5|hcp=-1.5" /></mat-form-field>
          <mat-form-field>
            <mat-label>Language</mat-label>
            <mat-select [ngModel]="previewLang()" (ngModelChange)="previewLang.set($event)">
              @for (l of langs(); track l) { <mat-option [value]="l">{{ l }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
        @if (preview(); as p) {
          <div class="preview-market"><b>{{ p.market.rendered }}</b> <span class="chip">{{ p.market.source }}</span></div>
          @for (o of p.outcomes; track o.code) {
            <div class="preview-outcome"><span class="mono muted">{{ o.code }}</span> {{ o.rendered }} <span class="chip">{{ o.source }}</span></div>
          }
          <p class="muted small">Unsaved edits are not in the preview; save first.</p>
        }
      </section>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .3rem .4rem; border-bottom: 1px solid var(--fo-border); vertical-align: top; }
    th { color: var(--fo-muted); font-weight: 500; }
    .cell { font: inherit; width: 100%; box-sizing: border-box; padding: .3rem .4rem; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--fo-text); }
    .cell:hover, .cell:focus { border-color: var(--fo-border); background: var(--fo-surface); outline: none; }
    .cell.changed { border-color: var(--fo-warn); background: var(--fo-warn-bg); }
    .small { font-size: .78rem; }
    .spacer { flex: 1; }
    .notice { display: flex; gap: .75rem; align-items: center; padding: .6rem; border-radius: 8px; background: var(--fo-info-bg); margin-bottom: .5rem; }
    .notice > div { flex: 1; }
    .preview-outcome { padding: .15rem 0; }
  `,
})
export class TranslationsPage {
  private readonly api = inject(BoApi);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  private readonly lang = inject(ContentLanguage);
  protected readonly types = TYPES;

  protected readonly type = signal<I18nEntityType>('tournament');
  protected readonly q = signal('');
  protected readonly missing = signal(false);
  protected readonly page = signal(1);
  protected readonly rows = signal<TranslationRow[]>([]);
  protected readonly total = signal(0);
  protected readonly pending = signal<Map<string, TranslationEdit>>(new Map());
  protected readonly importResult = signal<{ rows: number; changes: number; errors: string[]; csv: string } | null>(null);
  protected readonly previewMarket = signal<number | null>(null);
  protected readonly specifiers = signal('total=2.5');
  protected readonly previewLang = signal('ka');
  protected readonly preview = signal<TemplatePreview | null>(null);

  protected readonly langs = computed(() => this.lang.options().map((o) => o.code));
  protected readonly isTemplate = computed(() => this.type() === 'market_type' || this.type() === 'outcome_type');

  constructor() {
    this.lang.load();
    effect(() => {
      const langs = this.langs();
      if (!langs.length) {
        return;
      }
      this.api.translations({ entityType: this.type(), q: this.q(), langs: langs.join(','), missing: this.missing() || null, page: this.page() })
        .subscribe({ next: (r) => (this.rows.set(r.items), this.total.set(r.total)), error: (e) => this.snack.open(problemMessage(e), 'OK', { duration: 6000 }) });
    });
    effect(() => {
      const id = this.previewMarket();
      if (id) {
        this.api.preview(id, this.previewLang(), this.specifiers()).subscribe({ next: (p) => this.preview.set(p), error: () => this.preview.set(null) });
      }
    });
  }

  protected canEdit(): boolean {
    return this.session.has('i18n.edit') && !this.session.me()?.readOnly;
  }

  protected canImport(): boolean {
    return this.session.has('i18n.import') && !this.session.me()?.readOnly;
  }

  protected setType(t: I18nEntityType): void {
    if (this.pending().size && !confirm('Discard unsaved translations?')) {
      return;
    }
    this.pending.set(new Map());
    this.previewMarket.set(null);
    this.type.set(t);
    this.page.set(1);
  }

  protected changed(r: TranslationRow, lang: string): boolean {
    return this.pending().has(`${r.entityId}|${lang}`);
  }

  protected edit(r: TranslationRow, lang: string, event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    const m = new Map(this.pending());
    if ((r.values[lang].operator ?? '') === text) {
      m.delete(`${r.entityId}|${lang}`);
    } else {
      m.set(`${r.entityId}|${lang}`, { entityType: r.entityType, entityId: r.entityId, field: r.field, lang, text: text || null, platform: false });
    }
    this.pending.set(m);
  }

  protected focusTemplate(r: TranslationRow): void {
    if (this.isTemplate()) {
      this.previewMarket.set(Number(r.entityId.split(':')[0]));
    }
  }

  protected discard(): void {
    this.pending.set(new Map());
    this.page.set(this.page());
    this.rows.set([...this.rows()]);
  }

  protected async save(): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.saveTranslations([...this.pending().values()]));
      this.snack.open(`${r.saved} translations saved`, 'OK', { duration: 3000 });
      this.pending.set(new Map());
      this.reload();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 10000 });
    }
  }

  protected async export(): Promise<void> {
    const blob = await firstValueFrom(this.api.exportCsv(this.type(), this.langs().join(',')));
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `translations-${this.type()}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  protected async importFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const f = input.files?.[0];
    input.value = '';
    if (!f) {
      return;
    }
    const csv = await f.text();
    try {
      const r = await firstValueFrom(this.api.importCsv(csv, true));
      this.importResult.set({ rows: r.rows, changes: r.changes?.length ?? 0, errors: (r.errors ?? []) as string[], csv });
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  protected async applyImport(): Promise<void> {
    const r = this.importResult();
    if (!r) {
      return;
    }
    try {
      const done = await firstValueFrom(this.api.importCsv(r.csv, false));
      this.snack.open(`${done.saved} translations imported`, 'OK', { duration: 4000 });
      this.importResult.set(null);
      this.reload();
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
  }

  private reload(): void {
    this.api.translations({ entityType: this.type(), q: this.q(), langs: this.langs().join(','), missing: this.missing() || null, page: this.page() })
      .subscribe((r) => (this.rows.set(r.items), this.total.set(r.total)));
    if (this.previewMarket()) {
      this.api.preview(this.previewMarket()!, this.previewLang(), this.specifiers()).subscribe((p) => this.preview.set(p));
    }
  }
}
