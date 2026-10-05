# E2E (Playwright)

Browser checks against the running `platform/docker-compose.yml` stack. Screenshots go to the directory given as argument.

```bash
cd betradar-integration/e2e && npm install && npx playwright install chromium   # or CHROMIUM_PATH=/path/to/chromium
mkdir -p shots
node feedops.mjs shots        # Feed Ops: Keycloak login, scenario, producer outage, settlements (fresh DB: compose down -v)
node backoffice.mjs shots     # Back office: change set + four-eyes, isolation, new operator, invite with TOTP, catalogue
```

`backoffice.mjs` expects a fresh stack with one scenario played (the catalogue needs a league):
`docker compose down -v && docker compose up -d` and then
`curl -X POST localhost:8080/sim/scenarios/derby_settlement_rollback -H 'Content-Type: application/json' -d '{"speed":8}'`.

`backoffice-catalog-dev.mjs` drives `ng serve backoffice --port 4300` against a local Bo.Api with `Auth__Enabled=false`.

`backoffice-odds-dev.mjs` (BO-1b: trading view, override, suspend, manual market, market types, margin simulator, CMS) drives
the same `ng serve` setup. It needs one soccer match with a feed 1x2 (2.10 / 3.40 / 3.60) and total 2.5 (1.85 / 1.95);
the derby scenario of the simulator gives such a match, or insert those rows into `sb` by hand.
