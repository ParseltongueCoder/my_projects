# Project memory (read first)

Georgian B2B iGaming startup building a **full sportsbook engine** for operators (their PAM connects via API).
All real work lives in `betradar-integration/`. The user writes Georgian; answer in Georgian, docs in Georgian
(identifiers/code in English). The user tests on a **MacBook** (Apple Silicon, Docker Desktop, often a corporate network).

Branch: `claude/bettor-api-integration-hqk4e1` — commit and push there; no PR unless asked.

## Rules that must not be broken
- **Sportradar licence:** never copy SDK sources, XSDs or sample XML into the repo. XSDs only via `UOF_XSD_DIR` (local).
- **BO never edits `sb` rows** (feed truth). Operator data goes to `bo.*` (overrides, settings, translations) — docs/09 §2.
- **Tenant isolation = PostgreSQL RLS** (`SET LOCAL ROLE bo_app` + `app.operator_id`, see `Bo.Api/Infrastructure/BoDb.cs`).
  Every new `bo` table needs RLS policies like V005/V006 and a test that another operator cannot see/change it.
- **CFG is the only config mechanism** (`bo.setting`, catalog in `Bo.Core/Config/SettingCatalog.cs`). Visibility,
  limits, margins, cash-out, referral are settings, changed only through change sets (four-eyes for `requires_approval`).
- Every BO write: audit row + outbox row in the same transaction (`Audit.WriteAsync` / `Audit.OutboxAsync`).
- Frontend: Angular 22 + Angular Material (not PrimeNG: commercial licence, ADR-002). Self-hosted fonts.
- Don't put model names in commits/docs. Commit trailer is given by the session.

## Map
| Path | What |
|---|---|
| `betradar-integration/README.md` | master plan, decisions table, 14-week plan |
| `betradar-integration/docs/01-04` | roadmap, UOF data model + canonical DDL, simulator, platform architecture |
| `betradar-integration/docs/05-11` | operator back office design: research, CAT/I18N/ODDS/CFG/CMS, BET/LIM/CASH/CUS/INT, ADM/REP/PROMO, **09 = plan + binding decisions + status**, 10 = bet monitoring/referral, 11 = Player API + white-label |
| `betradar-integration/TESTING.md` | how the user runs and tests everything (Feed Ops §1-6, back office §7 B1-B4) |
| `uof-simulator/` | .NET UOF simulator (RabbitMQ + mock Betradar API, YAML scenarios) |
| `platform/` | `Platform.Canonical` (sb schema, migrations `db/migrations/V001..`), `Uof.Adapter`, `Admin.Api` (Feed Ops), **`Bo.Core` + `Bo.Api`** (back office), **`Offer.Core`** (the one price pipeline), tests `Platform.Tests`, `Bo.Tests`, `Offer.Tests` |
| `admin/admin-web/` | Angular workspace: `feed-ops`, **`backoffice`**, shared `@admin/ui` |
| `platform/deploy/keycloak/` | realms `feedops` and `bo` (Organizations = operators; dev passwords `<user>-devpass`) |

## Build, test, run
```bash
# DB tests need a PostgreSQL 18 server:
docker run -d --name platform-pg -p 55432:5432 -e POSTGRES_PASSWORD=platform postgres:18
cd betradar-integration/platform
PLATFORM_TEST_PG="Host=localhost;Port=55432;Username=postgres;Password=platform" dotnet test tests/Bo.Tests
PLATFORM_TEST_PG=... dotnet test tests/Platform.Tests
dotnet test tests/Offer.Tests   # pricing golden cases, no DB
cd ../admin/admin-web && npm ci && npx ng test backoffice --watch=false && npx ng build backoffice   # Node >= 24
# Full stack (user's way): cd betradar-integration/platform && ../uof-simulator/tools/gen-tls.sh && docker compose up -d --build
#   Feed Ops http://localhost:8088, back office http://localhost:8089, Keycloak :8180, Bo.Api :8083
```
Cloud-sandbox notes (not for the user): dockerd may need `rm -f /var/run/docker.pid; dockerd &`; Node 24 is in the
session scratchpad (`node24/bin`); Docker builds need the proxy CA — copy contexts to `/tmp/claude-0/build`, add
`COPY .proxy-ca.crt` lines in a `Dockerfile.sandbox` (never commit them), tag as `uof-platform-<service>`, then
`docker compose up -d --no-build`. Playwright: `chromium.launch({ executablePath: '/opt/pw-browsers/chromium' })`;
Keycloak `bo` login is identity-first (username, then password); invited users must enrol TOTP.

## Status (2026-10-05)
- Phase 1 done: simulator, adapter, canonical DB, monitoring, Feed Ops admin.
- Back office **BO-0 done** (tenants/brands, Keycloak orgs, RBAC 12 roles, audit, outbox→pg_notify, CFG engine with
  trace + change sets + four-eyes) and **BO-1a done** (catalog tree, events overrides, participants, translations with
  template lint/preview + CSV, media in DB at `/api/media/{id}`).
- **BO-1b done**: `Offer.Core` pipeline (power/proportional/Shin, feed/target/delta, ladder down, min/max, sanity floor,
  status precedence; golden tests), ODDS API + pages (trading view, overrides with TTL, suspend/close incl. platform-wide,
  manual markets from templates as `manual:{op}:{code}`, market-type matrix over `market.enabled`, simulator, expiry
  worker), CMS reason codes (ka/en defaults, operator/brand texts, lint, preview). V007 also restricts `bo_app` in `sb`
  to its own manual rows (RLS). Deviations: docs/09 §5 "BO-1b". E2E scripts: `betradar-integration/e2e/`.
- User decisions: Player API first, white-label later; counter-offer P0 (30 s for the player); referral timeouts live
  30 s / prematch 180 s, market close → auto-cancel, player cannot withdraw; odds format `2.50`. Legal questions open (docs/09 §6).

## Next (in order)
1. **BO-1 rest**: custom groups, manual events (+ result entry); then P1 market groups / display order, `market.max_lines`.
2. **SB-0/SB-1**: Player API skeleton (`/v1` catalog/events/event, OpenAPI, tenant via API key/brand host) on top of the
   same overlay and `Offer.Core` (docs/11); messages bundle endpoint for CMS.
3. BO-2: PAM simulator + INT contract, bet engine (uses `Offer.Core` + `CmsMessages`), manual settlement, LIM/liability,
   tickets, customers.
