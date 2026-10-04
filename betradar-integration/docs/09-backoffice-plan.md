# 09 — ოპერატორის Sportsbook Back Office: შემაჯამებელი გეგმა

> სტატუსი: დიზაინის ეტაპი · 2026-10-04
> B2B მოდელი: ოპერატორი ყიდულობს **სრულ sportsbook ძრავას**; ოპერატორის **PAM** (მოთამაშე, wallet, KYC) უერთდება API-ით.
>
> ეს დოკუმენტი აერთიანებს docs/05–08-ს. სადაც ისინი ერთმანეთს ეწინააღმდეგება, **§2-ის გადაწყვეტილებები სავალდებულოა** და იმ დოკუმენტებზე მაღლა დგას.

| დოკ. | შინაარსი |
|---|---|
| [05](05-backoffice-market-research.md) | ბაზრის კვლევა: 15+ vendor, მოდულების ტაქსონომია, gap analysis, საქართველოს რეგულაცია |
| [06](06-backoffice-catalog-odds-config.md) | CAT, I18N, ODDS, CFG, CMS + ცვლილებების გავრცელება + `bo` DDL |
| [07](07-backoffice-trading-risk-customers.md) | BET, LIM, CASH, CUS, INT (PAM კონტრაქტი) + `bet` DDL |
| [08](08-backoffice-platform-reporting-promo.md) | BO არქიტექტურა, sitemap, ADM, REP, PROMO, NOTIF |
| [11](11-sportsbook-api-and-whitelabel.md) | Player API (`/v1` + WebSocket), white-label frontend (Angular, iFrame + full-page), postMessage პროტოკოლი, theming, operator onboarding |
| [10](10-backoffice-bet-monitoring.md) | MON — ბილეთების მონიტორინგის დეშბორდები, referral (ხელით დადასტურება), real-time არქიტექტურა |

## 1. მოდულების რუკა

| კოდი | მოდული | მომხმარებლის მოთხოვნა | დამატებით რეკომენდებული |
|---|---|---|---|
| CAT | კატალოგი, ივენთები, მონაწილეები, media | კატეგორიები, ივენთები, participants, აიქონები, ივენთის დამატება კატეგორიაში | custom ჯგუფები („ქართული ფეხბურთი“), top leagues, დუბლიკატების merge, manual ივენთის feed-თან მიბმა |
| I18N | თარგმნები | ხელით თარგმნა | market/outcome **template**-ების თარგმნა, missing-translation რიგი, CSV/XLIFF |
| ODDS | კოეფიციენტები, მარკეტები | odds მართვა, manual მარკეტები | margin pipeline (power method), odds ladder, override-ი ვადით, margin simulator |
| CFG | კონფიგურაცია | სპორტი/ქვეყანა/ლიგა/მარკეტის დონე | „რატომ არის ეს მნიშვნელობა X“ trace, change set + approval, დაგეგმილი ცვლილება |
| CMS | შეტყობინებები | ერორ მესიჯები | reason code → თარგმნილი ტექსტი პარამეტრებით (`max_allowed_stake`) |
| BET | ბილეთები, settlement | ბილეთების ძებნა | live bet delay, odds-change პოლიტიკა, manual settlement / resettlement / void, bulk cancel |
| LIM | ლიმიტები, რისკი | ლიმიტები მარკეტ/ივენთზე | risk group + stake factor, **liability მონიტორინგი**, effective limit trace |
| CASH | Cash-out | ჩართვა/გამორთვა მარკეტზე/კატეგორიაზე | kill switch, risk group-ის მიხედვით, partial cash-out (P1) |
| CUS | მომხმარებლები | სია, დეტალური გვერდი, ლიმიტები, შეზღუდვები | 14 შეზღუდვის ტიპი, tags/flags (sharp, arbitrage, bonus abuser), შენიშვნები |
| REP | რეპორტები, სტატისტიკა | სტატისტიკა, რეპორტები | KPI dashboard, scheduled exports, **საქართველოს რეგულატორული რეპორტი** |
| ADM | ადმინ იუზერები | ადმინების მართვა | `module.action` permission-ები, TOTP, four-eyes, audit log viewer |
| PROMO | კამპანიები, freebet | კამპანია + ფრიბეთი | odds boost, acca boost, cashback (P1), abuse prevention |
| MON | ბილეთების მონიტორინგი | დეშბორდები ფილტრებით, მონიტორინგზე წამოსული ბილეთის შემოწმება და ხელით დადასტურება | ტრეიდერის ბარათი (ისტორია, liability ფსონამდე და ფსონის შემდეგ, მსგავსი ფსონები), claim/steal, ნაწილობრივი დადასტურება, timeout პოლიტიკა, ხმოვანი სიგნალები, multi-monitor |
| WL / API | მოთამაშის არხები | API დიდი ოპერატორებისთვის, white-label iFrame პატარებისთვის | ერთი `/v1` API ორივე არხისთვის, ბრენდის თემა live preview-ით, sandbox სიმულატორებზე, docs portal, certification |
| NOTIF | Alert-ები | — | დიდი ფსონი, liability ზღვარი, feed/PAM პრობლემა; in-app, Telegram, email |
| INT | PAM ინტეგრაცია | — | idempotent wallet API, outbox + reconciliation, webhooks, **PAM სიმულატორი** |

## 2. სავალდებულო გადაწყვეტილებები (05–08-ის შეთანხმება)

1. **Tenancy: `operator` → `brand`.** საქართველოში 2024-12-01-დან ორი დომენი სჭირდება ოპერატორს: ერთი ქართველი მოთამაშეებისთვის, მეორე მხოლოდ უცხოელებისთვის (05 §5). ამიტომ ოპერატორის ქვეშ ემატება `bo.brand` (საიტი/დომენი). Scope-ის ჯაჭვი ასეთია: `platform → operator → brand → sport → category → tournament → event → market`. `brand` არასავალდებულო დონეა: თუ ოპერატორს ერთი საიტი აქვს, `brand`-ის მნიშვნელობები ცარიელი რჩება. Bet-ი და customer-ი ინახავს `brand_id`-ს.
2. **`market_type` არის qualifier და არა ცალკე დონე** (06 §5.3). ასეთი პარამეტრი შეიძლება ნებისმიერ დონეზე დაყენდეს, მაგალითად „ფეხბურთი + market type 18“ ან „Premier League + market 1“. პრიორიტეტი ჯერ დონის სიღრმით განისაზღვრება, თანაბარ სიღრმეზე კი qualifier-იანი მნიშვნელობა იგებს. 07-ში სადაც წერია `… → event → market_type → market`, იგულისხმება ეს წესი.
3. **CFG ერთადერთი კონფიგურაციის მექანიზმია.** ლიმიტები (`limit.*`), cash-out (`cashout.*`), bet delay (`betdelay.*`) და margin (`margin.*`) ყველა `bo.setting`-შია. 06-ის catalog (§5.8) არის ძირითადი, ხოლო 07 §9-ის key-ები მას ემატება. სახელების დამთხვევისას 06-ის სახელი იგებს.
4. **Manual settlement.** ერთადერთი წყაროა `bo.manual_settlement` (07). ეს ეხება როგორც feed მარკეტზე trader-ის გადაწყვეტილებას, ისე manual ივენთის შედეგს. settlement-service ამ ჩანაწერს feed settlement-ის ტოლფასად ამუშავებს და აქვეყნებს `canon.settlement`-ზე. **06 §2.5/§9-ის `sb.settlement producer_id=0` ჩანაწერი აღარ გამოიყენება.** BO `sb`-ში მხოლოდ manual entity-ების row-ებს ქმნის (provider `manual`, id 0).
5. **Tenant isolation: EF global filter + PostgreSQL RLS** (08 §1). `app.operator_id` ტრანზაქციის დონეზე ყენდება; თუ არ არის დაყენებული, მოთხოვნა row-ებს ვერ ხედავს (fail-closed). ეს ეხება `bo`, `bet` და `promo` სქემებს. ორ ოპერატორს შორის იზოლაციის ტესტები P0-ია.
6. **Platform row-ებისთვის `operator_id IS NULL`** (არა 0). `operator_id` და `brand_id` არის `bigint`, ხოლო admin user-ის id არის Keycloak-ის `sub` (uuid).
7. **BO-დან void, resettle და cancel bet-engine-ის command API-ით სრულდება** (`POST /internal/bets/{id}/void` და ა.შ.). საერთო DB ტრანზაქცია არ გამოიყენება. ასე wallet-ის მოძრაობა და idempotency ერთ ადგილას რჩება.
8. **ფასი ერთ ადგილას ითვლება.** `libs/offer-core` (06 §4.2) გამოიყენება distribution API-შიც, bet-engine-შიც და cash-out-ის ფასისთვისაც (07 §5). ყველა bet ინახავს `config_version`-ს.
9. **Manual odds იყენებს ფარდობით offset-ს** (05-ის რეკომენდაცია, OddsMatrix-ის მსგავსი). ფიქსირებული ფასი ცალკე რეჟიმია. თუ override feed-ს X%-ით დაშორდა, ჩაირთვება NOTIF alert.
10. **ყოველ ბილეთზე ინახება რეგულატორული snapshot.** ბილეთის მიღებისას შემოწმდება ასაკი (25+), აკრძალულ პირთა რეესტრი და self-exclusion (PAM-დან), და შედეგი ბილეთთან ერთად ჩაიწერება (`bet.ticket.eligibility jsonb`).
11. **FX კურსები:** `bo.fx_rate` ცხრილი, წყარო ეროვნული ბანკის (NBG) დღიური კურსი. ცხრილს REP/platform ფლობს, ის გამოიყენება ლიმიტების base currency-ში გადასაყვანად და რეპორტებში.
12. **`bet.customer_stats_daily`** REP-ის rollup-ებს ეკუთვნის (08 §4). CUS მას მხოლოდ კითხულობს.
13. **Keycloak:** ერთი realm `bo`, ოპერატორები მასში Organizations-ად არიან. ის `feedops` realm-ისგან ცალკეა. დეტალური permission-ები ჩვენს DB-შია (08 §3).
14. **Sportradar MTS ოფციური ნაბიჯია.** თუ ოპერატორს საკუთარი MTS აქვს, bet pipeline-ის liability ნაბიჯის შემდეგ შეიძლება ჩაერთოს გარე acceptance-ის ნაბიჯი (P1, ⚠ ოპერატორის კონტრაქტზეა დამოკიდებული).
15. **MON და referral (docs/10):** referral-ის წყაროა `bet.referral`, რომელსაც bet-engine ფლობს. timer-ები მხოლოდ სერვერზე მუშაობს. docs/10 ცვლის 07-ის შემდეგ ნაწილებს: §2.7, `bet.referral` DDL, `referral.*` key-ები, `bet.referral.decide` permission (ახლა `referral.decide`) და `bo.risk_group.refer_all_bets` (ახლა `referral.risk_groups`). 08-ის sitemap-ში `/bet/pending-review` გადადის `/mon/*`-ზე. ემატება როლი `head_trader`, რომელიც 06-ის `op_head_trader`-სა და 07-ის `senior_trader`-ს აერთიანებს. NATS subject-ები: `bet.{op}.*`. CFG catalog-ს (06 §5.8) ემატება docs/10-ის `referral.*` და `monitor.*` key-ები.
16. **მოთამაშის არხები (docs/11):** white-label frontend იგივე საჯარო `/v1` Player API-ის პირველი კლიენტია; ცალკე „შიდა“ API არ არსებობს. ტოკენი მხოლოდ მეხსიერებაში ინახება (cookie-ების გარეშე) და iFrame-ს postMessage-ით გადაეცემა, URL-ით არასდროს. docs/11 ცვლის 07 §2.8/§5.5-ის player endpoint-ებს. მოთამაშისთვის საჯარო სტატუსია `pending_review` (შიდა `referred`). CMS-ის თარგმანებსა და შეტყობინებების override-ებს ემატება `brand` განზომილება.

## 3. სისტემის სურათი

```
                ┌──────────── Operator BO (Angular, projects/backoffice) ────────────┐
                │  CAT I18N ODDS CFG CMS │ BET LIM CASH CUS │ REP ADM PROMO NOTIF  │
                └───────────────┬─────────────────────────────────────────────────────┘
                                │ /api/bo/*  (Keycloak realm bo, Organizations = operators)
                ┌───────────────▼──────────────┐      outbox → NATS (bo.changed.*, bet.*)
UOF adapter ──► │ Bo.Api (modular monolith)     │──────────────┬───────────────────────┐
  (sb schema)   │ Bo.Workers (rollups, alerts)  │              │                       │
                └───────────────┬──────────────┘              ▼                       ▼
                                │                    distribution-api           bet-engine ──► PAM (operator)
                          PostgreSQL 18                (offer-core overlay,      (offer-core,     /pam/v1 wallet
                     sb · bo · bet · promo · rep        WebSocket → frontend)     Valkey liability)
```

## 4. P0 — რა სჭირდება პირველ ოპერატორის pilot-ს

| მოდული | P0 |
|---|---|
| საფუძველი | BO shell + operator/brand switcher, Bo.Api ჩონჩხი, outbox, RLS + იზოლაციის ტესტები, audit log |
| ADM | `bo` realm + Organizations, იუზერის მოწვევა და გათიშვა, 8 ოპერატორის როლი, permission catalog, TOTP, audit viewer |
| CFG | setting_def/setting, resolver, snapshot + NATS invalidation, scope editor, effective-value trace, change set + history |
| CAT | ხე (ხილვადობა, რიგი, სახელის შეცვლა, top), ივენთების ძებნა + override, media, manual match/outright ივენთები შედეგის შეყვანით, participant-ის თარგმანი/ლოგო |
| I18N | ka/en/ru, grid editor, template-ების თარგმნა preview-ით, CSV |
| ODDS | margin pipeline (feed/target/delta), ladder, min/max, suspend/close, override ვადით, market type matrix, manual მარკეტები template-ით |
| CMS | reason code-ების ტექსტები bet API-ის პასუხში |
| BET | single/multi/system, odds პოლიტიკა, live delay, feed settlement/rollback/resettle/cancel, ticket search + detail, void/resettle/manual settle |
| LIM | ყველა `limit.*`, risk group × stake factor, customer override, Valkey liability + ეკრანი + alert |
| CASH | cash-out ფასი, ჩართვა/გამორთვა scope-ით, kill switch |
| CUS | PAM mirror, პროფილი, risk group, შეზღუდვები, სია + დეტალური გვერდი |
| INT | `/pam/v1` (validate, debit, credit, rollback, status), webhooks, HMAC, outbox, reconciliation, **PAM სიმულატორი** |
| REP | KPI dashboard, 8 ძირითადი რეპორტი, CSV, rollup + reconciliation, საქართველოს bet register ⚠ |
| MON | ticker ყველა ფილტრით, referral რიგი + გადაწყვეტილების ბარათი, claim/steal, accept / **counter-offer** / reject (+ bulk reject), timeout და auto-cancel, დიდი ფსონები, მატჩის drill-down, watchlist, უარყოფილი ფსონების ნაკადი, შენახული ხედები, ხმები |
| NOTIF | დიდი ფსონი, liability, feed down, PAM error, referral backlog/SLA; in-app + Telegram |
| PROMO | standard freebet + manual/CSV დარიცხვა (თუ pilot-ს გაშვებისთანავე სჭირდება) |

P1: four-eyes approval-ები, odds/acca boost, კამპანიების builder, abuse detection, partial cash-out, scheduled რეპორტები.
P2: ML risk scoring, early payout, bet builder, ClickHouse, retail.

## 5. შესრულების თანმიმდევრობა

ბლოკები ერთმანეთზეა დამოკიდებული და ამ თანმიმდევრობით უნდა შესრულდეს. ვადები უხეში შეფასებაა, 3–4 დეველოპერიან გუნდზე.

| ეტაპი | შინაარსი | რას ვაჩვენებთ ბოლოს | ~კვირა |
|---|---|---|---|
| **BO-0 საფუძველი** ✅ | Bo.Api + Bo.Workers ჩონჩხი, `bo.operator/brand`, Keycloak `bo` realm, permission-ები, audit + outbox, RLS, Angular `backoffice` shell, CFG ძრავა | ორი ოპერატორი ერთმანეთის მონაცემებს ვერ ხედავს; პარამეტრი იცვლება და trace ჩანს | 3–4 |
| **BO-1 შეთავაზება** | CAT, I18N, CMS, ODDS + `offer-core`, distribution API (overlay + WebSocket) | ოპერატორი ცვლის margin-ს და ლიგის სახელს, frontend-ში 1 წამში აისახება | 4–5 |
| **BO-2 ფსონი და ფული** | PAM სიმულატორი (როგორც UOF სიმულატორი), INT, bet-engine (pipeline, settlement), LIM + liability, ticket search, CUS | სიმულატორის მატჩზე ფსონი იდება, სეტლდება, rollback-ზე resettle ხდება, PAM-ის ბალანსი სწორია | 6–8 |
| **BO-3 რისკი, მონიტორინგი, ანალიტიკა** | MON (ticker, referral რიგი), CASH, REP rollup-ები + dashboard, NOTIF | დიდი ფსონი მიდის referral-ზე, ტრეიდერი სთავაზობს counter-offer-ს და მოთამაშე ადასტურებს; cash-out live მატჩზე; GGR რეპორტი | 5–6 |
| **BO-4 Promo და რეგულაცია** | freebet ledger + დარიცხვა, საქართველოს რეპორტები, ასაკისა და რეესტრის შემოწმება | freebet-ით დადებული და მოგებული ბილეთი; რეგულატორული export | 3–4 |

**პარალელური ნაკადი SB-0…SB-4** (docs/11 §10): მოთამაშის ფენა (Player API, WebSocket, white-label, iFrame, theming) თითო BO ეტაპის გვერდით მიდის. მოცულობა დაახლოებით 34–40 დევ-კვირაა.

| გუნდი | pilot-მდე |
|---|---|
| 3–4 დეველოპერი (მხოლოდ BO) | ~6 თვე, მოთამაშის ფენის გარეშე |
| იგივე გუნდი + მოთამაშის ფენა | **~8–9 თვე** |
| **+2 frontend (Angular, მობილური) + 0.5 backend (რეკომენდებულია)** | **~6.5–7 თვე** |
| ✅ **არჩეულია (2026-10-04): ჯერ API, შემდეგ white-label.** White-label (SB-1/2-ის UI, SB-4-ის theming, დაახლოებით 15 დევ-კვირა) pilot-ის შემდეგ კეთდება | **~7–7.5 თვე** იმავე გუნდით; **~6–6.5 თვე** +1 frontend/backend დეველოპერით | პარალელურად მიმდინარეობს Sportradar-ის integration environment-ზე გადასვლა (ფაზა 2, README).

### BO-0: რა გაკეთდა და სად გადავუხვიეთ დიზაინს (2026-10-04)

აშენდა: `V005` (RLS), `Bo.Core`, `Bo.Api`, `projects/backoffice`, Keycloak realm `bo` Organizations-ით, compose, E2E (ტრეიდერი → four-eyes → effective trace → სხვა ოპერატორის იზოლაცია → ახალი ოპერატორი → მოწვევა TOTP-ით).

| დიზაინი | ახლა | რატომ / როდის |
|---|---|---|
| EF Core, `DbContext` თითო მოდულზე (08 §1.3) | Dapper + RLS | იგივე სტილი, რაც `Platform.Canonical`; RLS იზოლაციას ისედაც უზრუნველყოფს. EF — თუ მოდულების რაოდენობა მოითხოვს |
| თითო მოდული ცალკე project | `Bo.Api/Modules/*` საქაღალდეები + `Bo.Core` | ჯერ სამი მოდულია; პროექტებად დაყოფა BO-1-ში, NetArchTest-თან ერთად |
| Outbox → NATS | Outbox → PostgreSQL NOTIFY | NATS ჯერ არ არის გაშლილი (docs/04); relay-ის შეცვლა ერთ ადგილას ხდება |
| Settings snapshot Valkey-ში | in-process snapshot, `config_version`-ით ვალიდირებული | ერთი სერვისი კითხულობს; Valkey — როცა bet-engine/distribution დაემატება |
| Scheduling, rollback (P1) | არა | P1 |

## 6. ღია საკითხები: ბიზნესი და იურისტი (კოდს ბლოკავს)

1. ✅ **მოთამაშის frontend** (გადაწყდა 2026-10-04): ორივე არხი, **ჯერ API, შემდეგ white-label**. Odds ფორმატი: decimal, წერტილით (`2.50`). დიდ ოპერატორებს ვაძლევთ **Player API**-ს, რადგან მათ საკუთარი frontend აქვთ. პატარა ოპერატორებს ვაძლევთ **white-label frontend**-ს (iFrame), რომელსაც კონფიგურაციით თავიანთ დიზაინს მოარგებენ. დიზაინი: [docs/11](11-sportsbook-api-and-whitelabel.md).
2. **PAM:** pilot ოპერატორის PAM უჭერს მხარს reserve/commit-ს? adapter-ს ვინ წერს? შეიძლება თუ არა resettlement-მა მოთამაშის ბალანსი უარყოფითზე ჩამოიყვანოს?
3. **Settlement:** ვიხდით `certainty=1`-ზე, თუ ველოდებით დადასტურებას? როგორ ვამრგვალებთ გადასახადს (floor თუ banker's rounding)?
4. **Cash-out:** default margin 5% მისაღებია? თუ market cash-out-ის შემდეგ void-დება, cash-out ძალაში რჩება?
5. **⚠ იურისტი:**
   - საქართველოში სერვერის განთავსების მოთხოვნა: თუ სავალდებულოა, Hetzner-ის გეგმა იცვლება;
   - Revenue Service-ის რეპორტის ფორმატი და შენახვის ვადა;
   - ბონუსებისა და ფრიბეთების დაშვებული ტიპები;
   - გადასახადები: წყაროები ერთმანეთს ეწინააღმდეგება;
   - username პერსონალურ მონაცემად ითვლება?
6. **Trading:** ოპერატორი საკუთარ Sportradar MTS-ს გამოიყენებს თუ ჩვენს LIM-ს? შეზღუდავს თუ არა ოპერატორის კონტრაქტი ფასის შეცვლას?
7. ✅ **Referral** (გადაწყდა 2026-10-04): counter-offer P0-ია (მოთამაშეს პასუხისთვის 30 წმ აქვს, ცალკე ვადად), ნაწილობრივი დადასტურება მოთამაშის თანხმობის გარეშე არ გამოიყენება. Timeout: live 30 წმ, prematch 180 წმ. მარკეტის დახურვისას ბილეთი ავტომატურად უქმდება. მოთამაშე შემოწმებაზე მყოფ ფსონს ვერ გააუქმებს.
8. **კომერციული მოდელი** (GGR %, ფიქსირებული ფასი, ბილეთის ფასი): ის წყვეტს, რა უნდა აჩვენოს platform billing რეპორტმა.
