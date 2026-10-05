# HANDOFF: სესიიდან სესიაზე გადასაცემი ინფორმაცია

> ბოლო განახლება: 2026-10-05, BO-1b-ის დასრულების შემდეგ.
> ახალი სესია ჯერ `/CLAUDE.md`-ს კითხულობს (წესები, რუკა, სტატუსი), შემდეგ ამ ფაილს: აქ არის ბოლო სამუშაოს დეტალები,
> ღია კითხვები, გარემოს მომზადების რეცეპტი და შემდეგი ნაბიჯის საწყისი წერტილები.
> სავალდებულო გადაწყვეტილებები ისევ docs/09 §2-შია, გადახვევები docs/09 §5-ში.

## 1. სად ვართ

- Branch: `claude/bettor-api-integration-hqk4e1`, ყველაფერი დაპუშულია, PR არ არსებობს (მომხმარებელს არ უთხოვია).
- ბოლო commit-ები:
  - `36fbfdd` BO-1b: Offer.Core price pipeline and ODDS API
  - `c507123` BO-1b: CMS reason-code messages with brand texts
  - `49eac29` BO-1b: back office pages for trading and messages
  - `85c3ba4` BO-1b: docs, test guide and project memory
- ტესტები: `Offer.Tests` 38, `Bo.Tests` 76, `Platform.Tests` 32, backoffice unit 6. ყველა მწვანე იყო. `ng build backoffice` და `feed-ops` სუფთაა.
  Bo.Api-ის Docker image sandbox-ში აიწყო.
- **არ შემოწმებულა:** სრული `docker compose` Keycloak-ით (მომხმარებლის გზა). UI შემოწმდა `ng serve` + Bo.Api-ზე
  `Auth__Enabled=false`-ით (§4). მომხმარებელს TESTING.md-ში B5/B6 სცენარები აქვს MacBook-ზე გასაშვებად.

## 2. BO-1b-ის რუკა (რა სად არის)

### Backend
| ფაილი | შინაარსი |
|---|---|
| `platform/src/Offer.Core/` | `OfferPricer.Price(market, settings, gate, overrides, now)`: ფასის ერთადერთი pipeline (docs/06 §4.2). `Margin` (power / proportional / Shin, remove + apply, delta), `OddsLadder` (std / fine / none, ყოველთვის floor), `OfferModels` (`MarketInput`, `PricingSettings`, `MarketGate`, `OddsOverride`, `PricedMarket`, `OfferStatus`). დამოკიდებულებები არ აქვს |
| `platform/tests/Offer.Tests/Golden/pricing.json` | golden შემთხვევები. მნიშვნელობები დამოუკიდებელი Python იმპლემენტაციით გადამოწმდა. ამ ფაილის შეცვლა ნიშნავს მოთამაშის ფასის შეცვლას |
| `platform/db/migrations/V007__bo_odds_cms.sql` | `sb.provider` id 0 `manual`, `bo.manual_entity` (+ `bo.owns_manual()`), `bo.odds_override`, `bo.trading_override`, `bo.message_def`; RLS. **`sb.market/outcome/market_description/_outcome/specifier_def`-ზე RLS** ზღუდავს მხოლოდ `bo_app`-ს (`current_user <> 'bo_app'` policy დანარჩენებს ხსნის) |
| `Bo.Api/Modules/Odds/OfferService.cs` | ივენთის შეთავაზების აწყობა: `sb` + CFG (`Resolve`) + trading/odds overrides → `OfferPricer`. `VisibleMarketSql`: სხვა ოპერატორის manual მარკეტს მალავს (გამოიყენება catalog-ის `openMarkets`-შიც) |
| `Bo.Api/Modules/Odds/OddsEndpoints.cs` | `/api/bo/odds/*` (ქვემოთ) + `TradingExpiryWorker` (15 წმ: ვადაგასული / settled / feed_change override-ები და ვადაგასული შეჩერებები; audit + outbox, tenant role-ის გარეშე) |
| `Bo.Api/Modules/Cms/CmsEndpoints.cs` | `/api/bo/cms/*`. `CmsMessages.RenderAsync(...)` აბრუნებს `{code, params, title, message, lang, source}`-ს, ეს bet API-სთვისაა (BO-2) |
| `Bo.Core/Cms/MessageCatalog.cs`, `MessageFormat.cs` | 23 reason code ka/en ტექსტით (start-ზე → `bo.message_def`). `{name}` / `{name, number}`, lint (უცნობი პარამეტრი, შიდა ტერმინები) |
| `Bo.Core/Config/SettingCatalog.cs` | ახალი key-ები: `odds.override_max_ttl_min` (json `{live:120, prematch:1440}`, ვალიდაცია `SettingValidator`-ში), `manual.market_live` |
| `platform/Dockerfile` | დაემატა `COPY src/Offer.Core/Offer.Core.csproj` |

### API
```
GET    /api/bo/odds/events/{id}/markets?lang=        trading view (EventOffer)
GET    /api/bo/odds/overrides?eventId=               აქტიური odds override-ები
POST   /api/bo/odds/overrides                        {marketId,outcomeCode,kind:absolute|shift_pct,value,clearOn:expiry|feed_change,ttlMinutes|expiresAt,reason}
DELETE /api/bo/odds/overrides/{id}?reason=
GET    /api/bo/odds/trading?eventId=                 აქტიური suspend/close
POST   /api/bo/odds/trading                          {scopeType:event|market,scopeId,action:suspend|close,ttlMinutes?,reason,platform?}
DELETE /api/bo/odds/trading/{id}?reason=             platform-ის row-ს მხოლოდ platform staff ხსნის
GET    /api/bo/odds/market-types/{id}/template       specifiers + outcomes manual-ის ფორმისთვის
POST   /api/bo/odds/events/{id}/manual-markets       {marketTypeId,specifiers,outcomes:[{code,odds}],status,reason}
PATCH  /api/bo/odds/manual-markets/{id}             {outcomes?,status?,reason?}
GET    /api/bo/odds/market-types?q=                  matrix (sports × types, market.enabled)
PUT    /api/bo/odds/market-types/{id}                {sportId|null,enabled,reason} → CFG change set
POST   /api/bo/odds/simulate                         {marketId? | outcomes, closedSet?, settings{mode,pct,...}}
GET    /api/bo/cms/messages?category=&q=&brandId=&missing=
PUT    /api/bo/cms/messages/{code}                   {texts:{ka:{title?,text?}}, brandId?, platform?}  ("" = წაშლა, null = უცვლელი)
GET    /api/bo/cms/messages/{code}/preview?lang=&brandId=&params=maxStake:150,currency:GEL
```
Permission-ები: `odds.view`, `odds.override`, `odds.suspend`, `cat.market.add_manual`, matrix-ისთვის `cfg.edit` + `odds.margin.edit`,
`cms.view`, `cms.edit`. `margin.*` key-ებს four-eyes სჭირდება, `market.enabled`-ს არა.

### Frontend (`admin/admin-web/projects/backoffice/src/app/features/`)
- `odds/events.ts` `/odds`, `odds/trading.ts` `/odds/events/:id` (live-ზე ყოველ 5 წამში ახლდება), `odds/overrides.ts`,
  `odds/market-types.ts`, `odds/margins.ts` (simulator), `odds/dialogs.ts` (override / trading / manual market),
  `odds/odds-format.ts` (+ spec).
- `cms/messages.ts` `/cms`.
- Nav: ჯგუფი „Trading“ + „Messages“ (`shell/shell.ts`). `@admin/ui` status-ს დაემატა `hidden`.

### ტესტები
- `Bo.Tests/OddsApiTests.cs`: margin four-eyes-ის შემდეგ, override TTL, expiry worker, suspend/close + platform, manual markets,
  **RLS პირდაპირ SQL-ზე** (BetGeo ვერ ხედავს / ვერ ცვლის Acme-ს row-ებს, `bo_app` feed-ის row-ს ვერ ცვლის), matrix, simulator.
- `Bo.Tests/CmsApiTests.cs`: default-ები, brand → operator → platform, lint, ყოველი catalog ტექსტის lint.
- Fixture-ში (`BoApiTests.cs` → `BoApiFixture`) დაემატა `FeedMarketId`, `TotalMarketId`, `OtherEventId`, `OtherMarketId`.
  ერთ კლასში ტესტები საერთო DB-ს იყენებს: თუ ტესტი state-ს ცვლის, ბოლოს თავად უნდა დააბრუნოს.

## 3. ღია კითხვები და ცნობილი ხარვეზები

1. **მომხმარებლისთვის (ჯერ არ უპასუხია):** feed-ის `bet_stop` feed ივენთზე manual მარკეტსაც აჩერებს. ამის შემდეგ ტრეიდერს
   ის ხელით უნდა გახსნას, რადგან feed მას `odds_change`-ით ვერ გახსნის. გინდათ ავტომატური გახსნა? ამისთვის adapter-მა
   `bet_stop`-ის წყარო უნდა დაიმახსოვროს.
2. Override-ის feed-იდან გადახრაზე NOTIF alert ჯერ არ არის, მხოლოდ API-ის warning და UI-ის შეტყობინება (BO-3).
3. ჯერ არ გაკეთებულა (P1/P2): price lock, boost hook, custom manual markets, market groups / display order,
   `market.max_lines`, CMS `map_to_code`, messages bundle endpoint (SB-0).
4. Manual მარკეტის settlement: `bo.manual_settlement` + bet-engine, BO-2 (docs/09 §2.4).
5. Expiry worker Bo.Api-შია. Bo.Workers-ში გადავა, როცა ეს სერვისი გაჩნდება.
6. Prettier-ით ფორმატირება frontend-ის არსებულ კოდზე არ არის გამოყენებული. ჩვენც არ დავაფორმატეთ, რომ diff არ გაბერილიყო.

## 4. გარემოს მომზადება cloud sandbox-ში (მომხმარებლისთვის არა)

```bash
# .NET 10: dot.net-ის install script დაბლოკილია (proxy 403), Ubuntu-ს პაკეტი მუშაობს
apt-get update && apt-get install -y dotnet-sdk-10.0
# Docker + PostgreSQL 18
rm -f /var/run/docker.pid; (nohup dockerd > $SCRATCH/dockerd.log 2>&1 &)
docker run -d --name platform-pg -p 55432:5432 -e POSTGRES_PASSWORD=platform postgres:18
# Node 24 (nodejs.org მისაწვდომია)
V=$(curl -sS https://nodejs.org/dist/index.json | python3 -c "import json,sys;print([r['version'] for r in json.load(sys.stdin) if r['version'].startswith('v24.')][0])")
curl -sSL https://nodejs.org/dist/$V/node-$V-linux-x64.tar.xz | tar xJ -C $SCRATCH && mv $SCRATCH/node-$V-linux-x64 $SCRATCH/node24
export PATH=$SCRATCH/node24/bin:$PATH
```
- ⚠ `pkill -f "Bo.Api"` ან სხვა ფართო pattern Bash tool-ის საკუთარ shell-საც კლავს (exit 144). PID-ით მოკალით.
- Docker build: context დააკოპირეთ `/tmp/claude-0/build`-ში, `/root/.ccr/ca-bundle.crt` → `.proxy-ca.crt`. `Dockerfile.sandbox`-ში
  `WORKDIR`-მდე ჩასვით `COPY .proxy-ca.crt /usr/local/share/ca-certificates/proxy.crt` + `RUN update-ca-certificates`.
  Build: `docker build --network host --build-arg HTTPS_PROXY=$HTTPS_PROXY --build-arg PROJECT=Bo.Api -f Dockerfile.sandbox -t uof-platform-bo-api .`
  ეს ფაილები არასდროს უნდა მოხვდეს commit-ში.

### UI-ის სწრაფი E2E simulator-ისა და Keycloak-ის გარეშე
```bash
PGPASSWORD=platform psql -h localhost -p 55432 -U postgres -c "CREATE DATABASE bo_e2e"
cd betradar-integration/platform
ConnectionStrings__Platform="Host=localhost;Port=55432;Database=bo_e2e;Username=postgres;Password=platform" \
  Auth__Enabled=false Bo__DevSeed=true Urls=http://127.0.0.1:8083 nohup dotnet run --project src/Bo.Api &
# როცა /healthz = ok:
PGPASSWORD=platform psql -h localhost -p 55432 -U postgres -d bo_e2e -f ../e2e/dev-seed.sql
cd ../admin/admin-web && nohup npx ng serve backoffice --port 4300 --proxy-config ../../e2e/dev-proxy.json &
cd ../../e2e && npm install && mkdir -p shots
CHROMIUM_PATH=/opt/pw-browsers/chromium-1194/chrome-linux/chrome node backoffice-odds-dev.mjs shots
# ხელახლა გასაშვებად: psql ... -d bo_e2e -f dev-reset.sql
```
Auth-ის გარეშე მომხმარებელი platform super admin-ია. სკრიპტი switcher-ში AcmeBet-ს ირჩევს.

## 5. შემდეგი ნაბიჯი: BO-1-ის დარჩენილი ნაწილი (CLAUDE.md „Next“ 1)

**Custom groups** (docs/06 §2.2, §2.5, DDL §8 `custom_group`, `custom_group_member`):
- ახალი მიგრაცია `V008`. `bo` ცხრილებს RLS და იზოლაციის ტესტი სჭირდება (CLAUDE.md-ის წესი).
- ჯგუფი არის ნაჩვენები ხე, CFG scope არ არის (docs/06 §1.2). ხილვადობა ისევ `offer.visible`-ით იმართება.
- UI-ში ალბათ `features/cat/tree.ts`-ის გვერდით ახალი ტაბი ან გვერდი.

**Manual ივენთები + შედეგის შეყვანა** (docs/06 §2.5.4, §1.1, docs/09 §2.4):
- `bo.manual_entity`-ის CHECK უკვე უშვებს `entity_type = 'event'`-ს. `sb.provider` id 0 უკვე არსებობს.
- `sb.event`, `sb.event_competitor` (და შესაძლოა `sb.competitor`) ცხრილებს V007-ის ნიმუშით სჭირდება `bo_app` RLS:
  INSERT/UPDATE მხოლოდ საკუთარ manual row-ზე. Mapping: `sb.provider_mapping(provider_id=0, 'event', 'bo:<uuid>')`.
- Manual ივენთის მარკეტები = უკვე არსებული manual-market ნაკადი (`POST /odds/events/{id}/manual-markets`).
  `OpenEventStatuses` და `VisibleMarketSql` ივენთის დონეზეც უნდა გაფართოვდეს: სხვა ოპერატორის manual ივენთი არ უნდა ჩანდეს
  `cat/events`-ში, `cfg/scopes`-ში და `cat/tree`-ის `openEvents`-ში.
- შედეგის შეყვანა four-eyes-ით → `bo.manual_settlement` (docs/07). settlement-service BO-2-შია, ამიტომ ახლა მხოლოდ ჩანაწერი
  და დადასტურება კეთდება.

შემდეგ: P1 market groups / display order (`bo.display_order`, `bo.market_group`), `market.max_lines` (Offer.Core-ში ან
OfferService-ში, `sb.market.is_favourite`-ის მიხედვით).
