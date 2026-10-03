# Platform — UOF adapter + canonical model (.NET)

პირველი production-ის მსგავსი სერვისი: **Betradar UOF → ჩვენი canonical მოდელი (PostgreSQL)**.

```
Sportradar .NET SDK ──(raw XML of every event message)──► UofFeedParser ──► CanonicalStore ──► PostgreSQL (schema sb)
   │  session, alive, recovery                                (pure, no SDK)     (one tx per message,
   │  Sports API calls → EventResolver (teams, tournament)                        raw archive first)
```

- **SDK** მართავს AMQP სესიას, alive-ს და recovery-ს (არაფერს ვწერთ თავიდან).
- **Normalisation raw XML-იდან** (`IEventMessage.RawMessage`) — SDK-ის ობიექტურ მოდელზე არ ვართ დამოკიდებული, ამიტომ მეორე პროვაიდერის დამატება იგივე store-ს გამოიყენებს.
- **Raw-first:** ყოველი შეტყობინება ჯერ `feed_message_log`-ში იწერება (sha256-ით dedup 1 სთ-იან ფანჯარაში), შემდეგ ცალკე ტრანზაქციით გამოიყენება; ცუდი შეტყობინება `failed` სტატუსით ჩანს და feed-ს არ აჩერებს.
- სქემა = docs/02-ის DDL (`db/migrations/V001`), + `V002` — ის, რაც პირველმა რეალურმა გაშვებამ მოითხოვა (იხ. ქვემოთ).

## რას წერს adapter

| შეტყობინება | ცხრილები |
|---|---|
| პირველად ნანახი event | `sport`, `category`, `tournament`, `competitor`, `event`, `event_competitor`, `provider_mapping` (SDK → mock/real Sports API) |
| SDK startup | `market_description`, `market_specifier_def`, `market_description_outcome` (`markets.xml`-დან) |
| `odds_change` | `event` (status/score/clock), `market` (upsert natural key-ით: event + market + **დალაგებული** specifiers), `outcome` (odds, probability). ძველი (stale) შეტყობინება ახალს ვერ გადაწერს |
| `bet_stop` | `market.status` → suspended (groups-ის მიხედვით), `bet_stop_log` |
| `bet_settlement` | `settlement` (append-only; certainty 1→2 = supersede), `outcome.result`, `market.status = settled` |
| `rollback_bet_settlement` | `rollback`, settlement-ები `rolled_back_at`, market ბრუნდება წინა სტატუსზე |
| `bet_cancel` | `market_cancellation`, `market.status = cancelled` |
| producer down | `producer_status`, producer-ის ყველა active market → **suspended**, `bet_stop_log` (source `producer_down`) |
| producer up (recovery-ს შემდეგ) | market-ები ბრუნდება იმ სტატუსზე, რასაც feed ამბობს (`feed_status`) |
| ყველაფერი | `feed_message_log` (raw XML, sha256, status) |

## Producer down — უსაფრთხოება

`market.status` (რასაც ვთავაზობთ) და `market.feed_status` (რაც feed-მა ბოლოს თქვა) ცალ-ცალკეა (`V003`):

1. SDK აცხადებს ProducerDown-ს → producer-ის ყველა active market → `suspended` (odds-ზე ფსონი აღარ მიიღება).
2. სანამ producer down-ია, შემოსული odds_change (მათ შორის recovery snapshot) odds-ს და `feed_status`-ს აახლებს, მაგრამ market-ს **არ ხსნის**.
3. ProducerUp (snapshot_complete-ის შემდეგ) → `status = feed_status`: იხსნება მხოლოდ ის, რაც feed-ის მიხედვით active-ია; რაც feed-მა თავად შეაჩერა, შეჩერებული რჩება.

Feed-ის შეტყობინებები და producer-ის ცვლილებები ერთ რიგშია — ამიტომ down-მდე მიღებული შეტყობინებები suspension-მდე ინახება.

## მონიტორინგი

`docker compose up` ასევე უშვებს Prometheus-ს (http://localhost:9090) და Grafana-ს (http://localhost:3000, dashboard **„UOF Feed Health“**).

| მეტრიკა (`/metrics`, პორტი 8081) | რას ზომავს |
|---|---|
| `uof_messages_total{type,outcome}` | შეტყობინებები: processed / duplicate / failed / error |
| `uof_message_lag_seconds` | feed timestamp → ბაზაში ჩაწერა (histogram) |
| `uof_store_duration_seconds` | ერთი შეტყობინების archive + apply |
| `uof_queue_depth` | რიგში მომლოდინე შეტყობინებები |
| `uof_producer_up{producer}` | 1 / 0 |
| `uof_producer_down_suspended_markets_total` | producer down-ის გამო შეჩერებული market-ები |

Alert-ები (`observability/prometheus/rules/uof-feed.yml`): `UofAdapterDown`, `UofProducerDown` (30 წმ), `UofFeedLagHigh` (p99 > 500 ms, 5 წთ), `UofMessagesFailing`, `UofQueueBacklog`. Alertmanager → Telegram ჯერ არ არის მიერთებული (bot token სჭირდება); alert-ები ჩანს http://localhost:9090/alerts-ზე.

## გაშვება (სიმულატორთან ერთად)

```bash
cd betradar-integration/platform
../uof-simulator/tools/gen-tls.sh
docker compose up -d --build          # rabbitmq + uofsim + postgres + uof-adapter

curl -X POST localhost:8080/sim/scenarios/derby_settlement_rollback -H 'Content-Type: application/json' -d '{"speed":4}'
docker compose exec postgres psql -U platform -d platform \
  -c "select status, home_score, away_score from sb.event" \
  -c "select message_type, status, count(*) from sb.feed_message_log group by 1,2"
```

რეალურ Betradar-ზე გადასვლა — მხოლოდ კონფიგი: `Uof__Environment=Integration` (ან `Replay` / `Production`), `Uof__AccessToken=<token>`; `ApiHost`/`MessagingHost`/`MessagingPassword` მხოლოდ `Custom` (სიმულატორი) რეჟიმში გამოიყენება.

## ტესტები

```bash
dotnet test tests/Platform.Tests                       # parser tests (DB tests skipped)
docker run -d --name platform-pg -p 55432:5432 -e POSTGRES_PASSWORD=platform postgres:18
PLATFORM_TEST_PG="Host=localhost;Port=55432;Username=postgres;Password=platform" dotnet test tests/Platform.Tests
```

Store-ის ტესტები სიმულატორის `ScenarioCompiler`-ით აგენერირებენ სრულ მატჩს (გოლები, settlement, certainty upgrade, rollback, cancel) და ამოწმებენ შედეგს ნამდვილ PostgreSQL 18-ში (თითო ტესტ-კლასს — ცალკე, დროებითი ბაზა). CI: `.github/workflows/platform.yml`.

## რა აღმოვაჩინეთ (docs/02 / docs/04-ის განახლება)

- **docs/02-ის DDL პირველად გაეშვა PostgreSQL 18-ზე — უცვლელად აიწყო (22 ცხრილი).** `V002`-ში დაემატა:
  - `feed_message_log`-ის **DEFAULT partition** (pg_partman-მდე inserts სხვაგვარად ვერ იმუშავებს);
  - dedup unique index-ის მოხსნა (`received_at`-ის გამო არაფერს აკავებდა — docs-იც ამას აღნიშნავდა) → dedup app-ში;
  - `settlement.superseded_by_id` FK → **DEFERRABLE**: partial unique index-ი („ერთი effective settlement outcome-ზე“) certainty upgrade-ისას სხვაგვარად ჩიხს ქმნის.
- **.NET SDK raw XML-ს თითოეულ event შეტყობინებაზე იძლევა** (`IEventMessage.RawMessage`) — docs/04-ის „raw-first“ დიზაინი მეორე AMQP consumer-ის გარეშე მუშაობს.
- SDK-ის `GetMarketDescriptionsAsync()` `Open()`-ის შემდეგ პირველ წამებში ცარიელს აბრუნებს → adapter ცდის რამდენჯერმე.

## შემდეგი ნაბიჯები

- NATS JetStream (`UOF_RAW`) store-სა და SDK-ს შორის — docs/04-ის მიხედვით (ახლა in-process queue-ა, ერთი consumer).
- `fixture_change` / `rollback_bet_cancel`-ის გამოყენება (ახლა მხოლოდ არქივდება).
- Valkey hot cache + distribution API.
