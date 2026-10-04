import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthSession } from '../core/auth';
import { UserSession } from '../core/session';

interface NavItem {
  path: string;
  label: string;
  icon: string;
  permission?: string;
  needsOperator?: boolean;
  platformOnly?: boolean;
  soon?: string;
}

/** Sitemap (docs/08 §2): modules grouped by who uses them. Items for later stages show their stage. */
const NAV: { group: string; items: NavItem[] }[] = [
  { group: '', items: [{ path: '/', label: 'Home', icon: 'home' }] },
  {
    group: 'Offer',
    items: [
      { path: '/cat', label: 'Catalog', icon: 'account_tree', soon: 'BO-1' },
      { path: '/odds', label: 'Odds & margins', icon: 'percent', soon: 'BO-1' },
      { path: '/i18n', label: 'Translations', icon: 'translate', soon: 'BO-1' },
      { path: '/cms', label: 'Messages', icon: 'chat', soon: 'BO-1' },
    ],
  },
  {
    group: 'Trading & risk',
    items: [
      { path: '/mon', label: 'Bet monitoring', icon: 'monitor_heart', soon: 'BO-3' },
      { path: '/bet', label: 'Tickets', icon: 'receipt_long', soon: 'BO-2' },
      { path: '/lim', label: 'Limits & liability', icon: 'speed', soon: 'BO-2' },
      { path: '/cash', label: 'Cash-out', icon: 'payments', soon: 'BO-3' },
    ],
  },
  {
    group: 'Customers & marketing',
    items: [
      { path: '/cus', label: 'Customers', icon: 'group', soon: 'BO-2' },
      { path: '/promo', label: 'Promotions', icon: 'redeem', soon: 'BO-4' },
    ],
  },
  {
    group: 'Insights',
    items: [
      { path: '/rep', label: 'Reports', icon: 'query_stats', soon: 'BO-3' },
      { path: '/notif', label: 'Alerts', icon: 'notifications', soon: 'BO-3' },
    ],
  },
  {
    group: 'Configuration',
    items: [
      { path: '/cfg/scope', label: 'Settings', icon: 'tune', permission: 'cfg.view' },
      { path: '/cfg/effective', label: 'Effective config', icon: 'manage_search', permission: 'cfg.view', needsOperator: true },
      { path: '/cfg/change-sets', label: 'Change sets', icon: 'fact_check', permission: 'cfg.view' },
      { path: '/cfg/catalog', label: 'Settings catalog', icon: 'menu_book', permission: 'cfg.view' },
      { path: '/sites', label: 'Brands & modules', icon: 'language', permission: 'adm.brand.view', needsOperator: true },
    ],
  },
  {
    group: 'Administration',
    items: [
      { path: '/adm/users', label: 'Users', icon: 'manage_accounts', permission: 'adm.user.view' },
      { path: '/adm/roles', label: 'Roles', icon: 'badge', permission: 'adm.user.view' },
      { path: '/adm/audit', label: 'Audit log', icon: 'history', permission: 'adm.audit.view' },
      { path: '/platform/operators', label: 'Operators', icon: 'apartment', platformOnly: true },
    ],
  },
];

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatButtonModule, MatIconModule, MatTooltipModule, MatSelectModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit {
  protected readonly auth = inject(AuthSession);
  protected readonly session = inject(UserSession);
  private readonly router = inject(Router);
  protected readonly dark = signal(readDark());
  protected readonly navOpen = signal(false);

  protected readonly nav = computed(() => {
    const s = this.session;
    s.me(); // recompute when the session changes
    return NAV.map((g) => ({
      group: g.group,
      items: g.items.filter(
        (i) => (!i.permission || s.has(i.permission)) && (!i.needsOperator || s.operator()) && (!i.platformOnly || s.isPlatform()),
      ),
    })).filter((g) => g.items.length);
  });

  ngOnInit(): void {
    this.applyTheme();
    // Before login the guard redirects to Keycloak; don't fire API calls that can only fail with 401.
    void this.auth.isAuthenticated().then((ok) => (ok ? this.session.load() : undefined));
  }

  protected async switchOperator(id: number | null): Promise<void> {
    await this.session.switchOperator(id);
    await this.router.navigateByUrl('/');
  }

  protected toggleDark(): void {
    this.dark.update((d) => !d);
    try {
      localStorage.setItem('bo-dark', this.dark() ? '1' : '0');
    } catch {
      // storage unavailable: the choice just isn't remembered
    }
    this.applyTheme();
  }

  private applyTheme(): void {
    document.documentElement.classList.toggle('app-dark', this.dark());
  }
}

function readDark(): boolean {
  try {
    return localStorage.getItem('bo-dark') === '1';
  } catch {
    return false;
  }
}
