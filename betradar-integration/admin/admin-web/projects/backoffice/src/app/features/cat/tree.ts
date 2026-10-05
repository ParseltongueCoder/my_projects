import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { BoApi, problemMessage } from '../../core/bo-api';
import { ContentLanguage } from '../../core/content-lang';
import { TreeNode } from '../../core/models';
import { UserSession } from '../../core/session';
import { askReason } from '../../shared/reason-dialog';
import { MediaPicker } from '../../shared/media-picker';

interface Row {
  node: TreeNode;
  depth: number;
  siblings: TreeNode[];
  index: number;
}

const ROLE: Record<string, string> = { sport: 'icon', category: 'flag', tournament: 'logo' };

/**
 * Catalogue tree (docs/06 §2.2): the feed's sports → countries → leagues as this operator shows them — names in the
 * chosen language (rename inline), order, top leagues, images, and visibility (a configuration change: hidden at a
 * parent hides everything below).
 */
@Component({
  selector: 'bo-catalog-tree',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule,
    MatTooltipModule, RouterLink, MediaPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div><h1>Catalog</h1><div class="sub">Sports, countries and leagues as your players see them</div></div>
      <mat-form-field>
        <mat-label>Language</mat-label>
        <mat-select [ngModel]="lang.lang()" (ngModelChange)="lang.set($event)">
          @for (l of lang.options(); track l.code) { <mat-option [value]="l.code">{{ l.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Search</mat-label><input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" /></mat-form-field>
        <mat-slide-toggle [ngModel]="hiddenOnly()" (ngModelChange)="hiddenOnly.set($event)">Hidden only</mat-slide-toggle>
        <mat-slide-toggle [ngModel]="untranslated()" (ngModelChange)="untranslated.set($event)">Not translated</mat-slide-toggle>
        <span class="spacer"></span>
        <button matButton (click)="expandAll(true)">Expand all</button>
        <button matButton (click)="expandAll(false)">Collapse</button>
      </div>
      <div class="tree">
        @for (r of rows(); track r.node.type + r.node.id) {
          <div class="row" [style.padding-left.rem]="r.depth * 1.6" [class.hidden]="!r.node.visible" [attr.data-node]="r.node.type + '-' + r.node.id">
            @if (r.node.children.length) {
              <button matIconButton class="chev" (click)="toggle(r.node)" [attr.aria-label]="isOpen(r.node) ? 'Collapse' : 'Expand'">
                <mat-icon>{{ isOpen(r.node) ? 'expand_more' : 'chevron_right' }}</mat-icon>
              </button>
            } @else { <span class="chev"></span> }
            <bo-media-picker [entityType]="r.node.type" [entityId]="r.node.id" [role]="role[r.node.type]" [media]="r.node.media"
                             [disabled]="!canEdit()" (changed)="reload()" />
            @if (editing() === key(r.node)) {
              <input class="rename" [ngModel]="draft()" (ngModelChange)="draft.set($event)" (keyup.enter)="saveName(r.node)" (keyup.escape)="editing.set(null)" autofocus />
              <button matIconButton (click)="saveName(r.node)" aria-label="Save"><mat-icon>check</mat-icon></button>
            } @else {
              <span class="name" [class.provider]="r.node.nameSource !== 'operator'" (dblclick)="startRename(r.node)">{{ r.node.name }}</span>
              @if (r.node.name !== r.node.feedName) { <span class="muted small">{{ r.node.feedName }}</span> }
              @if (r.node.nameSource === 'platform') { <span class="chip">platform name</span> }
              @if (canEdit()) { <button matIconButton class="hover" (click)="startRename(r.node)" matTooltip="Rename in {{ lang.lang() }}" aria-label="Rename"><mat-icon>edit</mat-icon></button> }
            }
            <span class="spacer"></span>
            @if (r.node.type === 'tournament') {
              <a class="link small" routerLink="/cat/events" [queryParams]="{ tournamentId: r.node.id }">{{ r.node.openEvents }} events</a>
              <button matIconButton [disabled]="!canEdit()" (click)="toggleTop(r.node)" [matTooltip]="r.node.isTop ? 'Top league' : 'Mark as top league'"
                      [attr.aria-label]="r.node.isTop ? 'Unmark top league' : 'Mark as top league'">
                <mat-icon [class.top]="r.node.isTop">{{ r.node.isTop ? 'star' : 'star_outline' }}</mat-icon>
              </button>
            } @else { <span class="muted small">{{ r.node.openEvents }} events</span> }
            <button matIconButton [disabled]="!canEdit() || r.index === 0" (click)="move(r, -1)" aria-label="Move up"><mat-icon>arrow_upward</mat-icon></button>
            <button matIconButton [disabled]="!canEdit() || r.index === r.siblings.length - 1" (click)="move(r, 1)" aria-label="Move down"><mat-icon>arrow_downward</mat-icon></button>
            <mat-slide-toggle [checked]="!r.node.hiddenHere" [disabled]="!canHide()" (change)="setVisible(r.node, $event.checked)"
              [matTooltip]="r.node.hiddenHere ? 'Hidden here' : r.node.visible ? 'Visible' : 'Hidden by a parent'"
              [attr.aria-label]="'Visible ' + r.node.name"></mat-slide-toggle>
          </div>
        } @empty { <div class="empty">{{ loading() ? 'Loading…' : 'No catalogue yet: the feed has not delivered any sport.' }}</div> }
      </div>
    </section>
  `,
  styles: `
    .tree { display: flex; flex-direction: column; }
    .row { display: flex; align-items: center; gap: .4rem; min-height: 2.4rem; border-bottom: 1px solid var(--fo-border); }
    .row:hover { background: var(--fo-surface-alt); }
    .row.hidden .name { text-decoration: line-through; color: var(--fo-muted); }
    .chev { width: 2rem; display: inline-flex; }
    .name { font-weight: 500; cursor: text; }
    .name.provider { font-weight: 400; }
    .small { font-size: .8rem; }
    .spacer { flex: 1; }
    .hover { opacity: 0; } .row:hover .hover { opacity: 1; }
    .rename { font: inherit; padding: .2rem .4rem; border: 1px solid var(--fo-primary); border-radius: 6px; min-width: 16rem; background: var(--fo-surface); color: var(--fo-text); }
    mat-icon.top { color: var(--fo-warn); }
  `,
})
export class CatalogTreePage {
  private readonly api = inject(BoApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly session = inject(UserSession);
  protected readonly lang = inject(ContentLanguage);
  protected readonly role = ROLE;

  protected readonly tree = signal<TreeNode[]>([]);
  protected readonly loading = signal(true);
  protected readonly open = signal<Set<string>>(new Set());
  protected readonly q = signal('');
  protected readonly hiddenOnly = signal(false);
  protected readonly untranslated = signal(false);
  protected readonly editing = signal<string | null>(null);
  protected readonly draft = signal('');

  protected readonly canEdit = computed(() => this.session.has('cat.edit') && !this.session.me()?.readOnly);
  protected readonly canHide = computed(() => this.canEdit() && this.session.has('cfg.edit'));

  /** Flattened visible rows; filters keep a node when it or a descendant matches. */
  protected readonly rows = computed<Row[]>(() => {
    const q = this.q().trim().toLowerCase();
    const filtering = !!q || this.hiddenOnly() || this.untranslated();
    const matches = (n: TreeNode): boolean =>
      (!q || n.name.toLowerCase().includes(q) || n.feedName.toLowerCase().includes(q)) &&
      (!this.hiddenOnly() || !n.visible) && (!this.untranslated() || n.nameSource === 'provider');
    const keep = (n: TreeNode): boolean => matches(n) || n.children.some(keep);
    const out: Row[] = [];
    const walk = (nodes: TreeNode[], depth: number) => {
      const shown = nodes.filter((n) => !filtering || keep(n));
      shown.forEach((n, i) => {
        out.push({ node: n, depth, siblings: nodes, index: nodes.indexOf(n) });
        if (filtering ? n.children.some(keep) : this.isOpen(n)) {
          walk(n.children, depth + 1);
        }
        void i;
      });
    };
    walk(this.tree(), 0);
    return out;
  });

  constructor() {
    this.lang.load();
    effect(() => this.load(this.lang.lang()));
  }

  protected key(n: TreeNode): string {
    return `${n.type}-${n.id}`;
  }

  protected isOpen(n: TreeNode): boolean {
    return this.open().has(this.key(n));
  }

  protected toggle(n: TreeNode): void {
    const s = new Set(this.open());
    if (s.has(this.key(n))) {
      s.delete(this.key(n));
    } else {
      s.add(this.key(n));
    }
    this.open.set(s);
  }

  protected expandAll(on: boolean): void {
    const s = new Set<string>();
    if (on) {
      const walk = (nodes: TreeNode[]) => nodes.forEach((n) => (s.add(this.key(n)), walk(n.children)));
      walk(this.tree());
    }
    this.open.set(s);
  }

  protected startRename(n: TreeNode): void {
    if (!this.canEdit()) {
      return;
    }
    this.draft.set(n.nameSource === 'operator' ? n.name : '');
    this.editing.set(this.key(n));
  }

  protected async saveName(n: TreeNode): Promise<void> {
    const text = this.draft().trim();
    await this.run(
      () => firstValueFrom(this.api.saveTranslations([{ entityType: n.type, entityId: String(n.id), field: 'name', lang: this.lang.lang(), text: text || null, platform: false }])),
      text ? `Renamed to “${text}”` : 'Name reset to the default',
    );
    this.editing.set(null);
  }

  protected async toggleTop(n: TreeNode): Promise<void> {
    await this.run(() => firstValueFrom(this.api.patchNode(n.type, n.id, { isTop: !n.isTop })), n.isTop ? 'No longer a top league' : 'Marked as top league');
  }

  protected async move(r: Row, delta: number): Promise<void> {
    const ids = r.siblings.map((s) => s.id);
    const [moved] = ids.splice(r.index, 1);
    ids.splice(r.index + delta, 0, moved);
    await this.run(() => firstValueFrom(this.api.order(r.node.type, ids)), 'Order saved');
  }

  protected async setVisible(n: TreeNode, visible: boolean): Promise<void> {
    const reason = await askReason(this.dialog, {
      title: `${visible ? 'Show' : 'Hide'} ${n.name}`,
      message: visible ? 'Removes the hide set here; a hidden parent still hides it.' : 'Hides it and everything below it for your players.',
      confirm: visible ? 'Show' : 'Hide',
      danger: !visible,
    });
    if (!reason) {
      this.reload();
      return;
    }
    await this.run(async () => {
      const set = await firstValueFrom(this.api.visibility([{ type: n.type, id: n.id }], visible, reason));
      return set;
    }, visible ? `${n.name} shown` : `${n.name} hidden`);
  }

  protected reload(): void {
    this.load(this.lang.lang());
  }

  private async run(action: () => Promise<unknown>, message: string): Promise<void> {
    try {
      await action();
      this.snack.open(message, 'OK', { duration: 3000 });
    } catch (e) {
      this.snack.open(problemMessage(e), 'OK', { duration: 8000 });
    }
    this.reload();
  }

  private load(lang: string): void {
    this.api.tree(lang).subscribe({
      next: (t) => {
        this.tree.set(t);
        this.loading.set(false);
        if (!this.open().size && t.length === 1) {
          this.open.set(new Set([this.key(t[0])]));
        }
      },
      error: (e) => {
        this.loading.set(false);
        this.snack.open(problemMessage(e), 'OK', { duration: 6000 });
      },
    });
  }
}
