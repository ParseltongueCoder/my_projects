import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { authInterceptor } from 'angular-auth-oidc-client';
import { routes } from './app.routes';
import { provideFeedOpsAuth } from './core/auth';
import { APP_CONFIG, AppConfig } from './core/config';

export function buildAppConfig(config: AppConfig): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      { provide: APP_CONFIG, useValue: config },
      provideRouter(routes, withComponentInputBinding()),
      provideHttpClient(withInterceptors(config.auth.enabled ? [authInterceptor()] : [])),
      ...provideFeedOpsAuth(config),
      // Self-hosted Material Symbols (no Google Fonts CDN: the admin may run on an isolated network).
      provideAppInitializer(() => {
        inject(MatIconRegistry).setDefaultFontSetClass('material-symbols-outlined');
      }),
      { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline', subscriptSizing: 'dynamic' } },
    ],
  };
}
