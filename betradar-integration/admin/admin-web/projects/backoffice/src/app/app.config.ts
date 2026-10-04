import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { MatIconRegistry } from '@angular/material/icon';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { authInterceptor } from 'angular-auth-oidc-client';
import { routes } from './app.routes';
import { provideBoAuth } from './core/auth';
import { operatorHeaderInterceptor } from './core/bo-api';
import { APP_CONFIG, AppConfig } from './core/config';

export function buildAppConfig(config: AppConfig): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      { provide: APP_CONFIG, useValue: config },
      provideRouter(routes, withComponentInputBinding()),
      provideHttpClient(withInterceptors([...(config.auth.enabled ? [authInterceptor()] : []), operatorHeaderInterceptor])),
      ...provideBoAuth(config),
      // Self-hosted Material Symbols (no Google Fonts CDN).
      provideAppInitializer(() => {
        inject(MatIconRegistry).setDefaultFontSetClass('material-symbols-outlined');
      }),
      { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline', subscriptSizing: 'dynamic' } },
    ],
  };
}
