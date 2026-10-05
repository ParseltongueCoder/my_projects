// End-to-end check of the operator back office (BO-0) against the docker-compose stack with Keycloak realm "bo".
import { createHmac } from 'node:crypto';
import { chromium } from 'playwright';

/** RFC 6238 TOTP (SHA-1, 6 digits, 30 s) from a base32 secret, as an authenticator app would compute it. */
function totp(secretBase32) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of secretBase32.replace(/\s+/g, '').toUpperCase()) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const h = createHmac('sha1', key).update(counter).digest();
  const o = h[h.length - 1] & 0xf;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1000000).padStart(6, '0');
}

const BASE = 'http://localhost:8089';
const SHOTS = process.argv[2];
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH, args: ['--no-sandbox'] } : {});
const log = (m) => console.log(`[e2e ${new Date().toISOString().slice(11, 19)}] ${m}`);
const errors = [];

async function session(username, password = `${username}-devpass`) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', (e) => errors.push(`${username}: ${e}`));
  page.on('console', (m) => m.type() === 'error' && errors.push(`${username}: ${m.text()}`));
  await page.goto(BASE);
  await page.waitForURL(/localhost:8180\/realms\/bo/, { timeout: 30000 });
  // Realms with Organizations use identity-first login: username, then password.
  await page.fill('#username', username);
  if (!(await page.locator('#password').isVisible())) {
    await page.click('#kc-login');
    await page.locator('#password').waitFor();
  }
  await page.fill('#password', password);
  await page.click('#kc-login');
  return { context, page };
}

async function settled(page) {
  await page.waitForURL(`${BASE}/**`, { timeout: 30000 });
  await page.locator('.user').waitFor({ timeout: 20000 });
}

// 1. Trader of AcmeBet stages a margin change on a league + market type (needs approval) and submits it.
{
  const { context, page } = await session('acme-trader');
  await settled(page);
  await page.getByRole('heading', { name: 'AcmeBet' }).waitFor();
  log('acme-trader logged in (organization acmebet)');
  await page.goto(`${BASE}/cfg/scope`);
  await page.getByRole('radio', { name: 'League' }).click();
  await page.locator('bo-scope-search input').fill('Premier');
  await page.getByRole('option', { name: /^tournament Sim Premier League/ }).click();
  await page.locator('bo-market-type-search input').fill('uof_1');
  await page.getByRole('option', { name: /^#1 / }).click();
  const row = page.locator('tr[data-key="margin.pct"]');
  await row.waitFor();
  await row.getByRole('button', { name: /Override/ }).click();
  await page.locator('bo-setting-value input').fill('0.04');
  await page.getByRole('button', { name: 'Stage change' }).click();
  await page.locator('.draft-bar').waitFor();
  await page.locator('.draft-bar input').fill('Lower 1x2 margin on Sim Premier League (derby weekend)');
  await page.screenshot({ path: `${SHOTS}/01-trader-staged.png` });
  await page.getByRole('button', { name: 'Submit for approval' }).click();
  await page.getByText(/waits for approval by a second user/).waitFor();
  log('trader submitted a change set that needs approval');
  await context.close();
}

// 2. Head trader approves it (four-eyes); effective config shows the value and why.
{
  const { context, page } = await session('acme-head');
  await settled(page);
  await page.goto(`${BASE}/cfg/change-sets`);
  const set = page.locator('[data-change-set]', { hasText: 'Lower 1x2 margin' });
  await set.waitFor();
  await page.screenshot({ path: `${SHOTS}/02-head-pending.png` });
  await set.getByRole('button', { name: 'Approve' }).click();
  await page.locator('mat-dialog-container textarea').fill('Agreed with risk team');
  await page.getByRole('button', { name: 'Approve and apply' }).click();
  await page.getByText(/applied/).first().waitFor();
  log('head trader approved; change set applied');

  await page.goto(`${BASE}/cfg/effective`);
  await page.locator('bo-scope-search input').fill('Premier');
  await page.getByRole('option', { name: /^tournament Sim Premier League/ }).click();
  await page.locator('bo-market-type-search input').fill('uof_1');
  await page.getByRole('option', { name: /^#1 / }).click();
  const eff = page.locator('tr[data-key="margin.pct"]');
  await eff.getByText('0.04').waitFor();
  await eff.click();
  await page.getByText('specificity 11').waitFor();
  await page.screenshot({ path: `${SHOTS}/03-effective-trace.png` });
  log('effective config: margin.pct = 0.04 decided at league + market type');
  await context.close();
}

// 3. Another operator sees none of it.
{
  const { context, page } = await session('betgeo-admin');
  await settled(page);
  await page.getByRole('heading', { name: 'BetGeo' }).waitFor();
  await page.goto(`${BASE}/cfg/change-sets`);
  await page.getByRole('radio', { name: 'All' }).click();
  await page.getByText('No change sets yet').waitFor();
  await page.goto(`${BASE}/cfg/effective`);
  await page.locator('bo-scope-search input').fill('Premier');
  await page.getByRole('option', { name: /^tournament Sim Premier League/ }).click();
  await page.locator('tr[data-key="margin.pct"]').getByText('0.06').waitFor();
  log('betgeo-admin: no Acme change sets, margin still the default 0.06');
  await page.screenshot({ path: `${SHOTS}/04-betgeo-isolated.png` });
  await context.close();
}

// 4. Platform staff: act as AcmeBet, see the audit trail, create an operator and invite its admin.
let tempPassword;
{
  const { context, page } = await session('platform');
  await settled(page);
  await page.getByText('Platform level').waitFor();
  await page.locator('.switcher').click();
  await page.getByRole('option', { name: 'AcmeBet' }).click();
  await page.getByText('Acting as AcmeBet').waitFor();
  await page.goto(`${BASE}/adm/audit`);
  await page.getByText('cfg.change_set.approved').first().waitFor();
  await page.screenshot({ path: `${SHOTS}/05-platform-audit.png` });
  log('platform staff acting as AcmeBet sees the audit trail');

  await page.goto(`${BASE}/platform/operators`);
  await page.getByRole('button', { name: 'New operator' }).click();
  await page.getByLabel('Code').fill('newbet');
  await page.getByLabel('Name').fill('NewBet');
  await page.getByRole('button', { name: 'Create' }).click();
  await page.locator('[data-operator="newbet"]').waitFor();
  log('operator newbet created (Keycloak organization + default brand)');
  await page.locator('[data-operator="newbet"]').getByRole('button', { name: 'Open' }).click();
  await page.getByText('Acting as NewBet').waitFor();
  await page.getByRole('heading', { name: 'Brands & modules' }).waitFor();
  await page.screenshot({ path: `${SHOTS}/06-newbet-sites.png` });

  await page.goto(`${BASE}/adm/users`);
  await page.getByRole('button', { name: 'Invite user' }).click();
  await page.getByLabel('Username').fill('newbet-admin');
  await page.getByLabel('Email').fill('admin@newbet.test');
  await page.getByLabel('Display name').fill('NewBet Admin');
  await page.getByLabel('Roles').click();
  await page.getByRole('option', { name: /Operator admin/ }).click();
  // Close the multi-select panel by its backdrop (Escape would also close the dialog).
  await page.locator('.cdk-overlay-backdrop').last().click();
  await page.getByRole('button', { name: 'Invite', exact: true }).click();
  const notice = page.locator('.notice .mono');
  await notice.waitFor().catch(async (e) => {
    await page.screenshot({ path: `${SHOTS}/fail-invite.png` });
    throw e;
  });
  tempPassword = (await notice.textContent()).trim();
  await page.screenshot({ path: `${SHOTS}/07-invited.png` });
  log('newbet-admin invited (Keycloak user in organization newbet)');
  await context.close();
}

// 5. The invited admin logs in with the temporary password, sets a new one and lands in NewBet.
{
  const { context, page } = await session('newbet-admin', tempPassword);
  // Required actions of an invited user: enrol TOTP (2FA), then replace the temporary password.
  await page.getByText('Mobile Authenticator Setup').waitFor({ timeout: 20000 });
  await page.screenshot({ path: `${SHOTS}/08-first-login-totp.png` });
  await page.getByText('Unable to scan?').click();
  const secret = (await page.locator('#kc-totp-secret-key').textContent()).trim();
  await page.fill('#totp', totp(secret));
  await page.locator('#saveTOTPBtn, input[type=submit]').first().click();
  log('TOTP enrolled with a computed one-time code');
  await page.locator('#password-new').waitFor({ timeout: 20000 });
  await page.fill('#password-new', 'NewBet-admin-2026!');
  await page.fill('#password-confirm', 'NewBet-admin-2026!');
  await page.locator('input[type=submit], button[type=submit]').first().click();
  await settled(page);
  await page.getByRole('heading', { name: 'NewBet' }).waitFor();
  await page.screenshot({ path: `${SHOTS}/09-newbet-admin-home.png` });
  log('invited admin logged in to NewBet after setting a password');
  await context.close();
}

// 6. BO-1: AcmeBet admin renames the league in Georgian and uploads a country flag (served through nginx /api/media).
{
  const { context, page } = await session('acme-admin');
  await settled(page);
  await page.goto(`${BASE}/cat`);
  await page.getByRole('button', { name: 'Expand all' }).waitFor();
  await page.locator('.tree .row').first().waitFor();
  await page.getByRole('button', { name: 'Expand all' }).click();
  const league = page.locator('.row', { hasText: 'Sim Premier League' }).first();
  await league.hover();
  await league.getByRole('button', { name: 'Rename' }).click();
  await page.locator('input.rename').fill('სიმ პრემიერ ლიგა');
  await page.locator('input.rename').press('Enter');
  await page.locator('.row', { hasText: 'სიმ პრემიერ ლიგა' }).waitFor();
  const country = page.locator('.row[data-node^="category-"]').first();
  await country.locator('bo-media-picker input[type=file]').setInputFiles({
    name: 'flag.svg', mimeType: 'image/svg+xml',
    buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 20"><rect width="30" height="20" fill="#fff"/><rect x="13" width="4" height="20" fill="#e00"/><rect y="8" width="30" height="4" fill="#e00"/></svg>'),
  });
  const img = country.locator('bo-media-picker img');
  await img.waitFor();
  await page.waitForFunction((el) => el.complete && el.naturalWidth > 0, await img.elementHandle());
  await page.screenshot({ path: `${SHOTS}/10-catalog.png` });
  log('catalog: league renamed in Georgian, flag uploaded and served');
  await context.close();
}

console.log(errors.length ? `BROWSER ERRORS:\n${errors.join('\n')}` : 'no browser errors');
await browser.close();
