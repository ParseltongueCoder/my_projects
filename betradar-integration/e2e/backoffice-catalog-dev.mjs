import { chromium } from 'playwright';
const base = 'http://localhost:4300';
const shots = process.argv[2];
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH, args: ['--no-sandbox'] } : {});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
page.on('pageerror', (e) => errors.push(String(e)));
const log = (m) => console.log('[bo1]', m);
await page.goto(base + '/');
await page.locator('.switcher').click();
await page.getByRole('option', { name: 'AcmeBet' }).click();
await page.waitForSelector('text=Acting as AcmeBet');

// Catalogue: expand, rename a league in Georgian, hide a country, mark top league, upload a flag
await page.goto(base + '/cat');
await page.locator('[data-node="sport-1"]').waitFor();
await page.getByRole('button', { name: 'Expand all' }).click();
const league = page.locator('[data-node="tournament-1"]');
await league.waitFor();
await league.hover();
await league.getByRole('button', { name: 'Rename' }).click();
await page.locator('input.rename').fill('ეროვნული ლიგა');
await page.locator('input.rename').press('Enter');
await league.getByText('ეროვნული ლიგა').waitFor();
log('league renamed in Georgian');
await league.getByRole('button', { name: /top league/i }).click();
await page.waitForTimeout(500);
const england = page.locator('[data-node="category-2"]');
await england.getByRole('switch').click();
await page.locator('mat-dialog-container textarea').fill('Not licensed in our market');
await page.getByRole('button', { name: 'Hide' }).click();
await page.locator('[data-node="tournament-3"].hidden').waitFor();
log('England hidden → Premier League hidden by parent');
await page.locator('[data-node="category-1"] bo-media-picker input[type=file]').setInputFiles({
  name: 'georgia.svg', mimeType: 'image/svg+xml',
  buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 20"><rect width="30" height="20" fill="#fff"/><rect x="13" width="4" height="20" fill="#e00"/><rect y="8" width="30" height="4" fill="#e00"/></svg>'),
});
await page.locator('[data-node="category-1"] bo-media-picker img').waitFor();
log('flag uploaded');
await page.screenshot({ path: `${shots}/01-catalog-tree.png` });

// Events: feature one
await page.goto(base + '/cat/events');
await page.locator('[data-event="1"]').click();
await page.locator('[data-event-detail]').waitFor();
await page.getByText('Featured', { exact: true }).click();
await page.getByRole('button', { name: 'Save' }).click();
await page.locator('[data-event="1"] .star').waitFor();
log('event featured');
await page.screenshot({ path: `${shots}/02-events.png` });

// Participants: Georgian names
await page.goto(base + '/cat/participants');
const dinamo = page.locator('[data-participant="1"] input.cell').first();
await dinamo.fill('დინამო თბილისი');
await dinamo.press('Tab');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText(/saved/).waitFor();
log('participant renamed');
await page.screenshot({ path: `${shots}/03-participants.png` });

// Translations: outcome templates with preview
await page.goto(base + '/i18n');
await page.getByRole('radio', { name: 'Outcome names' }).click();
const over = page.locator('[data-entity="2::12"] input.cell').first();
await over.waitFor();
await over.fill('მეტი {total}');
await over.press('Tab');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText(/translations saved/).waitFor();
await page.locator('[data-entity="2::12"] button').click();
await page.locator('[data-preview]').getByText('მეტი 2.5').waitFor();
log('template saved and previewed');
await page.screenshot({ path: `${shots}/04-translations.png` });
// Lint error
const under = page.locator('[data-entity="2::13"] input.cell').first();
await under.fill('ნაკლები {totl}');
await under.press('Tab');
await page.getByRole('button', { name: 'Save' }).click();
await page.getByText(/TEMPLATE_LINT/).waitFor();
log('template lint rejected a wrong placeholder');
console.log('errors:', JSON.stringify(errors.filter((e) => !e.includes('400'))));
await browser.close();
