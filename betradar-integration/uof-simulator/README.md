# UofSim — Betradar UOF-თავსებადი სიმულატორი (.NET)

სიმულატორი, რომელიც ასახავს Betradar Unified Odds Feed-ის ორივე არხს:
- **AMQP** — RabbitMQ, TLS 5671, exchange `unifiedfeed` (topic), vhost `/unifiedfeed/{bookmaker_id}`, 8-სეგმენტიანი routing key-ები;
- **REST** — იგივე path-ები, რასაც ოფიციალური SDK იძახებს (`/v1/users/whoami.xml`, `/v1/descriptions/...`, recovery endpoint-ები).

მიზანი: ჩვენი adapter (ოფიციალური **Sportradar .NET SDK**-ით) ვაწყობთ და ვტესტავთ უფასოდ, კონტრაქტამდე. რეალურ Integration გარემოზე გადასვლა = მხოლოდ კონფიგის ცვლილება (host, token, password).

სტატუსი: **S1 + S2** (docs/03-feed-simulator.md §10) — alive, recovery, startup REST, chaos (producer down), L1 replay, **Sports API mock** (SDK ხსნის გუნდების/მარკეტების სახელებს), **YAML სცენარები** Poisson odds-ით, **recorder**.

## სწრაფი სტარტი

```bash
cd betradar-integration/uof-simulator
./tools/gen-tls.sh                 # ლოკალური CA + RabbitMQ სერტიფიკატი (config/tls, git-ignored)
docker compose up -d --build       # rabbitmq + uofsim
curl -H "x-access-token: sim-token-0000000000" localhost:8080/v1/users/whoami.xml

# დემო მატჩის გაშვება (10x სიჩქარით)
curl -X POST localhost:8080/sim/replay -H "Content-Type: application/json" \
     -d '{"file":"recordings/demo_match.jsonl","speed":10}'
```

RabbitMQ Management UI: http://localhost:15672 (`admin` / `admin`).

ლოკალურად Docker-ის გარეშე (RabbitMQ მაინც საჭიროა): `dotnet run --project src/UofSim.Host` → http://localhost:8080.

## SDK smoke ტესტი — ოფიციალური SDK ჩვენს სიმულატორზე

```bash
dotnet run --project tests/UofSim.SdkSmoke            # startup + producers UP + replay → odds_change/bet_stop/bet_settlement
dotnet run --project tests/UofSim.SdkSmoke -- --chaos # + producer 1 "ჩუმდება" → SDK: ProducerDown → recovery → ProducerUp
```

SDK-ის კონფიგურაცია სიმულატორზე (იგივე, რაც ჩვენს adapter-ში იქნება):

```csharp
var config = UofSdk.GetConfigurationBuilder()
    .SetAccessToken(token)                 // sim-token-0000000000
    .SelectCustom()
    .SetApiHost("localhost:8080").UseApiSsl(false)
    .SetMessagingHost("localhost").SetMessagingPort(5671).UseMessagingSsl(true)
    .SetMessagingUsername(token)
    .SetMessagingPassword("sim")           // Betradar-ზე ცარიელი; ლოკალურ RabbitMQ-ს ცარიელი პაროლი არ მიაქვს
    .SetNodeId(1)
    .SetDefaultLanguage(CultureInfo.GetCultureInfo("en"))
    .Build();
```

რეალურ გარემოზე გადასვლისას `SelectCustom()` იცვლება `SelectEnvironment(SdkEnvironment.Integration)`-ით (ან `SelectReplay()`-ით), token — Betradar-ის მიერ გაცემულით.

## Control API (`/sim/*`, token-ის გარეშე)

| Method | Path | რას აკეთებს |
|---|---|---|
| GET | `/sim/status` | producer-ების რეჟიმი + replay-ის მდგომარეობა |
| POST | `/sim/producers/{id}/down?mode=silent\|unsubscribed&seconds=N` | `silent` — alive აღარ იგზავნება (SDK → ProducerDown); `unsubscribed` — alive `subscribed=0` (SDK → recovery). `seconds`-ის შემდეგ producer ბრუნდება `unsubscribed`-ში და recovery-ს ელოდება |
| POST | `/sim/producers/{id}/up` | producer-ის აღდგენა |
| POST | `/sim/replay` `{"file","speed","loop"}` | JSONL ჩანაწერის გაშვება (`data/` დირექტორიის შიგნით) |
| POST | `/sim/replay/stop` | replay-ის შეჩერება |
| GET | `/sim/scenarios` | `data/scenarios/*.yaml`-ის სია |
| POST | `/sim/scenarios/{name}` `{"speed","loop"}` | სცენარის კომპილაცია და გაშვება |
| POST | `/sim/recorder/start` `{"file"}` · `/sim/recorder/stop` | ყველა გაგზავნილი event-შეტყობინების ჩაწერა JSONL-ში (შემდეგ `/sim/replay`-ით გაიშვება) |

## Mock REST (`/v1/*`, საჭიროა `x-access-token`)

| Endpoint | სტატუსი |
|---|---|
| `GET /v1/users/whoami.xml` | ✅ |
| `GET /v1/descriptions/producers.xml` (`api_url` = მოთხოვნის host + `/v1/{liveodds\|pre}/`) | ✅ |
| `GET /v1/descriptions/{lang}/markets.xml`, `variants.xml`, `match_status.xml` | ✅ (ჩვენი subset; უცნობი ენა → `en`) |
| `GET /v1/descriptions/betstop_reasons.xml`, `betting_status.xml`, `void_reasons.xml` | ✅ (placeholder მნიშვნელობები) |
| `POST /v1/{product}/recovery/initiate_request` | ✅ 202 → `snapshot_complete` იგივე `request_id`-ით, routing key-ის node სეგმენტით |
| `POST /v1/{product}/odds/events/{urn}/initiate_request`, `stateful_messages/...` | ✅ 202 (state-ის ხელახლა გაგზავნა — S2/S3) |
| `GET /v1/sports/{lang}/sports.xml`, `tournaments.xml`, `sports/{sport}/categories.xml`, `sports/{sport}/tournaments.xml` | ✅ `data/catalog.yaml`-დან |
| `GET /v1/sports/{lang}/schedules/{date}/schedule.xml`, `schedules/live/…`, `schedules/pre/…` | ✅ (event-ების დრო = სიმულატორის გაშვების დრო + offset) |
| `GET /v1/sports/{lang}/sport_events/{urn}/summary.xml`, `fixture.xml`, `fixture_change_fixture.xml` | ✅ summary-ის status/score მიჰყვება გაგზავნილ `odds_change`-ებს |
| `GET /v1/sports/{lang}/competitors/{urn}/profile.xml` | ✅ |
| `GET /v1/wns/{lang}/lotteries.xml`, timeline, player profile, `fixtures/changes.xml` | ❌ (SDK ამაზე მხოლოდ warning-ს წერს) |

სიმულატორი ყოველ დაუფარავ `/v1` მოთხოვნას ლოგავს: `Unsimulated endpoint: ...` — ეს არის S2-ის backlog.

## სცენარები (YAML)

```yaml
name: Derby with settlement rollback
event: sr:match:900000002          # უნდა არსებობდეს data/catalog.yaml-ში
strength: { home: 1.55, away: 1.10 } # expected goals → Poisson odds (1x2, Total 2.5, BTTS) მარჟით
steps:
  - { at: 0s,  action: prematch_odds }            # Ctrl (3), pre routing
  - { at: 4s,  action: kickoff }
  - { at: 11s, action: bet_stop }
  - { at: 12s, action: goal, team: home, minute: 23 }
  - { at: 20s, action: halftime }                 # markets suspended
  - { at: 38s, action: full_time }                # markets deactivated
  - { at: 40s, action: settle, certainty: 1 }     # შედეგი ანგარიშიდან ავტომატურად
  - { at: 44s, action: rollback_settlement }
  - { at: 47s, action: settle, certainty: 2 }
```

Actions: `prematch_odds`, `kickoff`, `odds_update`, `bet_stop`, `goal`, `halftime`, `second_half`, `full_time`, `settle`, `rollback_settlement`, `cancel`. CLI: `dotnet run --project src/UofSim.Host -- compile-scenario <in.yaml> <out.jsonl>`.

## XSD ვალიდაცია (ლიცენზიის წესი)

Sportradar-ის XSD-ები და SDK-ის ნიმუშ-ფაილები **ამ რეპოში არ ინახება** (SDK License Agreement). ვალიდაციისთვის მიუთითეთ ლოკალური დირექტორია, სადაც დევს `UnifiedFeed.xsd`, `UnifiedFeedDescriptions.xsd`, `UnifiedFeedResponse.xsd`:

```bash
UOF_XSD_DIR=/path/to/xsd dotnet test tests/UofSim.Tests      # XSD ტესტები ჩაირთვება (სხვა შემთხვევაში skip)
UOF_SPORTSAPI_XSD_DIR=/path/to/bsa/v1/endpoints/unified dotnet test tests/UofSim.Tests   # Sports API პასუხები
UOF_XSD_HOST_DIR=/path/to/xsd docker compose up -d           # ყოველი გაგზავნილი შეტყობინება მოწმდება
```

ყველა fixture (დემო მატჩი, descriptions) ჩვენი გენერირებულია, გამოგონილი ID-ებით.

## სტრუქტურა

```
uof-simulator/
├── src/UofSim.Core/            # routing keys, XML builders (XSD-ის თანმიმდევრობით), producers, recordings, demo match
├── src/UofSim.Host/            # ASP.NET Core: mock REST, control API, AMQP publisher, alive/recovery/replay services
│   └── data/                   # static descriptions + recordings/demo_match.jsonl
├── tests/UofSim.Tests/         # xUnit: unit + REST + XSD compliance
├── tests/UofSim.SdkSmoke/      # ოფიციალური Sportradar.OddsFeed.SDKCore სიმულატორის წინააღმდეგ
├── config/rabbitmq/            # rabbitmq.conf, definitions.json (vhost, users, exchange)
├── tools/gen-tls.sh            # dev CA + server cert
├── docker-compose.yml, Dockerfile
```

დემო ჩანაწერის ხელახლა გენერირება: `dotnet run --project src/UofSim.Host -- generate-demo $PWD/src/UofSim.Host/data/recordings/demo_match.jsonl`.

## შეზღუდვები და შემდეგი ნაბიჯები

- Recovery ჯერ snapshot-ს არ აგზავნის — მხოლოდ `snapshot_complete`-ს (S3: მიმდინარე odds-ის ხელახლა გაგზავნა).
- ერთდროულად ერთი replay/სცენარი მუშაობს (ახალი წინას აჩერებს) — პარალელური მატჩები S4-ში.
- SDK ცარიელ `schedule.xml`-ზე (დღე, როცა catalog-ში მატჩი არ არის) და `lotteries.xml`-ზე error-ს ლოგავს — მუშაობას არ აჩერებს.
- `after` timestamp-ის recovery window-ზე შემოწმება არ ხდება (რეალური API-ს ქცევა ⚠ გადასამოწმებელია).
- Market/outcome ID-ები და reason-ების სიები placeholder-ია — Integration წვდომისთანავე შევადაროთ რეალურ `markets.xml`-ს.
- S3: სრული recovery (snapshot); S4: L3 მატჩის ძრავა, მეტი მარკეტი, პარალელური მატჩები; S5: chaos/load.
