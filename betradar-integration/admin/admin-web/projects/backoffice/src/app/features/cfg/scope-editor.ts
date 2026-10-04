import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { Brand, Json, MarketType, ScopeOption, ScopeSetting, ScopeType, SettingChange, SettingDef } from '../../core/models';
import { UserSession } from '../../core/session';
import { formatValue } from '../../shared/setting-value';
import { EditSettingDialog, EditSettingData } from './edit-setting-dialog';
import { MarketTypeSearch, ScopeSearch } from './scope-picker';

interface Staged {
  key: string;
  op: 'upsert' | 'delete';
  value: Json;
}

const LEVEL_LABEL: Record<ScopeType, string> = {
  platform: 'Platform', operator: 'Operator', brand: 'Brand', sport: 'Sport', category: 'Country', tournament: 'League', event: 'Event', market: 'Market',
};

/**
 * Scope editor (docs/06 §5.6): pick a level of the hierarchy, see every key allowed there with what is set exactly here
 * and what applies (inherited, with its source). Edits are staged and submitted as one change set; keys that need
 * approval go to a second user.
 */
@Component({
  selector: 'bo-scope-editor',
  imports: [
    FormsModule, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSlideToggleModule, MatTooltipModule, RouterLink, ScopeSearch, MarketTypeSearch,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './scope-editor.html',
  styles: `
    .scope-bar { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: .45rem .5rem; border-bottom: 1px solid var(--fo-border); vertical-align: top; }
    th { color: var(--fo-muted); font-weight: 500; font-size: .85rem; }
    tr.staged td { background: var(--fo-warn-bg); }
    .key { font-family: ui-monospace, monospace; font-size: .85rem; }
    .desc { color: var(--fo-muted); font-size: .8rem; }
    .actions { white-space: nowrap; text-align: right; }
    mat-icon.small { font-size: 16px; width: 16px; height: 16px; vertical-align: middle; color: var(--fo-warn); }
  `,
})
export class ScopeEditorPage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly levelLabel = LEVEL_LABEL;

  protected readonly scopeType = signal<ScopeType>('operator');
  protected readonly brand = signal<Brand | null>(null);
  protected readonly node = signal<ScopeOption | null>(null);
  protected readonly marketType = signal<MarketType | null>(null);
  protected readonly brands = signal<Brand[]>([]);
  protected readonly defs = signal<Map<string, SettingDef>>(new Map());
  protected readonly rows = signal<ScopeSetting[]>([]);
  protected readonly loading = signal(false);
  protected readonly moduleFilter = signal<string>('');
  protected readonly search = signal('');
  protected readonly onlySetHere = signal(false);
  protected readonly staged = signal<Map<string, Staged>>(new Map());
  protected readonly title = signal('');
  protected readonly submitting = signal(false);

  protected readonly levels = computed<ScopeType[]>(() => {
    const op = !!this.session.operator();
    const list: ScopeType[] = [];
    if (this.session.isPlatform()) {
      list.push('platform');
    }
    if (op) {
      list.push('operator', 'brand', 'sport', 'category', 'tournament', 'event');
    }
    return list;
  });

  protected readonly scopeId = computed<number | null>(() => {
    const t = this.scopeType();
    if (t === 'platform' || t === 'operator') {
      return null;
    }
    if (t === 'brand') {
      return this.brand()?.id ?? null;
    }
    const n = this.node();
    return n && n.type === t ? n.id : null;
  });

  protected readonly ready = computed(() => ['platform', 'operator'].includes(this.scopeType()) || this.scopeId() !== null);
  protected readonly modules = computed(() => [...new Set(this.rows().map((r) => r.module))].sort());
  protected readonly canEdit = computed(() => this.session.has('cfg.edit') && !this.session.me()?.readOnly);
  protected readonly currencies = computed(() => ['GEL', 'USD', 'EUR']);

  protected readonly visible = computed(() => {
    const q = this.search().toLowerCase();
    const mod = this.moduleFilter();
    return this.rows().filter(
      (r) =>
        (!mod || r.module === mod) &&
        (!q || r.key.includes(q) || (this.defs().get(r.key)?.description ?? '').toLowerCase().includes(q)) &&
        (!this.onlySetHere() || r.valueHere !== null || this.staged().has(r.key)),
    );
  });

  constructor() {
    const query = inject(ActivatedRoute).snapshot.queryParamMap;
    effect(() => {
      // Default level follows the session: platform staff without an operator start at platform level.
      const levels = this.levels();
      if (levels.length && !levels.includes(this.scopeType())) {
        this.scopeType.set(levels[0]);
      }
    });
    const requested = query.get('scope') as ScopeType | null;
    if (requested) {
      this.scopeType.set(requested);
    }
    this.api.settingDefs().subscribe((d) => this.defs.set(new Map(d.map((x) => [x.key, x]))));
    effect(() => {
      if (this.session.operator()) {
        this.api.brands().subscribe((b) => this.brands.set(b));
      }
    });
    effect(() => {
      const type = this.scopeType();
      const id = this.scopeId();
      const mt = this.marketType()?.id ?? null;
      if (this.ready() && this.levels().includes(type)) {
        this.load(type, id, mt);
      } else {
        this.rows.set([]);
      }
    });
  }

  protected setLevel(t: ScopeType): void {
    if (this.staged().size && !confirm('Discard staged changes?')) {
      return;
    }
    this.staged.set(new Map());
    this.scopeType.set(t);
    this.node.set(null);
  }

  protected def(key: string): SettingDef | undefined {
    return this.defs().get(key);
  }

  protected format(key: string, value: Json): string {
    return formatValue(this.def(key), value);
  }

  protected source(r: ScopeSetting): string {
    const w = r.effective.winner;
    if (!w) {
      return 'default';
    }
    const here = w.scopeType === this.scopeType() && w.scopeId === this.scopeId() && w.marketTypeId === (this.marketType()?.id ?? null);
    if (here) {
      return 'set here';
    }
    return `${LEVEL_LABEL[w.scopeType]}${w.scopeId !== null ? ' #' + w.scopeId : ''}${w.marketTypeId !== null ? ' · market type ' + w.marketTypeId : ''}`;
  }

  protected async edit(r: ScopeSetting): Promise<void> {
    const def = this.def(r.key);
    if (!def) {
      return;
    }
    const staged = this.staged().get(r.key);
    const ref = this.dialog.open<EditSettingDialog, EditSettingData, Json>(EditSettingDialog, {
      data: { def, current: staged?.op === 'upsert' ? staged.value : (r.valueHere ?? r.effective.value), currencies: this.currencies(), where: this.where() },
      width: '36rem',
    });
    const value = await firstValueFrom(ref.afterClosed());
    if (value !== undefined && value !== null) {
      this.stage({ key: r.key, op: 'upsert', value });
    }
  }

  protected reset(r: ScopeSetting): void {
    this.stage({ key: r.key, op: 'delete', value: null });
  }

  protected discard(): void {
    this.staged.set(new Map());
  }

  protected unstage(key: string): void {
    const m = new Map(this.staged());
    m.delete(key);
    this.staged.set(m);
  }

  private stage(s: Staged): void {
    this.staged.set(new Map(this.staged()).set(s.key, s));
  }

  protected where(): string {
    const t = this.scopeType();
    const name = t === 'brand' ? this.brand()?.name : this.node()?.name;
    const mt = this.marketType();
    return `${LEVEL_LABEL[t]}${name ? ': ' + name : ''}${mt ? ` · market type #${mt.id} ${mt.name}` : ''}`;
  }

  protected needsApproval(): boolean {
    return [...this.staged().values()].some((s) => this.def(s.key)?.requiresApproval);
  }

  protected async submit(): Promise<void> {
    const changes: SettingChange[] = [...this.staged().values()].map((s) => ({
      op: s.op,
      scopeType: this.scopeType(),
      scopeId: this.scopeId(),
      marketTypeId: this.marketType()?.id ?? null,
      key: s.key,
      value: s.op === 'upsert' ? s.value : undefined,
    }));
    this.submitting.set(true);
    try {
      const set = await firstValueFrom(this.api.createChangeSet(this.title().trim(), this.scopeType() === 'platform', changes));
      this.snack.open(
        set.status === 'applied' ? `Change set #${set.id} applied` : `Change set #${set.id} waits for approval by a second user`,
        'OK',
        { duration: 5000 },
      );
      this.staged.set(new Map());
      this.title.set('');
      this.load(this.scopeType(), this.scopeId(), this.marketType()?.id ?? null);
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    } finally {
      this.submitting.set(false);
    }
  }

  private load(type: ScopeType, id: number | null, mt: number | null): void {
    this.loading.set(true);
    this.api.scopeSettings(type, id, mt).subscribe({
      next: (r) => {
        this.rows.set(r);
        this.loading.set(false);
      },
      error: (e) => {
        this.rows.set([]);
        this.loading.set(false);
        this.snack.open(problemMessage(e), 'OK', { duration: 6000 });
      },
    });
  }
}
