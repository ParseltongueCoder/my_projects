import { HttpErrorResponse } from '@angular/common/http';
import { problemMessage } from './bo-api';
import { loadConfig } from './config';
import { SettingDef } from './models';
import { formatValue } from '../shared/setting-value';

const def = (over: Partial<SettingDef>): SettingDef => ({
  key: 'x', module: 'CFG', type: 'int', allowedScopes: ['operator'], allowsMarketType: false, default: null, combine: 'Override',
  enumValues: null, min: null, max: null, operatorEditable: true, requiresApproval: false, customerCombine: 'none', description: '',
  ...over,
});

describe('formatValue', () => {
  it('shows money per currency, percentages, lists and toggles', () => {
    expect(formatValue(def({ type: 'money' }), { GEL: 5000, USD: 1500 })).toBe('GEL 5,000 · USD 1,500');
    expect(formatValue(def({ type: 'decimal', key: 'margin.pct' }), 0.04)).toBe('0.04 (4%)');
    expect(formatValue(def({ type: 'decimal', key: 'cashout.margin_pct' }), 0.05)).toBe('0.05 (5%)');
    expect(formatValue(def({ type: 'stringList' }), ['ka', 'en'])).toBe('ka, en');
    expect(formatValue(def({ type: 'bool' }), false)).toBe('off');
    expect(formatValue(def({}), null)).toBe('—');
  });
});

describe('problemMessage', () => {
  it('prefers the ProblemDetails title and code', () => {
    const err = new HttpErrorResponse({ status: 403, error: { title: 'Missing permission', code: 'PERMISSION_DENIED' } });
    expect(problemMessage(err)).toBe('Missing permission (PERMISSION_DENIED)');
    expect(problemMessage(new HttpErrorResponse({ status: 0 }))).toBe('API not reachable');
  });
});

describe('loadConfig', () => {
  it('falls back to defaults (auth off, bo-web client) when config.json is missing', async () => {
    const config = await loadConfig((() => Promise.resolve(new Response('', { status: 404 }))) as typeof fetch);
    expect(config.auth.enabled).toBe(false);
    expect(config.auth.clientId).toBe('bo-web');
  });
});
