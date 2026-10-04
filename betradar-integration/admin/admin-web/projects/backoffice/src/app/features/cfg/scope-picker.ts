import { ChangeDetectionStrategy, Component, effect, inject, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { debounceTime, distinctUntilChanged, Subject, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BoApi } from '../../core/bo-api';
import { MarketType, ScopeOption, ScopeType } from '../../core/models';

/** Search the feed catalogue (sports, countries, leagues, events) for a scope id. */
@Component({
  selector: 'bo-scope-search',
  imports: [FormsModule, MatAutocompleteModule, MatFormFieldModule, MatInputModule, MatIconModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="search">
      <mat-label>{{ label() }}</mat-label>
      <input matInput [ngModel]="text()" (ngModelChange)="onText($event)" [matAutocomplete]="auto" />
      @if (selected()) {
        <button matIconButton matSuffix (click)="clear()" aria-label="Clear"><mat-icon>close</mat-icon></button>
      }
      <mat-autocomplete #auto="matAutocomplete" (optionSelected)="pick($event.option.value)">
        @for (o of options(); track o.type + o.id) {
          <mat-option [value]="o">
            <span class="chip">{{ o.type }}</span> {{ o.name }} <span class="muted">{{ o.path }}</span>
          </mat-option>
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
  styles: `.search { min-width: 22rem; }`,
})
export class ScopeSearch {
  private readonly api = inject(BoApi);
  readonly type = input<ScopeType | undefined>();
  readonly label = input('Search sport, country, league or event');
  readonly selected = model<ScopeOption | null>(null);
  protected readonly text = signal('');
  protected readonly options = signal<ScopeOption[]>([]);
  private readonly terms = new Subject<string>();

  constructor() {
    this.terms
      .pipe(debounceTime(200), distinctUntilChanged(), switchMap((q) => this.api.scopes(q, this.type())), takeUntilDestroyed())
      .subscribe((o) => this.options.set(o));
    effect(() => {
      const s = this.selected();
      this.text.set(s ? `${s.name ?? s.id}` : '');
    });
  }

  protected onText(q: string): void {
    this.text.set(q);
    this.terms.next(q);
  }

  protected pick(o: ScopeOption): void {
    this.selected.set(o);
  }

  protected clear(): void {
    this.selected.set(null);
    this.onText('');
  }
}

/** Optional market type qualifier (docs/09 §2.2). */
@Component({
  selector: 'bo-market-type-search',
  imports: [FormsModule, MatAutocompleteModule, MatFormFieldModule, MatInputModule, MatIconModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field>
      <mat-label>Market type (optional)</mat-label>
      <input matInput [ngModel]="text()" (ngModelChange)="onText($event)" [matAutocomplete]="auto" />
      @if (selected()) {
        <button matIconButton matSuffix (click)="clear()" aria-label="Clear"><mat-icon>close</mat-icon></button>
      }
      <mat-autocomplete #auto="matAutocomplete" (optionSelected)="pick($event.option.value)">
        @for (o of options(); track o.id) {
          <mat-option [value]="o">#{{ o.id }} {{ o.name }}</mat-option>
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
})
export class MarketTypeSearch {
  private readonly api = inject(BoApi);
  readonly selected = model<MarketType | null>(null);
  readonly changed = output<MarketType | null>();
  protected readonly text = signal('');
  protected readonly options = signal<MarketType[]>([]);
  private readonly terms = new Subject<string>();

  constructor() {
    this.terms
      .pipe(debounceTime(200), distinctUntilChanged(), switchMap((q) => this.api.marketTypes(q)), takeUntilDestroyed())
      .subscribe((o) => this.options.set(o));
    effect(() => {
      const s = this.selected();
      this.text.set(s ? `#${s.id} ${s.name}` : '');
    });
    this.terms.next('');
  }

  protected onText(q: string): void {
    this.text.set(q);
    this.terms.next(q);
  }

  protected pick(o: MarketType): void {
    this.selected.set(o);
    this.changed.emit(o);
  }

  protected clear(): void {
    this.selected.set(null);
    this.changed.emit(null);
    this.onText('');
  }
}
