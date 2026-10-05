// BO-1b in the browser: trading view, odds override, suspend, manual market, market type matrix, margin simulator, CMS.
// Drives `ng serve backoffice --port 4300` (proxy /api → Bo.Api with Auth__Enabled=false, Bo__DevSeed=true) over a
// catalogue with one soccer match priced 1x2 2.10 / 3.40 / 3.60 and total 2.5 1.85 / 1.95 (see README.md).
import { chromium } from 'playwright';
const base = 'http://localhost:4300';
const shots = process.argv[2];
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH, args: ['--no-sandbox'] } : {});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
page.on('pageerror', (e) => errors.push(String(e)));
const log = (m) => console.log('[bo1b]', m);
const expectText = async (locator, text) => {
  const actual = (await locator.innerText()).trim();
  if (!actual.includes(text)) throw new Error(`expected "${text}", got "${actual}"`);
};

await page.goto(base + '/');
await page.locator('.switcher').click();
await page.getByRole('option', { name: 'AcmeBet' }).click();
await page.waitForSelector('text=Acting as AcmeBet');

// Trading view
await page.goto(base + '/odds');
await page.locator('[data-trading-row]').first().click();
await page.locator('[data-trading-event]').waitFor();
const x12 = page.locator('[data-market]').first();
await expectText(x12.locator('[data-offered="1"]'), '2.10');
log('trading view shows the feed price');

// Override the home win: 2.50 for 30 minutes → market held by the sanity check (1/2.5 + 1/3.4 + 1/3.6 < 1)
await x12.locator('[data-override="1"]').click();
await page.locator('[data-override-odds]').fill('2.50');
await page.locator('[data-override-ttl]').fill('30');
await page.locator('[data-override-reason]').fill('liability on the home side');
await page.locator('[data-override-save]').click();
await page.getByText(/Override set/).waitFor();
await x12.locator('[data-offered="1"]').getByText('2.50').waitFor();
await x12.locator('[data-market-status="suspended"]').waitFor();
await x12.getByText('prices below the margin floor').waitFor();
log('override 2.50 applied; market suspended by the margin floor');
await page.screenshot({ path: `${shots}/11-trading-override.png`, fullPage: true });
await x12.locator('[data-clear-override]').click();
await x12.locator('[data-offered="1"]').getByText('2.10').waitFor();
log('override cleared');

// Suspend the whole event, then lift it
await page.locator('[data-suspend-event]').click();
await page.locator('[data-trading-reason]').fill('team news');
await page.locator('[data-trading-save]').click();
await page.locator('[data-trading="suspend"]').first().waitFor();
await x12.locator('[data-market-status="suspended"]').waitFor();
log('event suspended');
await page.screenshot({ path: `${shots}/12-trading-suspended.png`, fullPage: true });
await page.locator('[data-lift]').first().click();
await page.locator('mat-dialog-container').getByRole('button', { name: 'Lift' }).click();
await x12.locator('[data-market-status="active"]').waitFor();
log('suspension lifted');

// Manual market from the Total template
await page.locator('[data-add-manual]').click();
await page.locator('[data-manual-type]').fill('Total');
await page.getByRole('option', { name: /Total/ }).first().click();
await page.locator('[data-spec="total"]').fill('3.5');
await page.locator('[data-price="12"]').fill('1.833');
await page.locator('[data-price="13"]').fill('1.95');
await page.locator('[data-manual-reason]').fill('derby special');
await page.locator('[data-manual-save]').click();
await page.getByText('Manual market created').waitFor();
const manual = page.locator('[data-market]').filter({ hasText: 'manual' }).first();
await expectText(manual.locator('[data-offered="12"]'), '1.83');
log('manual total 3.5 created at 1.83 / 1.95');
await manual.locator('[data-manual-price="12"]').fill('2.05');
await manual.locator('[data-save-manual]').click();
await manual.locator('[data-offered="12"]').getByText('2.04').waitFor();
log('manual price edited (ladder: 2.05 → 2.04)');
await page.screenshot({ path: `${shots}/13-trading-manual.png`, fullPage: true });

// An override that stays, then the active overrides page
await x12.locator('[data-override="3"]').click();
await page.locator('[data-override-odds]').fill('3.50');
await page.locator('[data-override-ttl]').fill('45');
await page.locator('[data-override-reason]').fill('shade the away side');
await page.locator('[data-override-save]').click();
await page.getByText(/Override set/).waitFor();
await page.goto(base + '/odds/overrides');
await page.locator('[data-override-row]').first().waitFor();
log('active overrides listed');
await page.screenshot({ path: `${shots}/14-active-overrides.png`, fullPage: true });

// Market type matrix: totals off for soccer, then on again
await page.goto(base + '/odds/market-types');
const cell = page.locator('[data-cell="2:1"] input');
await cell.click();
await page.locator('mat-dialog-container textarea').fill('totals not offered on soccer');
await page.getByRole('button', { name: 'Switch off' }).click();
await page.getByText('Applied').waitFor();
if (await cell.isChecked()) throw new Error('total still on for soccer');
log('total switched off for soccer');
await page.screenshot({ path: `${shots}/15-market-types.png`, fullPage: true });
await cell.click();
await page.locator('mat-dialog-container textarea').fill('back on');
await page.getByRole('button', { name: 'Switch on' }).click();
await page.getByText('Applied').waitFor();

// Margin simulator: the docs/06 example
await page.goto(base + '/odds/margins');
await page.locator('[data-sim-odds]').fill('2.10 3.40 3.60');
await page.locator('[data-sim-pct]').fill('7');
await page.locator('[data-sim-run]').click();
await expectText(page.locator('[data-sim-outcome="1"]'), '2.06');
await expectText(page.locator('[data-sim-outcome="2"]'), '3.30');
await expectText(page.locator('[data-sim-outcome="3"]'), '3.50');
log('simulator: 2.10/3.40/3.60 at 7% → 2.06/3.30/3.50');
await page.screenshot({ path: `${shots}/16-margin-simulator.png`, fullPage: true });

// CMS: own Georgian text, preview with parameters, lint against internal terms
await page.goto(base + '/cms');
await page.locator('[data-message="LIM_MAX_STAKE_EXCEEDED"]').click();
await page.locator('[data-message-text="ka"]').fill('ამ არჩევანზე მაქსიმალური ფსონია {maxStake, number} {currency}');
await page.locator('[data-message-save]').click();
await page.getByText('Saved').waitFor();
await page.locator('[data-preview="ka"]').click();
await expectText(page.locator('[data-preview-result="ka"]'), 'ამ არჩევანზე მაქსიმალური ფსონია 150.00 GEL');
log('CMS text saved and previewed');
await page.screenshot({ path: `${shots}/17-cms-messages.png`, fullPage: true });
await page.locator('[data-message="LIM_NOT_ACCEPTED"]').click();
await page.locator('[data-message-text="en"]').fill('Liability limit reached');
await page.locator('[data-message-save]').click();
await page.getByText(/MESSAGE_LINT/).waitFor();
log('CMS lint rejected an internal term');

console.log('errors:', JSON.stringify(errors.filter((e) => !e.includes('400'))));
await browser.close();
