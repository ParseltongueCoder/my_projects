# 03 — UOF Feed Simulator (Betradar Unified Odds Feed-თან თავსებადი სიმულატორი)

> **სტატუსი:** Draft v0.1 · **ფაზა:** 1 · **მფლობელი:** Feed/Integration გუნდი
> **დაკავშირებული დოკუმენტები:** `01-…` (ბიზნეს-მიზნები), `02-…` (consumer-ის არქიტექტურა) — ⚠ გადასამოწმებელი (ფაილების სახელები ჯერ არ არის დაფიქსირებული)
>
> ნიშანი **⚠ გადასამოწმებელი** ნიშნავს, რომ ფაქტი აღებულია საჯარო წყაროებიდან / SDK-ის კოდიდან / მეხსიერებიდან და Betradar-ის ოფიციალურ დოკუმენტაციასთან (docs.sportradar.com) ან რეალურ Integration გარემოსთან უნდა შემოწმდეს, სანამ მასზე ბიზნეს-ლოგიკას დავაშენებთ.

---

## 0. მოკლედ (TL;DR)

- ვაშენებთ **საკუთარ UOF-თავსებად სიმულატორს**, რომელიც ასახავს ორივე არხს: **AMQP feed-ს** (RabbitMQ, topic exchange `unifiedfeed`, Betradar-ის routing key-ებით) და **REST API-ს** (FastAPI, იგივე path-ებით და XML სტრუქტურით).
- ჩვენი consumer სერვისი იწერება ისე, რომ **სიმულატორიდან რეალურ Betradar Integration გარემოზე გადასვლა მხოლოდ კონფიგურაციის ცვლილებაა** (host, port, token, bookmaker_id/vhost, TLS CA).
- სიმულატორს აქვს 3 სიზუსტის დონე: **L1** (სტატიკური XML replay), **L2** (YAML სცენარის ძრავა), **L3** (სტოქასტიკური მატჩის ძრავა + Poisson-ზე დაფუძნებული odds მოდელი).
- ყველა გამავალი შეტყობინება **XSD-ით მოწმდება** (ოფიციალური `UnifiedFeed.xsd` Sportradar-ის SDK რეპოდან) და **იწერება Recorder-ში** დეტერმინისტული replay-სთვის.
- Chaos რეჟიმი: duplicate, out-of-order, delay, missing alive, producer down, `subscribed=0`, rollback, void/dead-heat, load test.

### ვერიფიცირებული საბაზისო ფაქტები (წყარო: Sportradar SDK-ების კოდი და საჯარო დოკ.)

| ფაქტი | მნიშვნელობა | წყარო / სტატუსი |
|---|---|---|
| AMQP exchange | `unifiedfeed`, type `topic` | Developer Integration PDF, SDK — ✅ |
| AMQP port | `5671` (TLS) | ✅ |
| Virtual host | `/unifiedfeed/{bookmaker_id}` (მოდის `whoami.xml`-დან) | ✅ |
| Username / Password | username = access token, password = ცარიელი | ✅ |
| AMQP hosts | prod `mq.betradar.com`, integration `stgmq.betradar.com`, replay `replaymq.betradar.com`, global: `global.mq…`, `global.stgmq…`, `global.replaymq…` | Java SDK `EnvironmentManager` — ✅ |
| REST hosts | prod `api.betradar.com` (global: `global.api.betradar.com`), integration/replay `stgapi.betradar.com` | Java SDK — ✅ |
| Auth header (REST) | `x-access-token: <token>` | go-uof-sdk — ✅ |
| Routing key | 8 სეგმენტი: `priority.pre.live.message_type.sport_id.urn_type.event_id.node_id` | SDK-ების პარსერები/ტესტები — ✅ |
| Alive | ყოველ ~10 წამში თითო producer-ზე, `<alive product timestamp subscribed/>` | Replay Server დოკ. (10s) — ✅; live feed-ისთვის ⚠ გადასამოწმებელი |
| Producer inactivity threshold (SDK default) | 20 წმ (min 10, max 180) | Java SDK `ConfigLimit` — ✅ |
| Max recovery time (SDK default) | 1200 წმ (snapshot_complete-ის ლოდინი) | Java SDK `ConfigLimit` — ✅ |
| Min interval between recovery requests (SDK default) | 30 წმ | Java SDK `ConfigLimit` — ✅ |
| Recovery window | LO (Live Odds) — 10 სთ (ახალი დოკ.), სხვები — 72 სთ, virtuals/gaming — 3 სთ | ძველი go-sdk-ში LO=72სთ → ⚠ გადასამოწმებელი |

---

## 1. მიზნები, არა-მიზნები და სიზუსტის დონეები

### 1.1 მიზნები (Goals)

1. **უფასო, ლოკალური, განმეორებადი ტესტირება** Betradar-თან კომერციულ კონტრაქტამდე.
2. **Wire-level თავსებადობა**: იგივე exchange, routing key-ები, XML ელემენტები/ატრიბუტები, REST path-ები, HTTP სტატუსები, header-ები. Consumer-ს არ უნდა ჰქონდეს `if simulator:` ტიპის განშტოება.
3. **Producer lifecycle-ის სრული სიმულაცია**: alive, `subscribed=0`, producer down/up, recovery `request_id`-ით, `snapshot_complete`, event odds recovery, stateful messages recovery.
4. **რეალისტური odds**: L3-ზე ფეხბურთისთვის Poisson მოდელი — 1x2, double chance, draw no bet, totals, handicap, BTTS, correct score, მარჟით.
5. **Chaos/edge case-ები** სცენარში დეკლარაციულად და Control API/CLI-ით runtime-ში.
6. **დეტერმინიზმი**: `seed` + ვირტუალური საათი + Recorder ⇒ ერთი და იგივე სცენარი ყოველთვის ბაიტ-ბაიტ იგივე შეტყობინებებს იძლევა (timestamp-ების ნორმალიზაციის შემდეგ) → golden-file ტესტები CI-ში.
7. **Load testing**: კონფიგურირებადი msg/s, შეტყობინების ზომის განაწილება, burst-ები.

### 1.2 არა-მიზნები (Non-goals)

- არ ვცდილობთ Betradar-ის **რეალური მონაცემების** გამეორებას (რეალური მატჩები/გუნდები/odds). სიმულატორი მხოლოდ სინთეზურ მონაცემებს აწარმოებს.
- არ ვაკეთებთ **ყველა სპორტის / ყველა მარკეტის** მხარდაჭერას (Phase 1: ფეხბურთი; შემდეგ კალათბურთი/ჩოგბურთი).
- არ ვაკეთებთ MTS-ს (ticket/bet acceptance), Live Data (LD)/Live Channel-ს, სტრიმინგს, Custom Bet API-ს, Cashout probabilities feed-ს (შეიძლება მოგვიანებით).
- არ ვქმნით „production-quality" trading მოდელს — odds მხოლოდ დამაჯერებელი/შინაგანად თანმიმდევრული უნდა იყოს.
- სიმულატორი **არ არის** Betradar-ის სახელით გამოსაქვეყნებელი სერვისი: შიდა გამოყენება, dev/test.
- Betradar-ის ოფიციალურ `markets.xml` კონტენტს არ ვაკოპირებთ ლიცენზიის გარეშე — ვქმნით საკუთარ მცირე subset-ს იგივე **სტრუქტურით** (XSD), ID-ები ვერიფიკაციამდე ⚠.

### 1.3 სიზუსტის დონეები (Fidelity levels)

| დონე | რას აკეთებს | შემავალი | გამოყენება | სირთულე |
|---|---|---|---|---|
| **L1 — Static XML replay** | წინასწარ მომზადებულ/ჩაწერილ XML ფაილებს აქვეყნებს სწორი routing key-ით და დროის ინტერვალებით | `data/recordings/*.jsonl` ან `*.xml` დირექტორია | parser-ის unit/contract ტესტები, smoke | დაბალი |
| **L2 — Scenario engine** | YAML DSL-ის timeline → მოვლენები (goal, card, bet_stop, settle, rollback); odds — სცენარში ხელით ან მარტივი ფორმულით | `scenarios/**/*.yaml` | ბიზნეს-ფლოუს ტესტები, QA, დემო | საშუალო |
| **L3 — Stochastic match engine + odds model** | Poisson პროცესით აგენერირებს მატჩის მოვლენებს, odds-ს ითვლის მიმდინარე მდგომარეობიდან, მარჟით; მრავალი მატჩი პარალელურად | `seed`, xG, ლიგის პროფილი | load test, soak test, trading/risk ლოგიკის ტესტი | მაღალი |

L2 და L3 კომბინირებადია: სცენარი შეიძლება იყოს „L3 მატჩი, მაგრამ 63'-ზე იძულებითი გოლი და 70'-ზე producer down".

---

## 2. არქიტექტურა

### 2.1 კომპონენტების დიაგრამა

```mermaid
flowchart LR
    subgraph CTRL["Control plane"]
        CLI["CLI (typer)\nuofsim ..."]
        CAPI["Control API (FastAPI :8090)\nstart/goal/red_card/bet_stop/suspend\nproducer down/delay/dup/reorder/rollback"]
    end

    subgraph SIM["uofsim engine (Python 3.12, asyncio)"]
        SCN["Scenario Engine\nYAML DSL → timeline"]
        CLK["SimClock\nreal / accelerated / stepped"]
        ME["Match Engine\nstate machine + Poisson events"]
        OE["Odds Engine\nPoisson score matrix → 1x2/DC/DNB/\nTotals/Handicap/BTTS/CS + margin"]
        MB["Message Builder\nUOF XML (lxml) + XSD validate"]
        RK["Routing Key Builder"]
        CH["Chaos Middleware\ndup / reorder / delay / drop"]
        PUB["AMQP Publisher\n(aio-pika, confirms)"]
        PM["Producer Manager\nalive loop, up/down, subscribed"]
        REC["Recorder\n(Postgres + JSONL)"]
        RCV["Recovery Handler\nsnapshot → snapshot_complete"]
    end

    subgraph REST["mock-rest (FastAPI :8080)"]
        R1["/v1/users/whoami.xml"]
        R2["/v1/descriptions/...\nproducers, markets, variants"]
        R3["/v1/sports/en/...\nfixture, summary, schedules"]
        R4["/v1/{product}/recovery/initiate_request\n/v1/{product}/odds/events/{urn}/initiate_request\n/v1/{product}/stateful_messages/..."]
        R5["/v1/replay/* (Replay-compatible)"]
    end

    MQ[("RabbitMQ\nvhost /unifiedfeed/{bookmaker_id}\nexchange unifiedfeed (topic)")]
    RD[("Redis\nlive state + command stream")]
    PG[("PostgreSQL\nrecordings, fixtures")]
    CONS["Our Consumer service\n(the real product)"]

    CLI --> CAPI --> SCN
    SCN --> ME --> OE --> MB
    CLK -.-> SCN & ME & PM
    MB --> RK --> CH --> PUB --> MQ
    PM --> MB
    RCV --> MB
    PUB --> REC --> PG
    ME -->|state snapshot| RD
    R3 -->|read state| RD
    R4 -->|XADD sim:commands| RD -->|consume| RCV
    R5 -->|XADD sim:commands| RD
    MQ --> CONS
    CONS -->|HTTPS x-access-token| REST
```

### 2.2 მონაცემთა ნაკადი (ერთი odds_change)

1. **Scenario Engine** SimClock-ის მიხედვით აგზავნის `Tick`/`Action` ივენთებს (მაგ. `goal(home, 23:14)`).
2. **Match Engine** ცვლის `MatchState`-ს (score, match_status, clock, cards), წერს Redis-ში (`sim:event:{urn}`), რათა mock-rest-ის `summary.xml` სინქრონში იყოს.
3. **Odds Engine** ითვლის ალბათობებს → odds-ს მარჟით → market status-ებს.
4. **Message Builder** ქმნის `<odds_change>`-ს, ამოწმებს XSD-ით (dev/test-ში 100%, load-ში sampling).
5. **Routing Key Builder** → `hi.-.live.odds_change.1.sr:match.900000001.-`.
6. **Chaos Middleware** (თუ ჩართულია) — duplicate/delay/reorder/drop.
7. **AMQP Publisher** აქვეყნებს `unifiedfeed` exchange-ზე, publisher confirms-ით.
8. **Recorder** ინახავს `(seq, sim_time, wall_time, routing_key, body, chaos_flags)`.

### 2.3 Recovery-ის sequence

```mermaid
sequenceDiagram
    participant C as Consumer
    participant R as mock-rest
    participant Q as Redis (sim:commands)
    participant E as Engine/RecoveryHandler
    participant MQ as RabbitMQ (unifiedfeed)

    MQ-->>C: alive product=1 subscribed=0  (ან alive აღარ მოდის >20s)
    Note over C: producer 1 → DOWN, bet acceptance stop
    C->>R: POST /v1/liveodds/recovery/initiate_request?after=T&request_id=4711&node_id=1
    R->>R: auth, producer lookup, window check, rate limit
    R->>Q: XADD {type: recovery, product: 1, after: T, request_id: 4711, node_id: 1}
    R-->>C: 202 <response response_code="ACCEPTED">
    Q-->>E: command
    loop ყველა აქტიური event (product 1)
        E->>MQ: odds_change request_id=4711 (rk ... .1 node)
    end
    E->>MQ: stateful msgs (bet_settlement/bet_cancel since T) request_id=4711
    E->>MQ: snapshot_complete request_id=4711 (rk -.-.-.snapshot_complete.-.-.-.1)
    E->>E: producer 1 subscribed=1
    MQ-->>C: alive product=1 subscribed=1
    Note over C: producer 1 → UP
```

### 2.4 პროცესები და კავშირი

| პროცესი | Docker service | პასუხისმგებლობა |
|---|---|---|
| `uofsim engine` + Control API | `simulator` | სცენარები, მატჩები, odds, publish, alive, recovery-ის შესრულება |
| `uofsim rest` | `mock-rest` | Betradar REST API-ს იმიტაცია; state-ს კითხულობს Redis/Postgres-დან; recovery ბრძანებებს აგზავნის Redis Stream-ში |
| Consumer | `consumer` | ჩვენი რეალური პროდუქტი (ცალკე რეპო/სერვისი; აქ placeholder) |

engine და mock-rest განცალკევებულია, რადგან რეალურ სამყაროშიც AMQP და REST სხვადასხვა host-ია — ეს ავლენს consumer-ის race condition-ებს (მაგ. odds_change მოვიდა მანამ, სანამ fixture REST-ში ხელმისაწვდომია).

---

## 3. რეპოზიტორიის სტრუქტურა

```text
uof-simulator/
├── pyproject.toml                  # პაკეტი `uofsim`, deps: fastapi, uvicorn, aio-pika, lxml, numpy, pydantic, pydantic-settings, redis, asyncpg, typer, orjson
├── README.md                       # სწრაფი სტარტი: make up, uofsim run ...
├── Makefile                        # up/down/test/lint/xsd/tls/load ბრძანებები
├── Dockerfile                      # ერთი image, ორი entrypoint (engine | rest)
├── docker-compose.yml              # rabbitmq, postgres, redis, simulator, mock-rest, consumer (§8)
├── .env.example                    # BOOKMAKER_ID, SIM_TOKEN, ports, speed, seed
├── config/
│   ├── simulator.yaml              # default კონფიგი: producers, alive interval, recovery windows, rate limits
│   ├── rabbitmq/
│   │   ├── rabbitmq.conf           # TLS listener 5671, plain 5672, definitions load
│   │   ├── enabled_plugins         # rabbitmq_management (+ optional rabbitmq_auth_backend_http)
│   │   └── definitions.json        # vhost /unifiedfeed/99999, users, permissions, exchange unifiedfeed
│   └── tls/                        # tools/gen_tls.sh-ით გენერირებული CA + server cert (git-ignored)
├── schemas/
│   └── xsd/
│       ├── UnifiedFeed.xsd                 # AMQP შეტყობინებები (SDK: sdk-core/src/main/resources/xsd/messages/)
│       ├── UnifiedFeedDescriptions.xsd     # markets/variants/producers descriptions
│       ├── UnifiedFeedResponse.xsd         # REST <response response_code=...>
│       └── sports/*.xsd                    # fixture/summary/schedule (SDK-დან) ⚠ გადასამოწმებელი ზუსტი ფაილები
├── data/
│   ├── static/
│   │   ├── producers.xml           # producers 1 (LO) და 3 (Ctrl) + optional 6 (VF)
│   │   ├── markets.en.xml          # ჩვენი subset: 1,10,11,16,18,29,41 (+ test-only id-ები)
│   │   ├── variants.en.xml         # variant descriptions (correct score, exact goals)
│   │   ├── sports.en.xml           # sr:sport:1 Soccer, 2 Basketball, 5 Tennis
│   │   └── bookmaker.xml           # whoami template
│   ├── teams/georgia_erovnuli.yaml # სინთეზური გუნდები/ტურნირები + სიძლიერის რეიტინგები
│   ├── templates/                  # Jinja2/lxml template-ები REST პასუხებისთვის (fixture, summary, schedule)
│   └── recordings/                 # L1 replay ფაილები (*.jsonl), golden outputs
├── scenarios/
│   ├── football/
│   │   ├── derby_full.yaml         # §4-ის სრული მაგალითი
│   │   ├── red_card_late_goal.yaml
│   │   └── abandoned_match.yaml    # status 9, bet_cancel
│   ├── chaos/
│   │   ├── producer_down_live.yaml
│   │   ├── duplicates_reorder.yaml
│   │   └── rollback_after_settlement.yaml
│   └── load/
│       ├── saturday_peak.yaml      # 300 L3 მატჩი + burst
│       └── recovery_storm.yaml     # 2000 event snapshot
├── src/uofsim/
│   ├── __init__.py
│   ├── __main__.py                 # `python -m uofsim` → cli
│   ├── config.py                   # pydantic-settings: AMQP/REST/Redis/PG/sim პარამეტრები
│   ├── clock.py                    # SimClock: real | accelerated(xN) | stepped; now_ms(), sleep_until()
│   ├── ids.py                      # URN გენერატორი (sr:match:9xxxxxxxx), request_id, seq
│   ├── rng.py                      # seeded numpy Generator per event (დეტერმინიზმი)
│   ├── domain/
│   │   ├── enums.py                # EventStatus, MatchStatus, MarketStatus, BetStopReason, Certainty, VoidReason
│   │   ├── models.py               # SportEvent, Competitor, Market, Outcome, MatchState (pydantic/dataclass)
│   │   └── producers.py            # Producer registry: id, name, url code (liveodds/pre/vf), scope, recovery window
│   ├── scenario/
│   │   ├── dsl.py                  # YAML DSL-ის pydantic სქემა + ვალიდაცია
│   │   ├── loader.py               # YAML → Scenario, include/extends, relative time პარსინგი
│   │   ├── engine.py               # timeline scheduler: actions + L3 auto-events
│   │   └── actions.py              # goal, card, bet_stop, suspend, settle, rollback, cancel, fixture_change...
│   ├── match/
│   │   ├── base.py                 # MatchEngine ინტერფეისი
│   │   ├── football.py             # ფეხბურთის state machine (0→6→31→7→100) + Poisson goal/card process
│   │   └── settlement.py           # საბოლოო შედეგიდან bet_settlement-ის გამოთვლა (void_factor, dead heat)
│   ├── odds/
│   │   ├── poisson.py              # score matrix, in-play λ, Dixon-Coles ρ
│   │   ├── margin.py               # proportional / power margin, odds ladder rounding
│   │   ├── markets_football.py     # market generators: 1,10,11,16,18,29,41 → outcomes+probabilities
│   │   └── engine.py               # OddsEngine: state → [Market]; market status transitions; throttling
│   ├── messages/
│   │   ├── builder.py              # odds_change, bet_stop, bet_settlement, rollback_*, bet_cancel, fixture_change, alive, snapshot_complete
│   │   ├── routing.py              # routing key builder (8 სეგმენტი) + binding pattern helper-ები
│   │   └── validator.py            # lxml XMLSchema validation, სტრიქტული/sampling რეჟიმი
│   ├── transport/
│   │   ├── publisher.py            # aio-pika publisher, confirms, reconnect, headers
│   │   ├── chaos.py                # Chaos middleware (dup/reorder/delay/drop/corrupt)
│   │   └── queue_watch.py          # RabbitMQ management API: consumer-ების არსებობა → subscribed flag
│   ├── producers/
│   │   ├── manager.py              # ProducerState (UP/DOWN_SILENT/UNSUBSCRIBED), alive loop
│   │   └── recovery.py             # full/after recovery, event odds recovery, stateful messages recovery
│   ├── rest/
│   │   ├── app.py                  # FastAPI factory (mock-rest)
│   │   ├── auth.py                 # x-access-token შემოწმება → 403 XML
│   │   ├── xml.py                  # XMLResponse helper, <response response_code=...>
│   │   └── routes/
│   │       ├── users.py            # /v1/users/whoami.xml
│   │       ├── descriptions.py     # producers.xml, markets.xml, variants, market variant single
│   │       ├── sports.py           # sports.xml, fixture.xml, summary.xml, schedules (live/pre/date), fixtures/changes
│   │       ├── recovery.py         # /v1/{product}/recovery/..., odds/events/..., stateful_messages/...
│   │       ├── replay.py           # /v1/replay/* (Replay Server-თან თავსებადი)
│   │       └── internal.py         # RabbitMQ HTTP auth backend (optional), healthz
│   ├── control/
│   │   ├── api.py                  # Control API (:8090) — runtime ინექციები
│   │   └── cli.py                  # typer CLI: run/goal/card/betstop/producer/chaos/record/replay/load
│   ├── recorder/
│   │   ├── recorder.py             # async batch insert Postgres + JSONL rotate
│   │   ├── replayer.py             # L1: recording → publish (speed, max_delay)
│   │   └── normalize.py            # golden ტესტებისთვის timestamp/request_id ნორმალიზაცია
│   ├── store/
│   │   ├── redis_state.py          # event state, producer state, command stream (XADD/XREADGROUP)
│   │   ├── db.py                   # asyncpg pool
│   │   └── migrations/001_init.sql # recordings, events, runs ცხრილები
│   └── load/
│       └── generator.py            # მაღალი rate-ის publisher (pre-built bodies, multi-worker)
├── tests/
│   ├── unit/                       # routing, poisson, margin, builder+XSD, DSL parsing
│   ├── contract/                   # REST path-ები/სტატუსები/XML ფორმა vs. რეალური sample-ები (როცა გვექნება)
│   ├── scenario/                   # golden-file ტესტები: scenario → normalized JSONL diff
│   └── e2e/                        # docker compose-ში: consumer-ის placeholder + assertions
└── tools/
    ├── gen_tls.sh                  # self-signed CA + server cert (CN=rabbitmq, SAN=localhost)
    ├── fetch_xsd.sh                # XSD-ების ჩამოტვირთვა Sportradar SDK რეპოდან (pinned commit)
    └── capture_real.py             # მომავალში: Integration env-დან sample-ების ჩაწერა contract ტესტებისთვის
```

---

## 4. სცენარის DSL (YAML)

### 4.1 პრინციპები

- **დრო**: `at` — kickoff-თან ფარდობითი სიმულაციური დრო (`-30m`, `0s`, `23m14s`, `90m+3m`), ან `clock: "45+2"` მატჩის საათისთვის. SimClock-ის `speed`-ით მასშტაბირდება. **alive ყოველთვის რეალურ დროში** მიდის (ისევე, როგორც Replay Server-ზე).
- **`model`**: L3 პარამეტრები. თუ `auto_events: false` — მხოლოდ timeline-ის მოვლენები (L2).
- **`timeline`**: დეკლარირებული მოვლენები; თითოეული აქცია `messages/builder.py`-ის ერთ ან რამდენიმე შეტყობინებად ითარგმნება.
- **`expect`** (optional): consumer-ის მხარეს მოსალოდნელი შედეგები e2e ტესტებისთვის.
- **`chaos`**: გლობალური და ფანჯრული (time-window) chaos წესები.

### 4.2 სრული მაგალითი — `scenarios/football/derby_full.yaml`

```yaml
version: 1
id: fb-derby-full-001
description: "Dinamo Tbilisi vs Torpedo Kutaisi — pre-match → live → goals → bet_stop → settlement → rollback"
seed: 20261002
clock:
  mode: accelerated          # real | accelerated | stepped
  speed: 10                  # 1 სიმ. წუთი = 6 რეალური წამი
  start_at: "-35m"           # სიმულაცია იწყება kickoff-მდე 35 წუთით
bookmaker_id: 99999
node_id: null                # თუ მითითებულია, სცენარის recovery პასუხები ამ node-ზე

event:
  urn: sr:match:900000001
  sport: { id: sr:sport:1, name: Soccer }
  category: { id: sr:category:900001, name: Georgia, country_code: GEO }
  tournament: { id: sr:tournament:900001, name: "Erovnuli Liga (SIM)" }
  season: { id: sr:season:900001, name: "Erovnuli Liga 2026 (SIM)" }
  round: { type: group, number: 27 }
  venue: { id: sr:venue:900001, name: "Boris Paichadze Stadium (SIM)", city: Tbilisi }
  competitors:
    home: { id: sr:competitor:900001, name: "Dinamo Tbilisi (SIM)", abbreviation: DIN }
    away: { id: sr:competitor:900002, name: "Torpedo Kutaisi (SIM)", abbreviation: TOR }
  scheduled: "2026-10-03T16:00:00Z"   # ან "now+35m"
  producers:
    prematch: 3               # Ctrl
    live: 1                   # LO
  booked: true                # live coverage booked (LO შეტყობინებები მოვა)

model:
  type: poisson
  xg: { home: 1.55, away: 1.05 }
  dixon_coles_rho: -0.05
  red_card_factor: { penalised: 0.72, opponent: 1.18 }   # ⚠ ევრისტიკა, არა Betradar
  margin: { value: 0.065, method: power }                # proportional | power
  odds_ladder: decimal_2                                  # დამრგვალება ქვემოთ
  max_odds: 501.0
  auto_events: true          # L3: დამატებითი შემთხვევითი გოლები/ბარათები timeline-ის გარდა
  auto_event_scale: 0.0      # 0 = მხოლოდ timeline-ის გოლები (დეტერმინისტული შედეგისთვის)
  odds_change_interval: { prematch: 60s, live: 5s, jitter: 0.3 }
  markets:
    - { id: 1 }                                      # 1x2
    - { id: 10 }                                     # Double chance
    - { id: 11 }                                     # Draw no bet
    - { id: 18, lines: [0.5, 1.5, 2.5, 3.5, 4.5] }   # Total
    - { id: 16, lines: [-1.5, -1, -0.75, -0.5, -0.25, 0, 0.25, 0.5, 1.5] }  # Handicap (quarter lines → void_factor 0.5)
    - { id: 29 }                                     # BTTS
    - { id: 41, max_goals: 6 }                       # Correct score ⚠ id/specifier
  main_line_favourite: true  # favourite="1" ყველაზე დაბალანსებულ ხაზზე

timeline:
  # ---------- PRE-MATCH (producer 3) ----------
  - at: "-35m"
    do: fixture_change
    change_type: 1           # NEW
  - at: "-35m"
    do: odds_change          # სრული pre-match market set, status=1
    producer: 3
  - at: "-20m"
    do: fixture_change
    change_type: 2           # DATETIME: kickoff +5 წუთით გადაიწია
    new_scheduled: "+5m"
  - at: "-10m"
    do: set_xg               # ტრეიდერის კორექცია (შემადგენლობის ცნობა)
    xg: { home: 1.40, away: 1.10 }

  # ---------- HANDOVER & LIVE (producer 1) ----------
  - at: "-1m"
    do: odds_change
    producer: 3
    market_status: -2        # HANDED_OVER — pre-match market-ები გადაეცემა LO-ს
  - at: "0s"
    do: match_status
    match_status: 6          # 1st half
    status: 1                # live
  - at: "0s"
    do: odds_change
    producer: 1

  # ---------- GOAL 1 (home, 23') ----------
  - at: "23m10s"
    do: bet_stop             # dangerous attack / possible goal
    producer: 1
    groups: all
    market_status: -1        # SUSPENDED
  - at: "23m14s"
    do: goal
    team: home
    player: { id: sr:player:9000011, name: "G. Kvaratskhelia (SIM)" }
  - at: "23m40s"
    do: odds_change          # ახალი ალბათობები 1:0-დან, ყველა market status=1
    producer: 1

  # ---------- RED CARD (away, 38') ----------
  - at: "38m"
    do: red_card
    team: away
    bet_stop: { groups: all, duration: 15s }

  # ---------- HALF-TIME ----------
  - at: "45m+2m"
    do: match_status
    match_status: 31         # halftime
  - at: "45m+2m"
    do: settle               # 1st half market-ები (თუ ჩართულია)
    scope: first_half
    certainty: 1
  - at: "62m"                # 45+2 + 15 წთ შესვენება ≈ 62m (სიმ. დრო)
    do: match_status
    match_status: 7          # 2nd half

  # ---------- GOAL 2 (away, 71') + VAR ----------
  - at: "88m"
    do: goal
    team: away
    var_check: { duration: 90s, outcome: confirmed }   # bet_stop VAR-ის დროს
  # ---------- GOAL 3 (home, 90+1') ----------
  - at: "108m"
    do: goal
    team: home

  # ---------- END ----------
  - at: "112m"
    do: bet_stop
    groups: all
  - at: "112m"
    do: match_status
    match_status: 100        # ended
    status: 3                # ended
  - at: "112m30s"
    do: settle               # LO: live-scouted settlement
    producer: 1
    certainty: 1
    markets: all
  - at: "125m"
    do: match_status
    status: 4                # closed (results confirmed)
  - at: "125m"
    do: settle               # Ctrl: confirmed settlement
    producer: 3
    certainty: 2
    markets: all

  # ---------- CORRECTION: ROLLBACK ----------
  - at: "140m"
    do: correct_score        # ოფიციალური შედეგი 2:1 → 1:1 (90+1' გოლი გაუქმდა)
    home: 1
    away: 1
  - at: "140m"
    do: rollback_bet_settlement
    producer: 3
    markets: affected        # 1, 10, 11, 18(total=2.5), 16, 41 ... ავტომატურად
  - at: "140m5s"
    do: settle
    producer: 3
    certainty: 2
    markets: affected
  - at: "150m"
    do: bet_cancel           # 1st goalscorer market — void_reason 12 (⚠ კოდი გადასამოწმებელი)
    markets: [{ id: 0, note: "placeholder for goalscorer market, not in Phase 1" }]
    enabled: false

chaos:
  global:
    duplicate:   { probability: 0.01 }                 # იგივე body, იგივე rk, 0–500ms შემდეგ
    reorder:     { probability: 0.005, max_shift: 1500ms }
    delay:       { probability: 0.02, range: [200ms, 3s] }
  windows:
    - between: ["70m", "72m"]
      producer_down:
        producer: 1
        mode: silent         # alive აღარ იგზავნება (consumer-მა >20s-ზე DOWN უნდა გამოაცხადოს)
        duration: 45s        # რეალური წამები
        then: unsubscribed   # შემდეგ alive subscribed=0 recovery-მდე
    - between: ["95m", "96m"]
      burst:
        extra_odds_change_per_sec: 50   # rapid odds changes (odds_change_reason=1)
    - between: ["130m", "131m"]
      out_of_order_timestamps: { probability: 0.2, max_skew: 2s }

expect:                       # e2e assertion-ები consumer-ის DB/API-ზე (optional)
  - after: "113m"
    event_status: ended
    market: { id: 1, status: settled_pending_confirmation }
  - after: "141m"
    market: { id: 1, outcome: "2", result: win }     # 1:1 → draw (outcome 2)
  - producer: { id: 1, max_down_detection: 25s, recovered: true }
```

### 4.3 Chaos ოფციების სრული სია

| ოფცია | პარამეტრები | ეფექტი |
|---|---|---|
| `duplicate` | `probability`, `delay` | იგივე შეტყობინება ხელახლა |
| `reorder` | `probability`, `max_shift` | publish-ის თანმიმდევრობის ცვლა (timestamp უცვლელი) |
| `out_of_order_timestamps` | `probability`, `max_skew` | `timestamp` ატრიბუტი წინა შეტყობინებაზე ძველი |
| `delay` | `probability`, `range` | publish-ის დაყოვნება |
| `drop` | `probability`, `types` | შეტყობინების დაკარგვა (recovery-ის სატესტოდ) |
| `producer_down` | `producer`, `mode: silent\|unsubscribed`, `duration`, `then` | alive-ის შეწყვეტა / `subscribed=0` |
| `alive_jitter` | `range` | alive ინტერვალის რყევა (9–14s) |
| `stale_alive` | `lag` | alive `timestamp` ძველი (clock skew) |
| `burst` | `extra_odds_change_per_sec` | rapid odds changes |
| `unknown_market` | `id`, `at` | markets.xml-ში არარსებული market id |
| `recovery_fail` | `http_status: 500\|429\|403`, `count` | recovery endpoint-ის შეცდომა |
| `snapshot_never_completes` | `request_id_match: false` | snapshot_complete არ მოდის ან სხვა request_id-ით |
| `amqp_disconnect` | `duration` | publisher connection-ის გაწყვეტა (broker-side close) |
| `malformed_xml` | `probability` | XSD-ზე არავალიდური body (consumer-ის DLQ ტესტი); XSD validator ამ შემთხვევაში bypass |

---

## 5. კოდის ჩონჩხები (Python 3.12)

### 5.1 Routing key builder — `messages/routing.py`

```python
from __future__ import annotations
from dataclasses import dataclass
from typing import Literal

MessageType = Literal[
    "odds_change", "bet_stop", "bet_settlement", "rollback_bet_settlement",
    "bet_cancel", "rollback_bet_cancel", "fixture_change", "alive", "snapshot_complete",
]
Priority = Literal["hi", "lo"]
_DASH = "-"


@dataclass(frozen=True, slots=True)
class Interest:
    prematch: bool = False   # segment 2: "pre"
    virtual: bool = False    # segment 2: "virt"
    live: bool = False       # segment 3: "live"


def split_urn(urn: str) -> tuple[str, str]:
    """'sr:match:12345' -> ('sr:match', '12345'); 'vf:match:1' -> ('vf:match', '1')."""
    prefix, _, ident = urn.rpartition(":")
    if not prefix or not ident:
        raise ValueError(f"bad URN: {urn!r}")
    return prefix, ident


def routing_key(
    msg_type: MessageType,
    *,
    priority: Priority | None = None,
    interest: Interest = Interest(),
    sport_id: int | None = None,
    event_urn: str | None = None,
    node_id: int | None = None,
) -> str:
    """<priority>.<pre|virt|->.<live|->.<type>.<sport_id>.<urn_type>.<event_id>.<node_id>

    Examples:
      hi.-.live.odds_change.1.sr:match.900000001.-
      hi.pre.-.odds_change.1.sr:match.900000001.-
      -.-.-.alive.-.-.-.-
      -.-.-.snapshot_complete.-.-.-.1
    """
    seg2 = "virt" if interest.virtual else ("pre" if interest.prematch else _DASH)
    seg3 = "live" if interest.live else _DASH
    if event_urn:
        urn_type, event_id = split_urn(event_urn)
    else:
        urn_type = event_id = _DASH
    parts = [
        priority or _DASH,
        seg2,
        seg3,
        msg_type,
        str(sport_id) if sport_id is not None else _DASH,
        urn_type,
        event_id,
        str(node_id) if node_id is not None else _DASH,
    ]
    return ".".join(parts)


def system_key(msg_type: Literal["alive", "snapshot_complete"], node_id: int | None = None) -> str:
    return routing_key(msg_type, node_id=node_id)


def priority_for(event_starts_in_s: float, is_live: bool) -> Priority:
    # ⚠ გადასამოწმებელი: Betradar-ის "lo" priority-ის ზუსტი წესი; ვიყენებთ ევრისტიკას
    return "hi" if is_live or event_starts_in_s < 24 * 3600 else "lo"
```

Consumer-ის binding pattern-ები (Betradar რეკომენდაცია — ყოველთვის `.#`-ით დასრულება):
`#` (ყველაფერი), `*.*.live.#` (live), `*.pre.-.#` (მხოლოდ pre-match), `-.-.-.#` (system: alive, snapshot_complete), `*.*.*.*.*.*.*.{node_id}` + `*.*.*.*.*.*.*.-` (multi-node) ⚠ გადასამოწმებელი ზუსტი node pattern-ები.

### 5.2 odds_change XML builder + XSD validator — `messages/builder.py`, `messages/validator.py`

```python
from __future__ import annotations
from dataclasses import dataclass, field
from lxml import etree

@dataclass(slots=True)
class Outcome:
    id: str                 # "1", "12", "sr:player:123" ...
    odds: float | None
    probability: float | None
    active: bool = True

@dataclass(slots=True)
class Market:
    id: int
    status: int = 1                                  # 1 active, -1 suspended, 0 inactive, -2 handed over, -3 settled, -4 cancelled
    specifiers: dict[str, str] = field(default_factory=dict)
    favourite: bool = False
    outcomes: list[Outcome] = field(default_factory=list)
    next_betstop: int | None = None

    @property
    def specifier_str(self) -> str | None:
        # ⚠ გადასამოწმებელი: specifier-ების თანმიმდევრობა რეალურ feed-ში
        return "|".join(f"{k}={v}" for k, v in self.specifiers.items()) or None

@dataclass(slots=True)
class EventStatusSnapshot:
    status: int                 # 0 not_started, 1 live, 2 suspended, 3 ended, 4 closed ...
    match_status: int           # 0, 6, 31, 7, 100 ...
    home_score: int = 0
    away_score: int = 0
    match_time: str | None = None   # "23:14"
    period_scores: list[tuple[int, int, int, int]] = field(default_factory=list)  # (number, code, home, away)
    red_cards: tuple[int, int] = (0, 0)
    yellow_cards: tuple[int, int] = (0, 0)


def _ses(parent: etree._Element, s: EventStatusSnapshot) -> None:
    el = etree.SubElement(parent, "sport_event_status",
                          status=str(s.status), match_status=str(s.match_status),
                          home_score=str(s.home_score), away_score=str(s.away_score))
    # XSD sequence order: clock, period_scores, results, statistics
    if s.match_time:
        etree.SubElement(el, "clock", match_time=s.match_time)
    if s.period_scores:
        ps = etree.SubElement(el, "period_scores")
        for number, code, h, a in s.period_scores:
            etree.SubElement(ps, "period_score", number=str(number), match_status_code=str(code),
                             home_score=str(h), away_score=str(a))
    st = etree.SubElement(el, "statistics")
    etree.SubElement(st, "yellow_cards", home=str(s.yellow_cards[0]), away=str(s.yellow_cards[1]))
    etree.SubElement(st, "red_cards", home=str(s.red_cards[0]), away=str(s.red_cards[1]))


def build_odds_change(
    *, product: int, event_urn: str, timestamp_ms: int, status: EventStatusSnapshot | None,
    markets: list[Market], request_id: int | None = None,
    betting_status: int | None = None, betstop_reason: int | None = None,
    odds_change_reason: int | None = None,
) -> bytes:
    root = etree.Element("odds_change", product=str(product), event_id=event_urn,
                         timestamp=str(timestamp_ms))
    if request_id is not None:
        root.set("request_id", str(request_id))
    if odds_change_reason is not None:
        root.set("odds_change_reason", str(odds_change_reason))   # 1 = rapid odds change ⚠
    if status is not None:
        _ses(root, status)
    odds = etree.SubElement(root, "odds")
    if betting_status is not None:
        odds.set("betting_status", str(betting_status))
    if betstop_reason is not None:
        odds.set("betstop_reason", str(betstop_reason))
    for m in markets:
        mel = etree.SubElement(odds, "market", id=str(m.id), status=str(m.status))
        if (spec := m.specifier_str):
            mel.set("specifiers", spec)
        if m.favourite:
            mel.set("favourite", "1")
        if m.next_betstop:
            etree.SubElement(mel, "market_metadata", next_betstop=str(m.next_betstop))
        if m.status in (0, -3, -4):      # inactive/settled/cancelled: outcome-ების გარეშე
            continue
        for o in m.outcomes:
            oel = etree.SubElement(mel, "outcome", id=o.id, active="1" if o.active else "0")
            if o.odds is not None and o.active:
                oel.set("odds", f"{o.odds:.2f}")
            if o.probability is not None:
                oel.set("probabilities", f"{o.probability:.7f}")   # XSD: "probabilities" (მრავლობითი)
    return etree.tostring(root, xml_declaration=True, encoding="UTF-8", standalone=True)


def build_alive(product: int, timestamp_ms: int, subscribed: bool) -> bytes:
    el = etree.Element("alive", product=str(product), timestamp=str(timestamp_ms),
                       subscribed="1" if subscribed else "0")
    return etree.tostring(el, xml_declaration=True, encoding="UTF-8", standalone=True)


def build_snapshot_complete(product: int, timestamp_ms: int, request_id: int) -> bytes:
    el = etree.Element("snapshot_complete", product=str(product),
                       timestamp=str(timestamp_ms), request_id=str(request_id))
    return etree.tostring(el, xml_declaration=True, encoding="UTF-8", standalone=True)
```

```python
# messages/validator.py
import random
from pathlib import Path
from lxml import etree

class InvalidMessage(ValueError): ...

class XsdValidator:
    def __init__(self, xsd_path: Path, sample_rate: float = 1.0) -> None:
        self._schema = etree.XMLSchema(etree.parse(str(xsd_path)))
        self._sample = sample_rate

    def validate(self, body: bytes) -> None:
        if self._sample < 1.0 and random.random() > self._sample:
            return
        doc = etree.fromstring(body)
        if not self._schema.validate(doc):
            raise InvalidMessage(str(self._schema.error_log.last_error))
```

### 5.3 Poisson odds მოდელი — `odds/poisson.py`, `odds/margin.py`

```python
from __future__ import annotations
import math
from dataclasses import dataclass
import numpy as np

MAX_GOALS = 12

def pmf(lam: float, n: int = MAX_GOALS) -> np.ndarray:
    k = np.arange(n + 1)
    if lam <= 0:
        out = np.zeros(n + 1); out[0] = 1.0
        return out
    logp = -lam + k * math.log(lam) - np.array([math.lgamma(i + 1) for i in k])
    return np.exp(logp)

def score_matrix(lh: float, la: float, rho: float = 0.0, n: int = MAX_GOALS) -> np.ndarray:
    """P(remaining home goals = i, remaining away goals = j). Dixon-Coles ρ low-score correction."""
    m = np.outer(pmf(lh, n), pmf(la, n))
    if rho:
        m[0, 0] *= 1 - lh * la * rho
        m[0, 1] *= 1 + lh * rho
        m[1, 0] *= 1 + la * rho
        m[1, 1] *= 1 - rho
    return m / m.sum()

@dataclass(slots=True)
class MatchState:
    minute: float = 0.0          # 0..90 (+ stoppage)
    home: int = 0
    away: int = 0
    red_home: int = 0
    red_away: int = 0

def in_play_lambdas(xg_h: float, xg_a: float, st: MatchState, *, total: float = 90.0,
                    pen: float = 0.72, opp: float = 1.18) -> tuple[float, float]:
    frac = max(0.0, (total - st.minute) / total)
    kh = pen ** st.red_home * opp ** st.red_away
    ka = pen ** st.red_away * opp ** st.red_home
    return xg_h * frac * kh, xg_a * frac * ka

class FootballProbs:
    """საბოლოო ანგარიშის განაწილება: current score + remaining goals."""
    def __init__(self, xg_h: float, xg_a: float, st: MatchState, rho: float = 0.0) -> None:
        lh, la = in_play_lambdas(xg_h, xg_a, st)
        self.m = score_matrix(lh, la, rho)
        i, j = np.indices(self.m.shape)
        self.fh, self.fa = i + st.home, j + st.away

    def p_1x2(self) -> dict[str, float]:            # market 1: outcomes 1/2/3 = home/draw/away
        return {"1": float(self.m[self.fh > self.fa].sum()),
                "2": float(self.m[self.fh == self.fa].sum()),
                "3": float(self.m[self.fh < self.fa].sum())}

    def p_total(self, line: float) -> dict[str, float]:   # market 18: 12 over / 13 under ⚠
        tot = self.fh + self.fa
        return {"12": float(self.m[tot > line].sum()), "13": float(self.m[tot < line].sum())}

    def p_handicap(self, hcp: float) -> dict[str, float]: # market 16: 1714 home / 1715 away ⚠ (.5 lines)
        diff = self.fh - self.fa + hcp
        return {"1714": float(self.m[diff > 0].sum()), "1715": float(self.m[diff < 0].sum())}

    def p_btts(self) -> dict[str, float]:                 # market 29: 74 yes / 76 no ⚠
        yes = float(self.m[(self.fh > 0) & (self.fa > 0)].sum())
        return {"74": yes, "76": 1.0 - yes}

    def p_correct_score(self, max_goals: int = 6) -> dict[str, float]:
        out: dict[str, float] = {}
        for h in range(max_goals + 1):
            for a in range(max_goals + 1):
                out[f"{h}:{a}"] = float(self.m[(self.fh == h) & (self.fa == a)].sum())
        out["other"] = max(0.0, 1.0 - sum(out.values()))
        return out    # outcome id-ები variant descriptions-დან (⚠)
```

```python
# odds/margin.py
def apply_margin(probs: dict[str, float], margin: float, method: str = "power",
                 min_odds: float = 1.01, max_odds: float = 501.0,
                 min_prob: float = 0.002) -> dict[str, tuple[float | None, bool]]:
    """Returns {outcome_id: (odds, active)}; ძალიან დაბალი ალბათობა → active=0."""
    s = sum(probs.values())
    fair = {k: v / s for k, v in probs.items()}
    if method == "proportional":
        implied = {k: p * (1 + margin) for k, p in fair.items()}
    else:  # power: q_i = p_i ** c, Σq = 1 + margin  (ფავორიტ/ლონგშოტ bias-ის რეალისტურად ასახვა)
        lo, hi = 0.5, 1.0
        for _ in range(60):
            c = (lo + hi) / 2
            if sum(p ** c for p in fair.values() if p > 0) > 1 + margin:
                lo = c
            else:
                hi = c
        implied = {k: (p ** c if p > 0 else 0.0) for k, p in fair.items()}
    out: dict[str, tuple[float | None, bool]] = {}
    for k, q in implied.items():
        if fair[k] < min_prob or q <= 0:
            out[k] = (None, False)
            continue
        odds = min(max(1.0 / q, min_odds), max_odds)
        out[k] = (ladder_floor(odds), True)
    return out

def ladder_floor(o: float) -> float:
    """ქვემოთ დამრგვალება, რომ მარჟა არ დაიკარგოს."""
    step = 0.01 if o < 3 else 0.05 if o < 10 else 0.5 if o < 30 else 1.0
    return round(int(o / step) * step, 2)
```

> შენიშვნა: quarter line-ები (−0.25, −0.75) = ორი ნახევარ-ფსონის კომბინაცია (`hcp-0.25` და `hcp+0.25`); settlement-ისას იძლევა `void_factor="0.5"`. ინტეგრალური ხაზი (0, −1) → push → `void_factor="1"`.

### 5.4 AMQP Publisher — `transport/publisher.py`

```python
from __future__ import annotations
import ssl
import aio_pika
from aio_pika import DeliveryMode, ExchangeType, Message

class UofPublisher:
    def __init__(self, url: str, exchange: str = "unifiedfeed", tls_ca: str | None = None,
                 recorder=None, validator=None) -> None:
        self._url, self._exchange_name = url, exchange
        self._ssl = ssl.create_default_context(cafile=tls_ca) if tls_ca else None
        self._recorder, self._validator = recorder, validator
        self._conn = self._ch = self._ex = None

    async def connect(self) -> None:
        # url: amqps://publisher:pwd@rabbitmq:5671/%2Funifiedfeed%2F99999
        self._conn = await aio_pika.connect_robust(self._url, ssl_context=self._ssl)
        self._ch = await self._conn.channel(publisher_confirms=True)
        self._ex = await self._ch.declare_exchange(self._exchange_name, ExchangeType.TOPIC, durable=True)

    async def publish(self, routing_key: str, body: bytes, *, timestamp_ms: int,
                      meta: dict | None = None) -> None:
        if self._validator:
            self._validator.validate(body)
        msg = Message(
            body,
            content_type="text/xml",
            delivery_mode=DeliveryMode.NOT_PERSISTENT,          # ⚠ რეალური feed-ის persistence
            headers={"timestamp_in_ms": timestamp_ms},          # ⚠ header-ის სახელი გადასამოწმებელი
        )
        await self._ex.publish(msg, routing_key=routing_key)   # confirm-ს ელოდება
        if self._recorder:
            await self._recorder.record(routing_key, body, timestamp_ms, meta or {})

    async def close(self) -> None:
        if self._conn:
            await self._conn.close()
```

### 5.5 FastAPI recovery endpoint — `rest/routes/recovery.py` + engine-side handler

```python
from __future__ import annotations
import time
from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import Response
from uofsim.domain.producers import REGISTRY           # code -> Producer(id, recovery_window_ms, ...)
from uofsim.rest.auth import require_token
from uofsim.rest.xml import response_xml

router = APIRouter()

@router.post("/v1/{product}/recovery/initiate_request")
async def initiate_recovery(
    request: Request,
    product: str,
    request_id: int | None = Query(None),
    after: int | None = Query(None, description="epoch ms"),
    node_id: int | None = Query(None),
    _token: str = Depends(require_token),
) -> Response:
    redis = request.app.state.redis
    prod = REGISTRY.by_code(product)
    if prod is None:
        return response_xml("NOT_FOUND", f"Unknown product {product}", status=404)
    now = int(time.time() * 1000)
    if after is not None and now - after > prod.recovery_window_ms:
        # ⚠ რეალური სტატუსი/ტექსტი გადასამოწმებელი
        return response_xml("FORBIDDEN", "Recovery 'after' timestamp too old", status=403)
    # rate limit (⚠ რეალური ლიმიტები): 1 მოთხოვნა / producer / node / 30s
    rl_key = f"sim:rl:recovery:{prod.id}:{node_id or '-'}"
    if not await redis.set(rl_key, "1", nx=True, ex=30):
        return response_xml("TOO_MANY_REQUESTS", "Recovery rate limit", status=429)
    if (chaos := await redis.hgetall("sim:chaos:recovery")) and chaos.get(b"fail_status"):
        return response_xml("ERROR", "Injected failure", status=int(chaos[b"fail_status"]))
    await redis.xadd("sim:commands", {
        "type": "recovery", "product": prod.id, "after": after or 0,
        "request_id": request_id or 0, "node_id": node_id if node_id is not None else -1,
    })
    return response_xml("ACCEPTED", "Request for recovery accepted", status=202)  # ⚠ body ტექსტი


@router.post("/v1/{product}/odds/events/{event_urn}/initiate_request")
async def event_odds_recovery(request: Request, product: str, event_urn: str,
                              request_id: int | None = None, node_id: int | None = None,
                              _token: str = Depends(require_token)) -> Response:
    prod = REGISTRY.by_code(product)
    if prod is None:
        return response_xml("NOT_FOUND", f"Unknown product {product}", status=404)
    await request.app.state.redis.xadd("sim:commands", {
        "type": "event_odds", "product": prod.id, "event": event_urn,
        "request_id": request_id or 0, "node_id": node_id if node_id is not None else -1})
    return response_xml("ACCEPTED", "Request for event odds accepted", status=202)
```

```python
# producers/recovery.py  (engine process — consumes sim:commands)
import asyncio
from uofsim.messages import builder, routing

class RecoveryHandler:
    def __init__(self, state, odds_engine, publisher, producers, recordings, clock) -> None:
        self.state, self.odds, self.pub = state, odds_engine, publisher
        self.producers, self.recordings, self.clock = producers, recordings, clock

    async def handle(self, cmd: dict) -> None:
        product, rid = int(cmd["product"]), int(cmd["request_id"])
        node = None if int(cmd["node_id"]) < 0 else int(cmd["node_id"])
        after = int(cmd.get("after") or 0)
        await asyncio.sleep(self.producers.recovery_start_delay(product))   # რეალისტური 1–5s

        # 1) odds snapshot: ყველა აქტიური event-ის სრული odds_change request_id-ით
        for ev in self.state.active_events(product):
            body = builder.build_odds_change(
                product=product, event_urn=ev.urn, timestamp_ms=self.clock.now_ms(),
                status=ev.status_snapshot(), markets=self.odds.current_markets(ev), request_id=rid)
            rk = routing.routing_key("odds_change", priority="hi", interest=ev.interest(product),
                                     sport_id=ev.sport_id, event_urn=ev.urn, node_id=node)
            await self.pub.publish(rk, body, timestamp_ms=self.clock.now_ms(),
                                   meta={"recovery": rid})

        # 2) stateful messages after `after`: bet_settlement, bet_cancel, rollback_* (Recorder-დან)
        if after:
            async for rec in self.recordings.stateful_since(product, after):
                body = builder.with_request_id(rec.body, rid)
                rk = routing.with_node(rec.routing_key, node)
                await self.pub.publish(rk, body, timestamp_ms=self.clock.now_ms(),
                                       meta={"recovery": rid, "replayed": rec.seq})

        # 3) snapshot_complete
        if not self.producers.chaos_skip_snapshot_complete(product):
            await self.pub.publish(
                routing.system_key("snapshot_complete", node_id=node),
                builder.build_snapshot_complete(product, self.clock.now_ms(), rid),
                timestamp_ms=self.clock.now_ms())
        self.producers.mark_subscribed(product)
```

### 5.6 Alive heartbeat loop — `producers/manager.py`

```python
from __future__ import annotations
import asyncio, enum, time
from dataclasses import dataclass
from uofsim.messages import builder, routing

class PState(enum.Enum):
    UP = "up"
    DOWN_SILENT = "down_silent"        # alive არ იგზავნება
    UNSUBSCRIBED = "unsubscribed"      # alive subscribed=0 recovery-მდე

@dataclass
class ProducerRuntime:
    id: int
    state: PState = PState.UP
    down_until: float | None = None
    then: PState | None = None

class ProducerManager:
    def __init__(self, producer_ids: list[int], publisher, interval_s: float = 10.0,
                 queue_watch=None, no_consumer_grace_s: float = 60.0) -> None:
        self.p = {pid: ProducerRuntime(pid) for pid in producer_ids}
        self.pub, self.interval, self.qw = publisher, interval_s, queue_watch
        self.grace = no_consumer_grace_s

    def force_down(self, pid: int, mode: PState, duration_s: float, then: PState | None) -> None:
        rt = self.p[pid]
        rt.state, rt.down_until, rt.then = mode, time.monotonic() + duration_s, then

    def mark_subscribed(self, pid: int) -> None:
        self.p[pid].state = PState.UP

    async def run(self) -> None:
        while True:
            now_ms = int(time.time() * 1000)             # alive — ყოველთვის რეალური wall-clock
            consumers_missing = (await self.qw.seconds_without_consumers() > self.grace
                                 if self.qw else False)
            for rt in self.p.values():
                if rt.down_until and time.monotonic() >= rt.down_until:
                    rt.state, rt.down_until = (rt.then or PState.UP), None
                if consumers_missing and rt.state is PState.UP:
                    rt.state = PState.UNSUBSCRIBED       # consumer დიდხანს არ იყო → recovery სავალდებულო
                if rt.state is PState.DOWN_SILENT:
                    continue
                body = builder.build_alive(rt.id, now_ms, subscribed=rt.state is PState.UP)
                await self.pub.publish(routing.system_key("alive"), body, timestamp_ms=now_ms)
            await asyncio.sleep(self.interval)
```

---

## 6. Producer-ებისა და Recovery-ის სიმულაცია

### 6.1 Producer-ები (სიმულატორში)

| id | name | URL code | scope | recovery window | სიმულატორში |
|---|---|---|---|---|---|
| 1 | LO (Live Odds) | `liveodds` | live | 10 სთ (ახალი დოკ.) / 72 სთ (ძველი SDK) ⚠ | ✅ |
| 3 | Ctrl (Betradar Ctrl) | `pre` | prematch | 72 სთ | ✅ |
| 4 | BetPal | `betpal` | live | 72 სთ ⚠ | ❌ (Phase 2) |
| 6 | VF (Virtual Football) | `vf` | virtual | 3 სთ | optional (S5) |

`/v1/descriptions/producers.xml` აბრუნებს თითოეულს `api_url`, `active`, `scope`, `stateful_recovery_window_in_minutes` ატრიბუტებით ⚠ გადასამოწმებელი ატრიბუტების ზუსტი ჩამონათვალი.

### 6.2 Alive

- ინტერვალი: **10 წამი** თითო producer-ზე (კონფიგურირებადი `alive.interval_s`; chaos-ით jitter 9–14s).
- Routing key: `-.-.-.alive.-.-.-.-`.
- Body: `<alive product="1" timestamp="1759420800000" subscribed="1"/>`.
- `timestamp` — **wall-clock** (accelerated სცენარშიც კი), რომ consumer-ის „alive lag" შემოწმება რეალისტური იყოს.

### 6.3 `subscribed=0` შემთხვევები (სიმულატორში)

| ტრიგერი | ქცევა |
|---|---|
| Consumer-ის queue-ს არ ჰყავს consumer-ი > `no_consumer_grace_s` (default 60s; RabbitMQ management API-ით) | producer → `UNSUBSCRIBED`, alive `subscribed=0` სანამ recovery არ დასრულდება |
| Control API: `POST /control/producers/1/unsubscribe` | იგივე, ხელით |
| Chaos window `producer_down … then: unsubscribed` | silent პერიოდის შემდეგ `subscribed=0` |
| სიმულატორის restart | ყველა producer `UNSUBSCRIBED` პირველ alive-ზე (რეალურ feed-ში ეს ასე იქნება? ⚠) |

> რეალურ Betradar-ზე `subscribed=0` ნიშნავს, რომ Betradar-ის მხარეს ჩვენი „სესია" producer-ისთვის აღარ ითვლება აქტიურად და **recovery სავალდებულოა**. ზუსტი ტრიგერები ⚠ გადასამოწმებელი.

### 6.4 Producer down-ის დეტექცია (consumer-ის მხარე — რას ვამოწმებთ სიმულატორით)

Consumer-მა producer DOWN უნდა გამოაცხადოს, თუ:
1. ბოლო alive-დან გავიდა **> 20s** (`max_inactivity_seconds`, SDK default 20, დიაპაზონი 10–180);
2. მოვიდა alive `subscribed=0`;
3. შეტყობინების დამუშავების lag (now − message timestamp) > threshold (SDK-ის „processing queue delay" ⚠);
4. AMQP connection გაწყდა.

DOWN-ზე: შესაბამისი producer-ის market-ებზე ფსონის მიღების შეჩერება (Betradar: „stop betting if producer non-active > 20s") → recovery.

### 6.5 Recovery სემანტიკა

| ასპექტი | რეალური Betradar | სიმულატორი |
|---|---|---|
| Full / after recovery | `POST /v1/{product}/recovery/initiate_request?after={ms}&request_id={id}&node_id={n}` | იგივე path/params |
| `after` | ბოლო წარმატებით დამუშავებული alive-ის timestamp (subscribed=1) | იგივე; window-ს გარეთ → 403 ⚠ |
| პასუხი | 202 Accepted (XML `<response response_code="ACCEPTED">`) ⚠ | იგივე |
| Snapshot | odds_change ყველა აქტიურ event-ზე `request_id`-ით + stateful messages (settlement/cancel) `after`-ის შემდეგ | იგივე (Recorder-დან stateful) |
| დასრულება | `snapshot_complete request_id=…` (rk `-.-.-.snapshot_complete.-.-.-.{node}`) | იგივე |
| Timeout | SDK: 1200s (default) → ხელახალი მოთხოვნა | chaos `snapshot_never_completes` |
| Rate limit | min interval 30s (SDK), სერვერზე დამატებითი ლიმიტები ⚠ | 1 req / 30s / producer / node → 429 |
| node_id | multi-instance consumer: recovery-ის შეტყობინებები მხოლოდ მომთხოვნ node-ზე routing key-ის 8-ე სეგმენტით | იგივე |
| Recovery-ს დროს live შეტყობინებები | odds_change **request_id-ის გარეშე** ჩვეულებრივ მუშავდება | engine პარალელურად აგრძელებს live-ს |
| Event odds recovery | `POST /v1/{product}/odds/events/{urn}/initiate_request` | odds_change ერთ event-ზე, `request_id`-ით |
| Stateful messages per event | `POST /v1/{product}/stateful_messages/events/{urn}/initiate_request` ⚠ | Recorder-დან replay |

### 6.6 Producer/Recovery სახელმწიფო მანქანა (consumer-ის მოსალოდნელი)

```mermaid
stateDiagram-v2
    [*] --> Down: startup
    Down --> Recovering: alive მოვიდა → POST initiate_request
    Recovering --> Up: snapshot_complete(request_id ემთხვევა)
    Recovering --> Down: timeout 1200s / HTTP error
    Up --> Down: alive >20s არ არის / subscribed=0 / lag / disconnect
```

---

## 7. Chaos / Edge-case ტესტების კატალოგი

| # | შემთხვევა | როგორ ვაწყობთ სიმულატორში | Consumer-ის მოსალოდნელი ქცევა | Pass კრიტერიუმი |
|---|---|---|---|---|
| C01 | Duplicate შეტყობინებები | `duplicate.probability=0.05` | იდემპოტენტური დამუშავება (event+market+specifiers+timestamp hash) | DB-ში დუბლიკატი settlement/odds ვერსია 0 |
| C02 | Out-of-order timestamps | `out_of_order_timestamps` | per-event/market „last timestamp wins"; ძველი odds_change ignore | მიმდინარე odds = უახლესი timestamp-ის |
| C03 | Reorder (publish order) | `reorder.max_shift=1500ms` | bet_stop → odds_change თანმიმდევრობის დარღვევის უსაფრთხო დამუშავება | market არ რჩება შეცდომით active bet_stop-ის შემდეგ |
| C04 | Missing alive | `producer_down mode=silent duration=45s` | 20s-ზე DOWN, betting stop; alive-ის დაბრუნებისას recovery | detection ≤ 25s; recovery დასრულდა |
| C05 | `subscribed=0` | `then: unsubscribed` / control API | recovery `after`-ით | snapshot_complete მიღებული, state სწორია |
| C06 | Recovery 429/500/403 | `recovery_fail` | backoff ≥30s, retry; 403 (too old) → full recovery `after`-ის გარეშე ⚠ | მაქს. 1 req/30s |
| C07 | snapshot_complete არ მოდის | `snapshot_never_completes` | timeout → retry ახალი request_id-ით | producer არ ხდება UP მის გარეშე |
| C08 | snapshot_complete სხვა request_id-ით | `request_id_match:false` | ignore | producer DOWN რჩება |
| C09 | Rollback after settlement | `rollback_bet_settlement` + ახალი `bet_settlement` | ფსონების გადახდის გაუქმება და ხელახალი settlement; ბალანსის კორექცია | wallet ledger ბალანსდება |
| C10 | `void_factor="0.5"` | handicap −0.25 / total 2.25 | ნახევარი refund + ნახევარი win/lose | payout ფორმულა სწორია (ტესტ-ვექტორები) |
| C11 | `void_factor="1"` (push) | handicap 0, total 2.0 და ზუსტი შედეგი | სრული refund | payout = stake |
| C12 | Dead heat | test market (`dead_heat_factor="0.5"`, 2 მოგებული outcome) | payout × dead_heat_factor | payout ტესტ-ვექტორები |
| C13 | bet_cancel + rollback_bet_cancel | `bet_cancel` start/end_time ფანჯრით, შემდეგ rollback | მხოლოდ ფანჯარაში დადებული ფსონების void, შემდეგ აღდგენა | |
| C14 | Market status transitions | 1→−1→1, 1→0, 1→−2 (handover), →−3, →−4 | ყველა გადასვლა ასახულია; −2-ის შემდეგ pre-match odds აღარ გამოიყენება | state-machine ტესტი |
| C15 | bet_stop `groups` + `market_status` | `groups="all"` / კონკრეტული ჯგუფი, market_status ატრიბუტით/გარეშე | default → suspend (−1) ⚠ | |
| C16 | Unknown market id | `unknown_market id=9999` | `GET /v1/descriptions/en/markets.xml` refresh (ან single market) → თუ მაინც არ არის, market ignore + alert | refresh ≤1 per N წთ (throttle) |
| C17 | Variant market | `specifiers="variant=sr:correct_score:max:6"` ⚠ | `GET /v1/descriptions/en/markets/{id}/variants/{variant}.xml` ⚠ path | outcome სახელები სწორად იხსნება |
| C18 | Fixture change (datetime/cancel) | `fixture_change change_type=2/3` | REST fixture refresh; კალენდარი განახლდა | |
| C19 | Event abandoned / cancelled | status 9 → `bet_cancel` ყველა market | void | |
| C20 | Settlement certainty 1→2 | LO settle certainty=1, შემდეგ Ctrl certainty=2 | პოლიტიკა: payout certainty=1-ზე ან 2-ზე (ბიზნეს-გადაწყვეტილება) | კონფიგურირებადი |
| C21 | Settlement before match end | (Betradar-ის early settlement, მაგ. BTTS yes 2 გოლის შემდეგ) | ნაწილობრივი settlement live-ში | |
| C22 | AMQP disconnect | `amqp_disconnect duration=30s` | reconnect, queue redeclare, recovery | მონაცემები სრულია |
| C23 | Malformed XML | `malformed_xml` | DLQ + alert, consumer არ ვარდება | |
| C24 | Rapid odds change | `burst` + `odds_change_reason=1` | throttle/coalesce UI-სთვის | lag არ იზრდება |
| C25 | Recovery storm | `scenarios/load/recovery_storm.yaml`: 2000 event snapshot | consumer ამუშავებს < 120s-ში ⚠ სამიზნე | queue ცარიელდება |
| C26 | Very high message rate | load profile (ქვემოთ) | p99 latency SLO | ქვემოთ |

### 7.1 Load-test სამიზნე რიცხვები (⚠ შეფასებები, რეალური პიკის მონაცემები Betradar-ისგან გადასამოწმებელი)

| პროფილი | აღწერა | msg/s | ხანგრძლივობა | შეტყობინების ზომა | Consumer SLO |
|---|---|---|---|---|---|
| **Baseline** | 200 live ფეხბურთის მატჩი, odds_change 2–5s | 60–100 | 2 სთ (soak) | median 6 KB, p99 60 KB | p99 ingest→DB < 150 ms |
| **Saturday peak** | 1,000 live event, მრავალი სპორტი | 1,500 sustained | 15 წთ | median 8 KB, p99 150 KB | p99 < 250 ms, queue < 10k |
| **Goal burst** | ერთდროული bet_stop + odds_change ბევრ მატჩზე | 5,000 burst | 30 წმ | 2–20 KB | queue drain < 60 s |
| **Recovery storm** | 2,000 event full snapshot (~40 KB) ≈ 80 MB | max broker rate | — | 40 KB | snapshot_complete-მდე < 120 s |
| **Stress (breaking point)** | ზრდა 500 msg/s-ით ყოველ 2 წთ-ში | 10,000+ | ავარიამდე | 8 KB | ვზომავთ ზღვარს |

სიმულატორის load generator: pre-built body-ები, N publisher worker (multiprocess), batch confirms; სამიზნე ერთ ჰოსტზე ≥ 10k msg/s 8 KB-ზე ⚠ (Python + aio-pika; საჭიროების შემთხვევაში `load/generator.py` შეიძლება გადავიდეს Go-ზე).

---

## 8. `docker-compose.yml`

```yaml
name: uof-sim

x-sim-env: &sim-env
  BOOKMAKER_ID: ${BOOKMAKER_ID:-99999}
  SIM_TOKEN: ${SIM_TOKEN:-sim-token-0000000000}
  AMQP_URL: amqps://uofsim-publisher:${PUBLISHER_PASSWORD:-publisher}@rabbitmq:5671/%2Funifiedfeed%2F${BOOKMAKER_ID:-99999}
  AMQP_TLS_CA: /certs/ca.pem
  REDIS_URL: redis://redis:6379/0
  PG_DSN: postgresql://uofsim:uofsim@postgres:5432/uofsim
  RABBIT_MGMT_URL: http://rabbitmq:15672
  SIM_SPEED: ${SIM_SPEED:-10}
  SIM_SEED: ${SIM_SEED:-42}

services:
  rabbitmq:
    image: rabbitmq:3.13-management
    hostname: rabbitmq
    ports:
      - "5671:5671"     # AMQPS (TLS) — Betradar-ის მსგავსად
      - "5672:5672"     # AMQP plain (მხოლოდ dev debug)
      - "15672:15672"   # Management UI (http://localhost:15672)
    volumes:
      - ./config/rabbitmq/rabbitmq.conf:/etc/rabbitmq/rabbitmq.conf:ro
      - ./config/rabbitmq/enabled_plugins:/etc/rabbitmq/enabled_plugins:ro
      - ./config/rabbitmq/definitions.json:/etc/rabbitmq/definitions.json:ro
      - ./config/tls:/etc/rabbitmq/tls:ro
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 10s
      timeout: 5s
      retries: 10

  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: uofsim
      POSTGRES_PASSWORD: uofsim
      POSTGRES_DB: uofsim
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./src/uofsim/store/migrations:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U uofsim"]
      interval: 5s
      retries: 10

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      retries: 10

  simulator:
    build: .
    command: ["python", "-m", "uofsim", "engine", "--control-port", "8090"]
    environment:
      <<: *sim-env
    ports:
      - "8090:8090"     # Control API (+ /docs Swagger)
    volumes:
      - ./scenarios:/app/scenarios:ro
      - ./data:/app/data
      - ./config/tls:/certs:ro
    depends_on:
      rabbitmq: { condition: service_healthy }
      redis: { condition: service_healthy }
      postgres: { condition: service_healthy }

  mock-rest:
    build: .
    command: ["python", "-m", "uofsim", "rest", "--port", "8080"]
    environment:
      <<: *sim-env
    ports:
      - "8080:8080"     # Mock Betradar REST: http://localhost:8080/v1/users/whoami.xml
    volumes:
      - ./data:/app/data:ro
    depends_on:
      redis: { condition: service_healthy }
      postgres: { condition: service_healthy }

  consumer:            # placeholder — ჩვენი რეალური consumer სერვისი (ცალკე რეპო)
    image: ${CONSUMER_IMAGE:-alpine:3.20}
    command: ["sh", "-c", "echo 'consumer placeholder' && sleep infinity"]
    environment:
      UOF_ENV: sim
      UOF_AMQP_HOST: rabbitmq
      UOF_AMQP_PORT: "5671"
      UOF_AMQP_TLS_CA: /certs/ca.pem
      UOF_ACCESS_TOKEN: ${SIM_TOKEN:-sim-token-0000000000}
      UOF_AMQP_PASSWORD: ${SIM_CONSUMER_PASSWORD:-sim}
      UOF_API_BASE_URL: http://mock-rest:8080
      UOF_NODE_ID: "1"
      DATABASE_URL: postgresql://uofsim:uofsim@postgres:5432/consumer
      REDIS_URL: redis://redis:6379/1
    ports:
      - "8000:8000"     # consumer health/metrics (placeholder)
    volumes:
      - ./config/tls:/certs:ro
    depends_on:
      - simulator
      - mock-rest

volumes:
  pgdata:
```

### 8.1 RabbitMQ `definitions.json` (შემოკლებული)

```json
{
  "vhosts": [{ "name": "/unifiedfeed/99999" }],
  "users": [
    { "name": "sim-token-0000000000", "password": "sim", "tags": "" },
    { "name": "uofsim-publisher", "password": "publisher", "tags": "" },
    { "name": "admin", "password": "admin", "tags": "administrator" }
  ],
  "permissions": [
    { "user": "sim-token-0000000000", "vhost": "/unifiedfeed/99999",
      "configure": "^amq\\.gen.*", "write": "^amq\\.gen.*", "read": "^(amq\\.gen.*|unifiedfeed)$" },
    { "user": "uofsim-publisher", "vhost": "/unifiedfeed/99999",
      "configure": "^unifiedfeed$", "write": "^unifiedfeed$", "read": "" },
    { "user": "admin", "vhost": "/unifiedfeed/99999", "configure": ".*", "write": ".*", "read": ".*" }
  ],
  "exchanges": [
    { "name": "unifiedfeed", "vhost": "/unifiedfeed/99999", "type": "topic",
      "durable": true, "auto_delete": false, "internal": false, "arguments": {} }
  ]
}
```

- Consumer **თვითონ** ქმნის server-named exclusive queue-ს (`amq.gen-…`) და აკეთებს bind-ს — ისევე, როგორც რეალურ Betradar-ზე (consumer-ს exchange-ის შექმნის უფლება არ აქვს). ⚠ რეალური permission მოდელი გადასამოწმებელი.
- **ცარიელი პაროლი:** რეალურ Betradar-ზე password ცარიელია; RabbitMQ-ის internal auth backend ცარიელ პაროლს არ ღებულობს ⚠. ორი ვარიანტი: (a) dev-ში password `sim`, consumer-ის კონფიგში `UOF_AMQP_PASSWORD` (prod-ში ცარიელი); (b) `rabbitmq_auth_backend_http` → mock-rest `/internal/rabbit/user`, რომელიც token-ს ამოწმებს და ცარიელ პაროლს ღებულობს — **მაქსიმალური სიზუსტე**, S5-ში.
- TLS: `tools/gen_tls.sh` ქმნის self-signed CA-ს; consumer-ი `UOF_AMQP_TLS_CA`-ით ენდობა. Betradar-ზე — სისტემური CA.

---

## 9. გადართვა: Simulator → Betradar Integration → Replay Server

### 9.1 კონფიგურაციის ცხრილი (consumer-ის მხარე)

| პარამეტრი (env) | `sim` | `integration` | `replay` | `production` |
|---|---|---|---|---|
| `UOF_AMQP_HOST` | `rabbitmq` / `localhost` | `stgmq.betradar.com` | `replaymq.betradar.com` (ან `global.replaymq.betradar.com`) | `mq.betradar.com` (ან `global.mq.betradar.com`) |
| `UOF_AMQP_PORT` | `5671` | `5671` | `5671` | `5671` |
| `UOF_AMQP_TLS_CA` | `config/tls/ca.pem` | system CA | system CA | system CA |
| `UOF_AMQP_VHOST` | `/unifiedfeed/99999` | `whoami.xml` → `virtual_host` | `whoami.xml`-დან | `whoami.xml`-დან |
| `UOF_ACCESS_TOKEN` (AMQP username + REST `x-access-token`) | `sim-token-…` | Integration token (Betradar-ისგან) | იგივე token ⚠ | Production token |
| `UOF_AMQP_PASSWORD` | `sim` (ან ცარიელი http-auth-ით) | ცარიელი | ცარიელი | ცარიელი |
| `UOF_EXCHANGE` | `unifiedfeed` | `unifiedfeed` | `unifiedfeed` | `unifiedfeed` |
| `UOF_API_BASE_URL` | `http://mock-rest:8080` | `https://stgapi.betradar.com` | `https://stgapi.betradar.com` (replay endpoints: `/v1/replay/...`) | `https://api.betradar.com` (ან `global.api.betradar.com`) |
| `UOF_NODE_ID` | `1` | `1` | `1` | instance-ზე უნიკალური |
| `UOF_PRODUCERS` | `1,3` | ანგარიშზე ჩართული (`producers.xml`) | `1,3` | ანგარიშზე ჩართული |
| `UOF_RECOVERY_ENABLED` | `true` | `true` | `false` ⚠ (replay-ზე recovery არ გამოიყენება) | `true` |
| `UOF_MAX_INACTIVITY_S` | `20` | `20` | `20` | `20` |
| `UOF_DB_SCHEMA` | `uof_sim` | `uof_int` | `uof_replay` | `uof_prod` |

**წესები, რომ გადართვა მხოლოდ კონფიგი იყოს:**
1. consumer-ი `bookmaker_id`/`vhost`-ს **ყოველთვის** `GET /v1/users/whoami.xml`-დან იღებს (სიმულატორიც აბრუნებს).
2. არავითარი hardcoded host/port/path-prefix; producer-ების სია `producers.xml`-დან.
3. სიმულატორის URN-ები `sr:match:9xxxxxxxx` დიაპაზონშია, მაგრამ მონაცემები ცალკე DB schema-ში ინახება — გადართვისას sim-მონაცემები რეალურს არ ერევა.
4. market descriptions-ის cache ინვალიდირდება გარემოს გადართვისას (sim subset ≠ რეალური markets.xml).
5. Contract ტესტები: Integration-ზე წვდომის მიღებისთანავე `tools/capture_real.py` ჩაწერს რეალურ sample-ებს → შედარება სიმულატორის გამონატანთან (XSD + სტრუქტურული diff) → სიმულატორის ⚠ პუნქტების დახურვა.

### 9.2 Betradar Replay Server-ზე გადასვლა

Replay Server — Betradar-ის სერვისი, რომელიც წარსულ მატჩებს/სცენარებს უკრავს ჩვენს ექსკლუზიურ replay feed-ში (საჭიროებს Betradar-ის ანგარიშს/token-ს).

| ოპერაცია | Betradar endpoint (REST host: `stgapi.betradar.com`) | ჩვენი სიმულატორი (mock-rest) |
|---|---|---|
| რიგის ნახვა | `GET /v1/replay/` ⚠ | ✅ |
| event-ის დამატება | `PUT /v1/replay/events/{event_urn}` | ✅ (დამატებს Recorder-ის ჩანაწერს ან სცენარს) |
| event-ის წაშლა | `DELETE /v1/replay/events/{event_urn}` ⚠ | ✅ |
| დაკვრა | `POST /v1/replay/play?speed=10&max_delay=10000&use_replay_timestamp=…&node_id=…&product_id=…&run_parallel=…` (speed default 10x, max_delay default 10s) | ✅ |
| სცენარის დაკვრა | `POST /v1/replay/scenario/play/{scenario_id}?speed&max_delay&use_replay_timestamp` | ✅ (`scenarios/*.yaml`-ის id) |
| სცენარების სია | `GET /v1/replay/scenario` ⚠ | ✅ |
| გაჩერება | `POST /v1/replay/stop` | ✅ |
| გასუფთავება | `POST /v1/replay/reset` | ✅ |
| სტატუსი | `GET /v1/replay/status` ⚠ | ✅ |

- Replay-ზე alive მოდის producer 1 და 3-ისთვის ყოველ 10 წამში **მიმდინარე დროში** (დოკ.) — სიმულატორიც ასე იქცევა.
- ჩვენი CLI: `uofsim replay add sr:match:…`, `uofsim replay play --speed 20` — ერთნაირად მუშაობს ორივე backend-ზე (`--target sim|betradar`), რათა QA-ს სკრიპტები ორივეზე გაეშვას.

---

## 10. Milestone-ები (S1..S5)

| # | Milestone | შედეგი (Definition of Done) | შეფასება (person-days) |
|---|---|---|---|
| **S1** | Infra + L1 | docker-compose (RabbitMQ TLS, PG, Redis); definitions.json; XSD-ების fetch; routing key builder + unit ტესტები; L1 replayer (JSONL → AMQP); alive loop; mock-rest: `whoami.xml`, `producers.xml`, სტატიკური `markets.xml`; consumer-ს შეუძლია დაკავშირება და შეტყობინებების მიღება | **5–6** |
| **S2** | L2 Scenario engine | YAML DSL (pydantic) + loader; SimClock; Message Builder ყველა ტიპზე + XSD validation; Control API + CLI (start, goal, card, bet_stop, suspend, settle, rollback, cancel); Recorder (PG+JSONL); golden-file ტესტები; mock-rest: fixture/summary/schedules (live/pre/date), fixtures/changes | **8–10** |
| **S3** | Producers & Recovery | Producer manager (UP/DOWN_SILENT/UNSUBSCRIBED), queue_watch → `subscribed=0`; recovery (after/full), event odds recovery, stateful messages recovery, request_id/node_id routing, snapshot_complete; rate limit/403/429; Redis command stream; e2e ტესტი C04–C08 | **6–7** |
| **S4** | L3 Match + Odds engine | ფეხბურთის state machine + Poisson მოვლენები; odds engine (1,10,11,16,18,29,41) + მარჟა (power/proportional) + ladder; market status transitions (handover −2); settlement (void_factor 0.5/1, dead heat test market), certainty 1→2; მრავალი პარალელური მატჩი; variant market + unknown market | **10–12** |
| **S5** | Chaos, Load, Replay API, Hardening | Chaos middleware სრულად (§4.3); load generator + პროფილები (§7.1) + Grafana/Prometheus მეტრიკები; `/v1/replay/*` თავსებადი API; `rabbitmq_auth_backend_http` (ცარიელი პაროლი); გადართვის runbook (§9); CI pipeline; ⚠ პუნქტების დახურვა რეალურ sample-ებზე (როგორც კი წვდომა გვექნება) | **7–9** |
| | **სულ** | | **≈ 36–44 person-day** (1 senior + 1 mid ≈ 4–5 კალენდარული კვირა) |

### 10.1 რისკები

| რისკი | გავლენა | შერბილება |
|---|---|---|
| სიმულატორი „ცრუ თავდაჯერებას" იძლევა (რეალური feed განსხვავდება) | მაღალი | ⚠ სია, contract ტესტები რეალურ sample-ებზე, ადრეული Integration წვდომის მოთხოვნა Betradar-თან (trial ⚠) |
| markets.xml ID-ები/outcome ID-ები არ ემთხვევა | საშუალო | consumer-ი ID-ებს არ hardcode-ავს; ყველაფერი descriptions-დან |
| Python publisher-ის throughput load-ისთვის არ კმარა | საშუალო | multiprocess, pre-built bodies; საჭიროებისას Go load generator |
| ლიცენზია: XSD/descriptions-ის გამოყენება | დაბალი/საშუალო | XSD-ები ღია SDK რეპოებიდან (ლიცენზიის შემოწმება ⚠); descriptions — საკუთარი subset |

---

## დანართი A — Mock REST endpoint-ების სრული სია

| Method | Path | სიმულატორის ქცევა | შენიშვნა |
|---|---|---|---|
| GET | `/v1/users/whoami.xml` | `<bookmaker_details response_code="OK" bookmaker_id="99999" virtual_host="/unifiedfeed/99999" expire_at="…"/>` | ⚠ ატრიბუტების სრული სია |
| GET | `/v1/descriptions/producers.xml` | producers 1, 3 (+6) | |
| GET | `/v1/descriptions/en/markets.xml?include_mappings=true` | `data/static/markets.en.xml` (+ runtime-ში დამატებული) | `UnifiedFeedDescriptions.xsd` |
| GET | `/v1/descriptions/en/markets/{id}/variants/{variant}.xml` | single variant market | ⚠ path |
| GET | `/v1/descriptions/en/variants.xml` | variant descriptions | ⚠ |
| GET | `/v1/sports/en/sports.xml` | სპორტები | |
| GET | `/v1/sports/en/sport_events/{urn}/fixture.xml` | Redis/PG-დან | |
| GET | `/v1/sports/en/sport_events/{urn}/summary.xml` | live state Redis-დან; REST-ში status სტრიქონია (`live`, `closed`), AMQP-ში რიცხვი | ⚠ namespace `http://schemas.sportradar.com/sportsapi/v1/unified` |
| GET | `/v1/sports/en/schedules/live/schedule.xml` | მიმდინარე live event-ები | |
| GET | `/v1/sports/en/schedules/pre/schedule.xml?start=0&limit=1000` | მომავალი event-ები | ⚠ |
| GET | `/v1/sports/en/schedules/{YYYY-MM-DD}/schedule.xml` | დღის კალენდარი | |
| GET | `/v1/sports/en/fixtures/changes.xml` | ბოლო 24 სთ-ის fixture ცვლილებები | ⚠ |
| POST | `/v1/{product}/recovery/initiate_request` | §5.5 | |
| POST | `/v1/{product}/odds/events/{urn}/initiate_request` | §5.5 | |
| POST | `/v1/{product}/stateful_messages/events/{urn}/initiate_request` | Recorder-დან | ⚠ |
| * | `/v1/replay/*` | §9.2 | |
| — | არასწორი/გამოტოვებული token | `403` + `<response response_code="FORBIDDEN">` | ⚠ 401 vs 403 |

## დანართი B — Enum-ები (სიმულატორში გამოყენებული)

| Enum | მნიშვნელობები |
|---|---|
| Market status | `1` active, `-1` suspended, `0` inactive (deactivated), `-2` handed over, `-3` settled, `-4` cancelled |
| Event status (AMQP) | `0` not started, `1` live, `2` suspended, `3` ended, `4` closed, `5` cancelled, `6` delayed, `7` interrupted, `8` postponed, `9` abandoned |
| Football match_status | `0` not started, `6` 1st half, `31` halftime, `7` 2nd half, `100` ended (+ extra time/penalties ⚠) |
| fixture_change change_type | `1` NEW, `2` DATETIME, `3` CANCELLED, `4` FORMAT, `5` COVERAGE, `6` PITCHER (XSD) |
| bet_settlement result | `0` lose, `1` win; `void_factor` ∈ {0.5, 1}; `dead_heat_factor` (double) |
| certainty | `1` live scouted, `2` confirmed ⚠ |
| Football markets (sim subset) | `1` 1x2 (1/2/3), `10` double chance (9/10/11), `11` draw no bet (4/5), `16` handicap (1714/1715), `18` total (12/13), `29` BTTS (74/76), `41` correct score ⚠ — **ყველა ID ⚠ გადასამოწმებელი** რეალურ `markets.xml`-თან |

## დანართი C — წყაროები

- Sportradar UOF docs: https://docs.sportradar.com/uof (AMQP topic filtering, Replay Server, Recovery using API, Environments) — ⚠ ჩვენი გარემოდან ზოგი გვერდი ვერ ჩამოიტვირთა; მონაცემები აღებულია საძიებო snippet-ებიდან.
- Sportradar Java SDK: https://github.com/sportradar/UnifiedOddsSdkJava — `xsd/messages/UnifiedFeed.xsd`, `xsd/UnifiedFeedDescriptions.xsd`, `EnvironmentManager.java` (hosts), `ConfigLimit.java` (inactivity 20s, recovery 1200s, 30s interval).
- go-uof-sdk (minus5): https://github.com/minus5/go-uof-sdk — routing key პარსინგი/ტესტები, replay endpoints, producers ცხრილი, sample XML-ები.
- Betradar Unified Odds Developer Integration PDF: https://iodocs.betradar.com/unifiedsdk/Betradar_Unified-Odds_Developer_Integration.pdf
