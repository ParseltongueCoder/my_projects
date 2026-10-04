# Betradar UOF ინტეგრაცია — სამაგისტრო გეგმა (Master Plan)

> სტატუსი: დაგეგმვის ეტაპი · თარიღი: 2026-10-02
> მიდგომა: **„simulator-first, contract-second"** — ჯერ ვაშენებთ UOF-თავსებად სიმულატორს და მთელ პაიპლაინს უფასოდ, შემდეგ მუშა დემოთი მივდივართ Sportradar-თან.

## დოკუმენტები

| # | დოკუმენტი | რას მოიცავს |
|---|---|---|
| 01 | [Roadmap, წვდომა, ბიზნესი](docs/01-roadmap-and-access.md) | ფაზები და DoD, გუნდი, Sportradar-თან მიდგომა + 15+ კითხვა, უფასო რესურსები, ალტერნატიული პროვაიდერები, ლიცენზირება, გრანტები, რისკები, ბიუჯეტი |
| 02 | [UOF დატა მოდელი და მარკეტის სქემა](docs/02-uof-data-model.md) | შეტყობინებების ტიპები, XML ნიმუშები, URN-ები, market descriptions/specifiers/variants, **canonical ERD + PostgreSQL DDL**, state machine-ები, UOF → canonical mapping |
| 03 | [Feed Simulator](docs/03-feed-simulator.md) | არქიტექტურა (L1/L2/L3), რეპოს სტრუქტურა, YAML სცენარის DSL, Python კოდის ჩონჩხები, recovery, chaos კატალოგი, docker-compose, Java SDK compatibility checklist, მაილსტოუნები |
| 04 | [პლატფორმის არქიტექტურა](docs/04-platform-architecture.md) | სერვისები, UOF adapter-ის შიდა მოწყობა, admin, მონიტორინგი (მეტრიკები/alert-ები), stack, ინფრა და ხარჯები, უსაფრთხოება, Phase 2+, monorepo |

| — | [UofSim — სიმულატორის კოდი](uof-simulator/README.md) | .NET სიმულატორი: mock Betradar API (descriptions + Sports API), RabbitMQ feed, YAML სცენარები, replay/recorder, recovery, chaos, SDK smoke ტესტი |
| — | [Platform — UOF adapter](platform/README.md) | .NET adapter (ოფიციალური SDK) → canonical მოდელი PostgreSQL-ში |
| ADR | [ADR-001: .NET stack](docs/adr/ADR-001-dotnet-stack.md) | Java → .NET გადაწყვეტილება |
| ADR | [ADR-002: Angular + Material](docs/adr/ADR-002-admin-frontend.md) | ადმინების frontend; რატომ არა PrimeNG |
| — | [Feed Ops ადმინი](admin/admin-web/README.md) | Angular აპლიკაცია + `Admin.Api` |
| 05–11 | [**ოპერატორის Back Office — გეგმა**](docs/09-backoffice-plan.md) | ბაზრის კვლევა (05), კატალოგი/odds/კონფიგურაცია (06), ბილეთები/ლიმიტები/cash-out/მომხმარებლები/PAM (07), არქიტექტურა/ADM/რეპორტები/promo (08), შეჯამება და თანმიმდევრობა (09), ბილეთების მონიტორინგი და referral (10), Player API და white-label iFrame (11) |
| — | [**ტესტირების გზამკვლევი**](TESTING.md) | ფაზა 1-ის ლოკალური გაშვება და სატესტო სცენარები |

## ძირითადი გადაწყვეტილებები (შეჯერებული 4 დოკუმენტს შორის)

| საკითხი | გადაწყვეტილება |
|---|---|
| Production ენა | **.NET 10 + ASP.NET Core + ოფიციალური Sportradar .NET SDK** (`Sportradar.OddsFeed.SDKCore`, მოდიფიკაციის გარეშე) — [ADR-001](docs/adr/ADR-001-dotnet-stack.md) |
| სიმულატორი | .NET 10 — [`uof-simulator/`](uof-simulator/README.md) (S1 მზადაა) |
| Feed transport (სიმულატორი) | RabbitMQ, exchange `unifiedfeed` (topic), vhost `/unifiedfeed/{bookmaker_id}`, TLS 5671 |
| შიდა event bus | NATS JetStream (`UOF_RAW` → normalizer); Kafka მხოლოდ Phase 2-ის კრიტერიუმებით |
| DB / cache | PostgreSQL 18 / Valkey 8 (სიმულატორის შიდა state-ისთვის Redis-თავსებადი ნებისმიერი) |
| Admin | Angular 22 + Angular Material, Keycloak (OIDC), Admin.Api (.NET) + SSE |
| Monitoring | OpenTelemetry, Prometheus, Loki, Tempo, Grafana, Alertmanager → Telegram |
| Infra | docker-compose → Hetzner Cloud (CX/CAX) → Phase 2-ში k3s |
| B2B პროდუქტი | ოპერატორი ყიდულობს **სრულ sportsbook ძრავას** (odds/markets, ბეტების მიღება, settlement, რისკი); ოპერატორის **PAM** (მოთამაშე, wallet, KYC) უერთდება API-ით |
| ბიზნეს-მოდელი სტარტზე | ოპერატორს აქვს **საკუთარი** Sportradar კონტრაქტი, ჩვენ — ტექნოლოგიური მიმწოდებელი (რედისტრიბუციის რისკის თავიდან ასაცილებლად) |

## ლიცენზიის წესი (სავალდებულო ყველასთვის)

Sportradar-ის SDK რეპოები (`UnifiedOddsSdkJava`, `UnifiedOddsSdkNetCore`) **არ არის open source** — მოქმედებს Sportradar SDK License Agreement (მოდიფიკაცია/რედისტრიბუცია/derivative works აკრძალულია).
- SDK-ს ვიყენებთ როგორც დამოკიდებულებას (Maven), მის კოდს არ ვცვლით.
- XSD-ებს და SDK-ის ნიმუშ-XML-ებს **ჩვენს რეპოში არ ვინახავთ** — XSD იტვირთება ლოკალური `UOF_XSD_DIR`-დან.
- ყველა fixture/ნიმუში — ჩვენი გენერირებული, სინთეტიკური ID-ებით.

## 14-კვირიანი გეგმა (დაწყება 2026-10-05)

| კვირა | ფოკუსი | შედეგი |
|---|---|---|
| 0–2 | ფაზა 0: სწავლა | UOF ლექსიკონი, ADR-001..003, canonical DDL-ის დამტკიცება, სიმულატორის სცენარების სია |
| 2–3 | სიმულატორი S1 ✅ | docker-compose, routing keys, alive, whoami/producers/descriptions mock, recovery, L1 replay, chaos, **.NET SDK smoke ტესტი** |
| 3–4 | სიმულატორი S2 + adapter ✅ | Sports API mock, YAML სცენარები, recorder; .NET adapter SDK-ით → PostgreSQL (docs/02-ის DDL + V002) |
| 4–5 | S3 + canonical store | producer/recovery სიმულაცია, SDK smoke test CI-ში; normalizer → Postgres/Valkey |
| **5** | **Sportradar outreach** | პირველი მიმართვა, discovery call |
| 5–7 | S3 ✅ + monitoring ✅ + admin | recovery snapshot, producer down → market-ების შეჩერება/აღდგენა, Prometheus + Grafana „UOF Feed Health" + alert-ები ✅; Feed Ops admin MVP ✅ |
| 7–8 | S5 + დემო | chaos/load ტესტები, E2E დემო-ვიდეო, ერთგვერდიანი ტექ. აღწერა |
| 8–14 | ფაზა 2: Integration env | კონფიგით გადართვა, divergence report, Replay რეგრესიული სუიტი, hardening, integration review |
| 14+ | ფაზა 3 | პირველი ოპერატორის პილოტი, მეორე პროვაიდერი (LSports — აქვს trial/sandbox), სტრიმები, ვიჯეტები |

## ღია საკითხები (გადასაწყვეტი / გადასამოწმებელი)

1. **იურისტი:** სჭირდება თუ არა B2B მიმწოდებელს ქართული ნებართვა; რომელი საერთაშორისო ლიცენზია (MGA / Curaçao) და როდის.
2. **Sportradar:** trial/integration token-ის ხანგრძლივობა (ერთ წყაროში ~2 კვირა), რედისტრიბუციის უფლებები, Replay წვდომა, XSD-ების გამოყენების პირობები, ფასები.
3. **ტექნიკური:** Live Odds recovery window (10სთ vs 72სთ), recovery rate limit-ები, `-2` (handed over) სტატუსის დამუშავება, player outcome ID-ების ფორმატი, ქართული ენის მხარდაჭერა descriptions-ში.
4. **GITA:** ვრცელდება თუ არა გრანტი iGaming-თან დაკავშირებულ B2B პროდუქტზე.

ყველა `⚠ გადასამოწმებელი` პუნქტი დოკუმენტებშია მონიშნული; docs.sportradar.com კვლევისას მიუწვდომელი იყო, ამიტომ ფაქტები გადამოწმებულია SDK-ის კოდით და საძიებო ამონარიდებით.
