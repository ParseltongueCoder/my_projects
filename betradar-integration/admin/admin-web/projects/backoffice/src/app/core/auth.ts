import { EnvironmentProviders, inject, Injectable, makeEnvironmentProviders, Provider } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import {
  autoLoginPartialRoutesGuard, LogLevel, OidcSecurityService, provideAuth, withAppInitializerAuthCheck,
} from 'angular-auth-oidc-client';
import { firstValueFrom } from 'rxjs';
import { AppConfig } from './config';

/** What the app needs from authentication, independent of whether Keycloak is configured. */
export abstract class AuthSession {
  abstract readonly enabled: boolean;
  /** Bearer token for calls the HTTP interceptor does not see (the SSE stream uses fetch). */
  abstract accessToken(): Promise<string | null>;
  /** Resolves once the login state is known (the OIDC check runs as an app initializer). */
  abstract isAuthenticated(): Promise<boolean>;
  abstract logout(): void;
}

@Injectable()
class OidcAuthSession extends AuthSession {
  private readonly oidc = inject(OidcSecurityService);
  readonly enabled = true;

  async accessToken(): Promise<string | null> {
    return (await firstValueFrom(this.oidc.getAccessToken())) || null;
  }

  async isAuthenticated(): Promise<boolean> {
    return (await firstValueFrom(this.oidc.isAuthenticated$)).isAuthenticated;
  }

  logout(): void {
    this.oidc.logoffAndRevokeTokens().subscribe();
  }
}

/** Auth disabled (local dev against an API started with Auth:Enabled=false). */
@Injectable()
class NoAuthSession extends AuthSession {
  readonly enabled = false;
  accessToken(): Promise<string | null> {
    return Promise.resolve(null);
  }
  isAuthenticated(): Promise<boolean> {
    return Promise.resolve(true);
  }
  logout(): void {}
}

/** OpenID Connect (authorization code + PKCE) against Keycloak, or nothing when disabled. */
export function provideBoAuth(config: AppConfig): (Provider | EnvironmentProviders)[] {
  if (!config.auth.enabled) {
    return [{ provide: AuthSession, useClass: NoAuthSession }];
  }
  const origin = window.location.origin;
  return [
    provideAuth(
      {
        config: {
          authority: config.auth.authority,
          clientId: config.auth.clientId,
          scope: config.auth.scope,
          // Keycloak issues refresh tokens without offline_access; don't ask for offline sessions.
          disableRefreshTokenOfflineAccessScopeWarning: true,
          responseType: 'code',
          redirectUrl: origin,
          postLogoutRedirectUri: origin,
          silentRenew: true,
          useRefreshToken: true,
          renewTimeBeforeTokenExpiresInSeconds: 30,
          // Attach the token only to our own API (relative '/api' calls and an absolute API base, if set).
          secureRoutes: config.apiBaseUrl ? [config.apiBaseUrl] : ['/api', `${origin}/api`],
          logLevel: LogLevel.Warn,
        },
      },
      withAppInitializerAuthCheck(),
    ),
    makeEnvironmentProviders([{ provide: AuthSession, useClass: OidcAuthSession }]),
  ];
}

/** Route guard: redirect to the Keycloak login when auth is on; open otherwise. */
export const boAuthGuard: CanActivateFn = (route, state) =>
  inject(AuthSession).enabled ? autoLoginPartialRoutesGuard(route, state) : true;

