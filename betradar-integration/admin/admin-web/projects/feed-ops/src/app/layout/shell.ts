import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthSession } from '../core/auth';
import { APP_CONFIG } from '../core/config';
import { FeedOpsApi } from '../core/feed-ops-api';
import { LiveChanges } from '../core/live-changes';
import { UserSession } from '../core/session';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit {
  protected readonly auth = inject(AuthSession);
  protected readonly session = inject(UserSession);
  protected readonly live = inject(LiveChanges);
  protected readonly config = inject(APP_CONFIG);
  private readonly api = inject(FeedOpsApi);
  protected readonly simulatorEnabled = signal(false);
  protected readonly dark = signal(readDark());

  protected readonly nav = [
    { path: '/', label: 'Overview', icon: 'monitoring', exact: true },
    { path: '/events', label: 'Events', icon: 'sports_soccer', exact: false },
    { path: '/producers', label: 'Producers', icon: 'dns', exact: false },
    { path: '/messages', label: 'Feed messages', icon: 'inbox', exact: false },
  ];

  ngOnInit(): void {
    this.applyTheme();
    // Before login the guard redirects to Keycloak; don't fire API calls that can only fail with 401.
    void this.auth.isAuthenticated().then((ok) => ok && this.loadSession());
  }

  private loadSession(): void {
    this.session.load();
    this.live.start();
    this.api.simulatorStatus().subscribe({ next: (s) => this.simulatorEnabled.set(s.enabled !== false), error: () => {} });
  }

  protected toggleDark(): void {
    this.dark.update((d) => !d);
    try {
      localStorage.setItem('feedops-dark', this.dark() ? '1' : '0');
    } catch {
      // storage unavailable (private mode): the choice just isn't remembered
    }
    this.applyTheme();
  }

  private applyTheme(): void {
    document.documentElement.classList.toggle('app-dark', this.dark());
  }
}

function readDark(): boolean {
  try {
    return localStorage.getItem('feedops-dark') === '1';
  } catch {
    return false;
  }
}
