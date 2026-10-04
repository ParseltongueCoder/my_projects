import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

/** Placeholder for modules of later stages (docs/09 §5), so the sitemap is complete from day one. */
@Component({
  selector: 'bo-soon',
  imports: [MatIconModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><div><h1>{{ title() }}</h1><div class="sub">Module {{ module() }}</div></div></div>
    <div class="panel empty">
      <mat-icon>construction</mat-icon>
      <p>This module arrives in stage <b>{{ stage() }}</b> of the back-office plan.</p>
      <p class="muted">Its settings already exist in the <a class="link" routerLink="/cfg/catalog">settings catalog</a>.</p>
    </div>
  `,
})
export class SoonPage {
  readonly title = input('');
  readonly module = input('');
  readonly stage = input('');
}
