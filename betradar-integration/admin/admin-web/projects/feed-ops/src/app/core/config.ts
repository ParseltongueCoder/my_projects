import { InjectionToken } from '@angular/core';

/**
 * Runtime configuration, read from /config.json before the app boots so one build runs in every
 * environment (nginx writes config.json from environment variables in Docker).
 */
export interface AppConfig {
  /** Prefix for API calls; empty = same origin ("/api/..."). */
  apiBaseUrl: string;
  auth: {
    enabled: boolean;
    authority: string;
    clientId: string;
    scope: string;
  };
  grafanaUrl?: string;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

export const DEFAULT_CONFIG: AppConfig = {
  apiBaseUrl: '',
  auth: { enabled: false, authority: '', clientId: 'feed-ops-web', scope: 'openid profile' },
};

export async function loadConfig(fetchFn: typeof fetch = fetch): Promise<AppConfig> {
  try {
    const response = await fetchFn('/config.json', { cache: 'no-store' });
    if (!response.ok) {
      return DEFAULT_CONFIG;
    }
    const loaded = (await response.json()) as Partial<AppConfig>;
    return { ...DEFAULT_CONFIG, ...loaded, auth: { ...DEFAULT_CONFIG.auth, ...loaded.auth } };
  } catch {
    return DEFAULT_CONFIG;
  }
}
