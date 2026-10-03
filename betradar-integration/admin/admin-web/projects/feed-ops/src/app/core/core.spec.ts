import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_CONFIG, DEFAULT_CONFIG, loadConfig } from './config';
import { FeedOpsApi } from './feed-ops-api';
import { parseSseEvent } from './live-changes';

describe('parseSseEvent', () => {
  it('parses change events', () => {
    expect(parseSseEvent('event: change\ndata: {"table":"market","eventId":7,"producerId":null}')).toEqual({
      table: 'market',
      eventId: 7,
      producerId: null,
    });
  });

  it('ignores comments, other events and broken data', () => {
    expect(parseSseEvent(': ping')).toBeNull();
    expect(parseSseEvent('event: other\ndata: {}')).toBeNull();
    expect(parseSseEvent('event: change\ndata: {not json')).toBeNull();
  });
});

describe('loadConfig', () => {
  it('merges config.json over the defaults', async () => {
    const fake = (async () => new Response(JSON.stringify({ apiBaseUrl: 'http://api', auth: { enabled: true } }))) as typeof fetch;
    const config = await loadConfig(fake);
    expect(config.apiBaseUrl).toBe('http://api');
    expect(config.auth.enabled).toBe(true);
    expect(config.auth.clientId).toBe('feed-ops-web');
  });

  it('falls back to defaults when config.json is missing', async () => {
    const missing = (async () => new Response('', { status: 404 })) as typeof fetch;
    expect(await loadConfig(missing)).toEqual(DEFAULT_CONFIG);
  });
});

describe('FeedOpsApi', () => {
  let api: FeedOpsApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: { ...DEFAULT_CONFIG, apiBaseUrl: 'http://api' } },
      ],
    });
    api = TestBed.inject(FeedOpsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends only the filters that are set', () => {
    api.events({ status: 'live', q: '', page: 2, pageSize: null as unknown as number }).subscribe();
    const req = http.expectOne((r) => r.url === 'http://api/api/events');
    expect(req.request.params.keys()).toEqual(['status', 'page']);
    expect(req.request.params.get('page')).toBe('2');
    req.flush({ items: [], page: 2, pageSize: 50, hasMore: false });
  });

  it('posts producer outages with mode and duration', () => {
    api.producerDown(1, 'silent', 30).subscribe();
    const req = http.expectOne((r) => r.url === 'http://api/api/sim/producers/1/down');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('mode')).toBe('silent');
    expect(req.request.params.get('seconds')).toBe('30');
    req.flush({});
  });
});
