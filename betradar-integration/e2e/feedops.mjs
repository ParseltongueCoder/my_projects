// End-to-end check of the Feed Ops admin against the full docker-compose stack.
import { chromium } from 'playwright';

const BASE = 'http://localhost:8088';
const SHOTS = process.argv[2];
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH, args: ['--no-sandbox'] } : {});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const log = (m) => console.log(`[e2e ${new Date().toISOString().slice(11, 19)}] ${m}`);
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));

// 1. Login through Keycloak
await page.goto(BASE);
await page.waitForURL(/localhost:8180\/realms\/feedops/, { timeout: 30000 });
log('redirected to Keycloak');
await page.fill('#username', 'operator');
await page.fill('#password', 'operator');
await page.click('#kc-login');
await page.waitForURL(`${BASE}/**`, { timeout: 30000 });
await page.getByRole('heading', { name: 'Overview' }).waitFor();
await page.getByText('operator', { exact: true }).first().waitFor();
await page.locator('.stream.live').waitFor({ timeout: 20000 });
log('logged in, live stream connected');

// 2. Start the derby scenario from the Simulator page
await page.getByRole('link', { name: 'Simulator' }).click();
await page.getByRole('heading', { name: 'Simulator' }).waitFor();
// Slow enough that the match is still live when we take the producer down below.
await page.getByLabel('Speed').fill('0.5');
const row = page.locator('.row', { hasText: 'derby_settlement_rollback' });
await row.getByRole('button', { name: 'Start' }).click();
await page.getByText('Scenario derby_settlement_rollback started').waitFor();
log('scenario started from the UI');
await page.screenshot({ path: `${SHOTS}/05-simulator.png`, fullPage: true });

// 3. Events list updates live; open the match
await page.getByRole('link', { name: 'Events' }).click();
const match = page.getByRole('link', { name: 'Kutaisi Eagles v Rustavi Steel' });
await match.waitFor({ timeout: 30000 });
await page.waitForTimeout(6000);
await page.screenshot({ path: `${SHOTS}/02-events.png`, fullPage: true });
await match.click();
await page.getByText('Both teams to score').waitFor({ timeout: 30000 });
log('event detail shows rendered market names');
await page.screenshot({ path: `${SHOTS}/03-event-markets.png`, fullPage: true });

// 4. Producer outage from the UI: markets must be held suspended, then reopen after recovery
await page.goto(`${BASE}/simulator`);
const lo = page.locator('[data-producer="1"]');
await lo.getByRole('button', { name: 'Silent' }).click();
await page.goto(`${BASE}/producers`);
await page.locator('tr', { hasText: 'LO' }).getByText('down', { exact: true }).waitFor({ timeout: 60000 });
log('producer LO reported down');
await page.screenshot({ path: `${SHOTS}/04-producers-down.png`, fullPage: true });
await page.locator('tr', { hasText: 'LO' }).getByText('up', { exact: true }).waitFor({ timeout: 90000 });
log('producer LO back up after recovery');

// 5. Wait for the match to end and be settled, then look at settlements
await page.goBack();
await page.goto(`${BASE}/events`);
await page.getByRole('link', { name: 'Kutaisi Eagles v Rustavi Steel' }).click();
await page.getByRole('tab', { name: /Settlements \([1-9]/ }).waitFor({ timeout: 150000 });
await page.waitForTimeout(8000);
await page.getByRole('tab', { name: /Settlements/ }).click();
await page.screenshot({ path: `${SHOTS}/06-settlements.png`, fullPage: true });
await page.getByRole('tab', { name: /Bet stops/ }).click();
await page.getByText('producer_down').first().waitFor();
log('bet stop log shows the producer_down suspension');

// 6. Feed messages with the raw XML drawer
await page.goto(`${BASE}/messages`);
await page.locator('td.mono', { hasText: 'rollback_bet_settlement' }).first().click();
await page.getByText('Raw XML').waitFor();
await page.waitForTimeout(500);
await page.screenshot({ path: `${SHOTS}/07-message-xml.png` });
await page.keyboard.press('Escape');
await page.waitForTimeout(300);

// 7. Overview at the end
await page.goto(BASE);
await page.getByText('Producers').first().waitFor();
await page.waitForTimeout(1500);
await page.screenshot({ path: `${SHOTS}/01-overview.png`, fullPage: true });

const relevant = errors.filter((e) => !/favicon|ERR_ABORTED/.test(e));
log(relevant.length ? `browser errors: ${relevant.join(' | ')}` : 'no browser errors');
await browser.close();
log('E2E PASSED');
