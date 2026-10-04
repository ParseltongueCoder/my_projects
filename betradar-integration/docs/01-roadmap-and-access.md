# 01 — Roadmap, წვდომა, უფასო რესურსები და ბიზნეს/იურიდიული გეგმა

> **სტატუსი:** სამუშაო დრაფტი v0.1 · **თარიღი:** 2026-10-02 · **ქალაქი:** თბილისი
> **აუდიტორია:** დამფუძნებლები, ტექნიკური გუნდი, პოტენციური ინვესტორები/მრჩევლები
> **ნიშნები:** `⚠ გადასამოწმებელი` — ფაქტი ვერ გადამოწმდა პირველწყაროდან (ოფიციალური საიტი/დოკუმენტი) ან სწრაფად იცვლება. ასეთი ფაქტი გადაწყვეტილების მიღებამდე უნდა დაზუსტდეს.
>
> **მნიშვნელოვანი შენიშვნა კვლევის მეთოდზე:** `docs.sportradar.com`, `iodocs.betradar.com`, `rsig.ge`, `legal500.com` და სხვა ზოგიერთი საიტი კვლევისას პირდაპირ ვერ გაიხსნა (ქსელის შეზღუდვა), ამიტომ მათი შინაარსი გადამოწმდა ძიების შედეგების ამონარიდებით. Sportradar-ის ოფიციალური SDK-ების რეპოზიტორიები (Java v4.12.0, .NET v3.12.0, სექტემბერი 2026) კი პირდაპირ ჩამოიტვირთა და დათვალიერდა.

---

## სარჩევი

1. [Executive summary](#1-executive-summary)
2. [ფაზური Roadmap](#2-ფაზური-roadmap)
3. [გუნდი და როლები (RACI-lite)](#3-გუნდი-და-როლები-raci-lite)
4. [როგორ მივუდგეთ Sportradar-ს — ნაბიჯ-ნაბიჯ](#4-როგორ-მივუდგეთ-sportradar-ს--ნაბიჯ-ნაბიჯ)
5. [უფასო რესურსების ცხრილი](#5-უფასო-რესურსების-ცხრილი)
6. [ალტერნატიული / შემდეგი პროვაიდერები](#6-ალტერნატიული--შემდეგი-პროვაიდერები)
7. [იურიდიული და ლიცენზირების checklist](#7-იურიდიული-და-ლიცენზირების-checklist)
8. [დაფინანსება და კრედიტები](#8-დაფინანსება-და-კრედიტები)
9. [რისკების რეესტრი](#9-რისკების-რეესტრი)
10. [ბიუჯეტი — პირველი 6 თვე (lean)](#10-ბიუჯეტი--პირველი-6-თვე-lean)
11. [წყაროები](#11-წყაროები)

---

## 1. Executive summary

**მიზანი.** შევქმნათ თბილისში დაფუძნებული B2B iGaming კომპანია, რომელიც სპორტული ფსონების ოპერატორებს (sportsbook-ებს) აწვდის: ფიდების ინტეგრაციას (პირველი — **Betradar/Sportradar Unified Odds Feed, UOF**), ნორმალიზებულ (canonical) მონაცემთა მაღაზიას, ადმინ-პანელს, მონიტორინგს, მოგვიანებით — სტრიმებს, ვიჯეტებს და მცირე „add-on" ფიჩერებს. პირველ ეტაპზე საჭიროა **ნულოვანი ან მინიმალური ხარჯით** დაწყება.

**მთავარი პრობლემა.** Sportradar-ის ბეთინგ-მონაცემები (UOF) არის **sales-gated** — წვდომა (access token, bookmaker ID) გაიცემა მხოლოდ კომერციული მოლაპარაკების შემდეგ; Sportradar იყენებს მკაცრ KYC-ს და ძირითადად ლიცენზირებულ ოპერატორებთან/მიმწოდებლებთან მუშაობს. ე.ი. კონტრაქტამდე „ნამდვილ" ფიდზე ვერ ვიმუშავებთ.

**მიდგომა: „simulator-first, contract-second".**

1. **Simulator-first.** ვაშენებთ UOF-თან ფორმატით თავსებად **ფიდის სიმულატორს** (AMQP/RabbitMQ + XML შეტყობინებები `odds_change`, `bet_stop`, `bet_settlement`, `bet_cancel`, `fixture_change`, `alive`, `snapshot_complete` და REST endpoint-ები), რომელიც დაფუძნებულია **ოფიციალურ XSD სქემებზე** (ისინი საჯაროდ დევს Sportradar-ის SDK რეპოზიტორიებში — `UnifiedFeed.xsd`, `UnifiedFeedDescriptions.xsd`, `UnifiedFeedResponse.xsd` და replay-ის XSD-ები). მასზე ვაშენებთ UOF adapter-ს, canonical store-ს, ადმინს და მონიტორინგს.
2. **Contract-second.** როცა გვაქვს მუშა დემო (end-to-end: „ფიდი → ადაპტერი → canonical store → admin/API → მონიტორინგი"), მივდივართ Sportradar-თან და ვითხოვთ **Integration environment**-ზე წვდომას. ამ დროს ჩვენ უკვე „სერიოზული პარტნიორი" ვართ და არა „იდეა".
3. **Provider-agnostic არქიტექტურა.** Canonical მოდელი თავიდანვე დაპროექტდება ისე, რომ მეორე პროვაიდერი (LSports / Genius / TXODDS / OddsMatrix) დაემატოს ადაპტერის დონეზე.

**სავარაუდო ვადები (თუ დავიწყებთ 2026-10-05-ს):**

| ფაზა | კვირები | კალენდარი (სავარაუდო) | მთავარი შედეგი |
|---|---|---|---|
| 0 — სწავლა | 0–2 | ოქტ 5 – ოქტ 19, 2026 | XSD-ები, დოკუმენტაცია, SDK ნიმუშები, ADR-ები |
| 1 — სიმულატორი + ბირთვი | 2–8 | ოქტ 19 – ნოე 30 | E2E დემო სიმულატორზე |
| 2 — Betradar Integration | 8–14 | დეკ 1 – იან 11, 2027 | ნამდვილ integration env-ზე მუშაობა, Replay ტესტები |
| 3 — პილოტი და გაფართოება | 14+ | იანვარი 2027 → | პირველი ოპერატორი, მე-2 პროვაიდერი, სტრიმები/ვიჯეტები |

> **რეალისტური შენიშვნა:** ფაზა 2-ის დაწყება დამოკიდებულია Sportradar-ის პასუხზე, რასაც შეიძლება კვირები/თვეები დასჭირდეს. ამიტომ Sportradar-თან **პირველი კონტაქტი უნდა დაიწყოს ფაზა 1-ის შუაში (კვირა ~5)** და არა ფაზა 2-ის დასაწყისში.

---

## 2. ფაზური Roadmap

### ფაზა 0 — სწავლა, დოკუმენტაცია, XSD, SDK ნიმუშები (კვირა 0–2)

**Owner:** Tech Lead (A/R), ყველა დეველოპერი (R)

**მიზნები**
- UOF-ის კონცეფციების სრული გაგება: producers (LO/Pre-match/Ctrl და ა.შ.), `alive` / producer-down ლოგიკა, recovery (`initiate_request`, `snapshot_complete`), routing keys, `node_id`, market descriptions & variants, specifiers, outcome ID-ები, `bet_stop` / `betting_status`, settlement/rollback, `fixture_change`, URN-ები (`sr:match:…`, `sr:competitor:…`).
- ოფიციალური XSD-ების მოპოვება და ჩვენი ტიპების (code-gen) გენერაცია.
- SDK-ების (Java / .NET) არქიტექტურის შესწავლა: როგორ აკეთებენ recovery-ს, caching-ს, message dispatch-ს.
- Environments-ის გაგება: Integration (`stgmq.betradar.com`, `stgapi.betradar.com`), Production (`mq.betradar.com`), Replay (`replaymq.betradar.com`) და მათი `global.*` ვარიანტები — ეს ჰოსტები პირდაპირ ჩანს SDK-ის კოდში (`Environment` enum: `Integration`, `Production`, `Replay`, `GlobalIntegration`, `GlobalProduction`, `GlobalReplay`, `Custom`).

**Deliverables**
- `docs/uof-concepts.md` — UOF ცნებების ლექსიკონი (ქართულად).
- `schemas/` — XSD ფაილები + გენერირებული ტიპები (**მხოლოდ შიდა გამოყენებისთვის**, იხ. ლიცენზიის შენიშვნა ქვემოთ).
- ADR-001: ენა/სტეკი (მაგ. Go/Java/.NET/TS), ADR-002: message broker (RabbitMQ), ADR-003: canonical store (PostgreSQL + Redis/Kafka ან NATS).
- სიმულატორის სცენარების სია (pre-match → live → bet_stop → settlement → rollback, producer down/up, recovery).

**Definition of Done (exit criteria)**
- [ ] გუნდის თითოეულ წევრს შეუძლია ახსნას recovery-ის ნაკადი და `bet_stop`-ის სემანტიკა.
- [ ] XSD-დან გენერირებული ტიპები კომპილირდება; SDK-ის ნიმუშ-XML-ები (`odds_change.xml`, `bet_settlement.xml`, `bet_stop.xml`, `bet_cancel.xml`, `fixture_change.xml`, `alive.xml` …) წარმატებით პარსირდება ჩვენი ტიპებით.
- [ ] დამტკიცებულია ADR-001..003.
- [ ] დამტკიცებულია ფაზა 1-ის backlog (ესტიმაციით).

> **⚠ ლიცენზიის მნიშვნელოვანი აღმოჩენა.** `sportradar/UnifiedOddsSdkJava` და `sportradar/UnifiedOddsSdkNetCore` **არ არის open-source** (არც BSD, არც MIT — ზოგი მესამე მხარის საიტი შეცდომით BSD-3-ს წერს). ორივე რეპოს `LICENSE.md` არის Sportradar-ის საკუთარი **„SDK License Agreement"** (შვეიცარიის სამართალი, St. Gallen): ლიცენზია არის შეზღუდული, არაექსკლუზიური, royalty-free; **აკრძალულია** SDK-ის ასლის გადაღება (backup-ის გარდა), მოდიფიკაცია, რედისტრიბუცია, derivative works, SDK-ის „ფრაგმენტაცია" (მის საფუძველზე სხვა SDK-ის შექმნა). შესაბამისად:
> - კოდი და ნიმუშ-XML-ები გამოიყენეთ **სწავლისთვის და შიდა ტესტებისთვის**; ნუ ჩადებთ მათ ჩვენს რეპოში/პროდუქტში და ნუ გაავრცელებთ.
> - სიმულატორის fixture-ები **ჩვენ თვითონ დავაგენერიროთ** XSD-ზე დაყრდნობით (სინთეტიკური ID-ებით/სახელებით).
> - XSD-ების გამოყენების უფლება ინტეგრაციისთვის ლოგიკურია, მაგრამ მათი საჯარო რედისტრიბუცია — `⚠ გადასამოწმებელი` (ჰკითხეთ Sportradar-ს, კითხვა #14).
> - საზოგადოებრივი (community) SDK-ები: `minus5/go-uof-sdk` (MIT, 2019; ბოლო commit 2023 — ნაკლებად აქტიური), `efcasado/betradar_uof_sdk` (Elixir). ისინიც შეიცავს testdata XML-ებს; MIT ლიცენზია კოდს ფარავს, თუმცა ნიმუშ-მონაცემების წარმომავლობა `⚠ გადასამოწმებელი`.

---

### ფაზა 1 — სიმულატორი + UOF adapter + canonical store + საბაზისო admin + მონიტორინგი (კვირა 2–8)

**Owner:** Tech Lead (A), Backend Dev ×1–2 (R), Frontend/Fullstack (R — admin), DevOps (R — მონიტორინგი), CEO/BD (I; კვირა ~5-დან R — Sportradar outreach)

**მიზნები**
- **Feed Simulator** (დეტალები — მეზობელი დოკუმენტი, სიმულატორის გუნდი): RabbitMQ exchange `unifiedfeed`, routing keys UOF-ის ფორმატით, XML შეტყობინებები XSD-ის მიხედვით, REST mock (`/v1/sports/…/fixture.xml`, `/v1/descriptions/…/markets.xml`, `/v1/users/whoami.xml`, recovery endpoint-ები), producer-down/alive სცენარები, „chaos" რეჟიმი (დაგვიანება, დუბლიკატები, out-of-order).
- **UOF Adapter:** AMQP consumer, XSD-ვალიდაცია, დედუპლიკაცია, sequence/timestamp მართვა, recovery state machine, market/outcome name templating (specifiers + variant descriptions), mapping → canonical.
- **Canonical store** (data model — მეზობელი დოკუმენტი): events, markets, outcomes, odds history, statuses, settlements; provider-agnostic ID-ები + provider mapping ცხრილები.
- **Basic Admin:** ივენთების სია/ფილტრი, მარკეტების ხედი, ხელით suspend/unsuspend, producer status პანელი, recovery-ის ღილაკი.
- **Monitoring:** Prometheus/Grafana (ან Grafana Cloud free tier), მეტრიკები: message lag (feed `timestamp` vs მიღების დრო), msgs/sec, producer up/down, recovery ხანგრძლივობა, XSD-ვალიდაციის შეცდომები, DLQ ზომა; alert-ები (Telegram/Slack).
- **Sportradar outreach-ის დაწყება** (კვირა ~5): იხ. სექცია 4.

**Deliverables**
- სიმულატორი Docker-ში (`docker compose up`) + 5+ სცენარი.
- Adapter + canonical store + migrations.
- Admin MVP (web).
- Grafana dashboard + alert rules.
- **3–5 წუთიანი დემო-ვიდეო** და ერთგვერდიანი ტექნიკური აღწერა (Sportradar-ისა და ოპერატორებისთვის).
- Load test-ის შედეგი (მაგ. 2,000–5,000 msg/s სიმულატორიდან) — `⚠ ზუსტი target-ი შეთანხმდეს არქიტექტურის დოკუმენტთან`.

**Definition of Done**
- [ ] E2E: სიმულატორის სცენარი „pre-match → live → bet_stop → settlement → rollback" სრულად აისახება canonical store-სა და admin-ში.
- [ ] Producer down → ჩვენი სისტემა ავტომატურად აჩერებს (suspend) შესაბამის მარკეტებს და recovery-ის შემდეგ აღადგენს მდგომარეობას — დადასტურებულია ავტოტესტით.
- [ ] ყველა სიმულატორის შეტყობინება გადის XSD-ვალიდაციას; ვალიდაციის შეცდომები ჩანს მონიტორინგში.
- [ ] p95 შიდა latency (მიღება → canonical commit) < 100 ms სიმულატორის ნორმალურ დატვირთვაზე `⚠ target შესათანხმებელი`.
- [ ] Sportradar-ს გაეგზავნა პირველი მიმართვა და დაინიშნა/მოთხოვნილია discovery call.

---

### ფაზა 2 — Betradar Integration environment, Replay Server-ით „სერტიფიკაციის მსგავსი" ტესტირება, hardening (კვირა 8–14)

**Owner:** Tech Lead (A/R), Backend (R), DevOps/SRE (R), QA (R), CEO/BD (R — კონტრაქტი/კომუნიკაცია)

**რა ვიცით Integration env-ზე (წყარო: Sportradar-ის საჯარო დოკუმენტაციის ამონარიდები და SDK კოდი):**
- Integration env „ახლოს არის production-თან", მუშაობს **24/5** — შაბათ-კვირას კავშირი წყდება და ახალი სესია ვერ იხსნება.
- Access token-ები იქმნება Integration Portal-ში (`stgufadmin.betradar.com`); REST-ზე token იგზავნება header-ით `x-access-token: <token>`.
- ჰოსტები: messaging — `stgmq.betradar.com`, API — `stgapi.betradar.com`; admin — `integration.portal.betradar.com`.
- **Replay Server:** ცალკე message queue (`replaymq.betradar.com`), იმართება REST API-ით (SDK-ში — `ReplayManager`). შეიძლება ნებისმიერი **48 საათზე ძველი** ივენთის ხელახლა „დაკვრა"; ივენთები ინახება დიდხანს (Betradar იტოვებს უფლებას 2 წელზე ძველები წაშალოს). ნაგულისხმევი სიჩქარე **10x**, `max_delay` ნაგულისხმევად **10 წმ**; არსებობს **3 წინასწარ მომზადებული სცენარი** (stress test-ისთვის). Replay ხელმისაწვდომია **შაბათ-კვირასაც**. `⚠ გადასამოწმებელი` — ზუსტი რიცხვები docs.sportradar.com-ზე ხელახლა დაადასტურეთ.
- ინტეგრაციის პროცესი: Sportradar-ის **Integration Manager**, kickoff call, გეგმა **~4 სპრინტი / 8–10 კვირა**; production-ზე გადასვლამდე — **integration review** ან სრული **certification** (Betradar-ის მიხედვით — უფასოდ). Production წვდომა მხოლოდ **whitelisted IP**-ებიდან.

**მიზნები**
- Adapter-ის გადართვა სიმულატორიდან Integration env-ზე **კონფიგურაციის ცვლილებით** (არა კოდის).
- **„Divergence report":** რა განსხვავდება ნამდვილ ფიდსა და ჩვენს სიმულატორს შორის (ველები, სიხშირე, ზომები, edge case-ები) → სიმულატორის გასწორება.
- Replay Server-ით რეგრესიული ტესტ-სუიტი: 20–50 ცნობილი მატჩი (ფეხბურთი, ჩოგბურთი, კალათბურთი, ბოლო წუთის გოლი, VAR-ით გაუქმებული გოლი, abandoned/postponed, rollback_bet_settlement).
- Hardening: idempotency, backpressure, DLQ, graceful restart + recovery, multi-node (`node_id`), secret-ების მართვა, IP whitelist-ისთვის სტატიკური egress IP.
- Security baseline (OWASP ASVS L1), ლოგების/აუდიტის მოთხოვნები (ლიცენზირებისთვის მომავალში).

**Deliverables**
- Integration env-ზე 72+ საათიანი (სამუშაო დღეებში) სტაბილური გაშვება მონიტორინგით.
- Replay რეგრესიის CI job (ღამის) + რეპორტი.
- „Certification readiness" checklist (Sportradar-ის integration review-სთვის).
- Runbook-ები: producer down, recovery storm, token rotation, broker outage.

**Definition of Done**
- [ ] 5 სამუშაო დღე ზედიზედ Integration env-ზე ხელით ჩარევის გარეშე; ყველა recovery წარმატებული.
- [ ] Replay სუიტი: 100% settlement-ები ემთხვევა მოსალოდნელ შედეგს.
- [ ] Divergence report დახურულია (ყველა კრიტიკული სხვაობა გასწორებულია სიმულატორში).
- [ ] გავლილია Sportradar-ის integration review (ან დაგეგმილია თარიღი) — `⚠ დამოკიდებულია კონტრაქტზე`.

---

### ფაზა 3 — პირველი ოპერატორის პილოტი, მეორე პროვაიდერი, სტრიმები, ვიჯეტები (კვირა 14+)

**Owner:** CEO/BD (A — პილოტი/კონტრაქტები), Tech Lead (A — ტექნიკა), Compliance advisor (R — ლიცენზიები), Product (R)

**მიზნები**
- **პირველი ოპერატორის პილოტი** (სასურველია ლიცენზირებული ოპერატორი საქართველოში ან რეგიონში, რომელსაც **საკუთარი Sportradar კონტრაქტი აქვს** — ასე ჩვენ ვართ „ტექნოლოგიური მიმწოდებელი" და მონაცემების რედისტრიბუციის პრობლემა მცირდება; იხ. სექცია 7).
- **მეორე პროვაიდერი** (LSports / TXODDS / Genius / OddsMatrix — იხ. სექცია 6) → multi-provider mapping (event matching, market mapping, provider priority/failover).
- **სტრიმები:** Sportradar-ის streaming (ან Genius/IMG-ის შემკვიდრე) — მხოლოდ ცალკე უფლებებით; ჩვენი მხარე — player integration, geo-blocking, entitlement (ფსონის/ბალანსის პირობა).
- **ვიჯეტები:** Live Match Tracker (Sportradar Widgets) ინტეგრაცია ოპერატორის ლიცენზიით; საკუთარი მარტივი ვიჯეტები canonical მონაცემებზე.
- მცირე ფიჩერები: odds-ის მარჟის მართვა, market templates, alert-ები ტრეიდერებისთვის, SLA რეპორტები.

**Deliverables**
- პილოტის ხელშეკრულება (MSA + SLA + DPA) და go-live.
- მეორე პროვაიდერის adapter + mapping UI.
- ლიცენზირების გეგმა (Georgia/MGA/Curaçao — გადაწყვეტილება იურისტთან).

**Definition of Done**
- [ ] ოპერატორი production-ში იღებს ჩვენი პლატფორმიდან მონაცემებს ≥ 30 დღე, SLA ≥ 99.5% `⚠ target`.
- [ ] მეორე პროვაიდერის ≥ 80% ტოპ-ლიგების ივენთები ავტომატურად დაკავშირებულია (matched) Sportradar-ის ივენთებთან.
- [ ] ყველა რედისტრიბუციის/ლიცენზიის კითხვა წერილობით დახურულია.

---

## 3. გუნდი და როლები (RACI-lite)

### ვის ავიყვანოთ პირველ რიგში (პრიორიტეტით)

| # | როლი | დატვირთვა | რატომ პირველი | შენიშვნა |
|---|---|---|---|---|
| 1 | **Tech Lead / Senior Backend (feed integration)** | full-time (ხშირად co-founder) | UOF/AMQP/recovery — მთელი პროდუქტის ბირთვი | სასურველია betting/feeds გამოცდილება (თბილისში არის Betsson/Adjarabet/Crystalbet/Europebet-ის ეკოსისტემის სპეციალისტები) |
| 2 | **CEO / BD (co-founder)** | full-time | Sportradar-თან და ოპერატორებთან მოლაპარაკება, ფონდები | iGaming-ის კონტაქტები კრიტიკულია |
| 3 | **Backend Developer** | full-time | adapter, canonical store, API | |
| 4 | **Fullstack/Frontend (Admin)** | part-time → full | admin, monitoring UI | |
| 5 | **DevOps/SRE** | part-time / freelance | infra, CI/CD, მონიტორინგი, IP whitelist | ფაზა 2-ში მნიშვნელოვანია |
| 6 | **QA (automation)** | part-time ფაზა 2-დან | Replay რეგრესია | შეიძლება Backend-მა შეითავსოს |
| 7 | **Compliance / Legal advisor** | outsourced, საათობრივი | ლიცენზიები, კონტრაქტები, AML | არ არის full-time საჭირო 6 თვემდე |
| 8 | **Sportsbook domain expert / trader** | advisor (equity/საათობრივი) | მარკეტები, settlement-ის edge case-ები | |

### RACI-lite (R = აკეთებს, A = პასუხისმგებელია, C = კონსულტაცია, I = ინფორმირებული)

| აქტივობა | CEO/BD | Tech Lead | Backend | Frontend | DevOps | QA | Legal |
|---|---|---|---|---|---|---|---|
| ფაზა 0: სწავლა, ADR-ები | I | **A/R** | R | R | C | – | – |
| სიმულატორი | I | A | **R** | – | C | C | – |
| UOF Adapter + canonical store | I | **A** | **R** | – | C | C | – |
| Admin | C | A | C | **R** | – | C | – |
| მონიტორინგი/alerting | I | A | C | C | **R** | C | – |
| Sportradar outreach & კონტრაქტი | **A/R** | C (ტექ. კითხვები, დემო) | – | – | – | – | C |
| Integration env + Replay ტესტები | I | **A** | R | – | R | **R** | – |
| ლიცენზირება / კომპანიის სტრუქტურა | **A** | I | – | – | – | – | **R** |
| ოპერატორის პილოტი | **A** | R | R | R | R | R | C |
| მე-2 პროვაიდერი | A (კონტრაქტი) | **A** (ტექ.) | R | C | C | R | C |
| გრანტები/კრედიტები | **A/R** | C | – | – | – | – | C |

---

## 4. როგორ მივუდგეთ Sportradar-ს — ნაბიჯ-ნაბიჯ

### 4.1. რა უნდა ვიცოდეთ წინასწარ

- **UOF-ზე self-serve trial არ არსებობს.** Developer Portal-ის (developer.sportradar.com) 30-დღიანი trial-ები ეხება **Sports Data API-ებს** (media/non-betting, მაგ. Soccer, Odds Comparison), და არა UOF-ს. ზოგი პროდუქტის trial-ი ვერ გაიცემა ავტომატურად — საჭიროა sales (ასეა, მაგ., Live Odds API-ზე).
- **Developer Portal trial-ის პირობები:** 30 დღე, **1,000 მოთხოვნა** (rolling 30 დღე), **1 QPS**, თითო key — ერთი სპორტი/პროდუქტი; Master Terms-ის მიხედვით „Free Trial" = **არაკომერციული**, მხოლოდ **შიდა ტესტირება/შეფასება**, აკრძალულია მონაცემების გამოქვეყნება/ჩვენება. ე.ი. პროტოტიპისთვის კი, პროდუქტში — არა.
- Betradar-ის საიტის მიხედვით UOF-ის ინტეგრაცია „უფასოა" (free of charge), integration review/certification — უფასო, შემდეგ account გადადის production-ზე. **მაგრამ** Integration env-ზე წვდომაც კი გაიცემა Sportradar-ის account manager-ის მეშვეობით (bookmaker ID + token) — `⚠ გადასამოწმებელი`: გასცემენ თუ არა წინასწარ (კონტრაქტამდე) platform provider-ს.
- Sportradar ხშირად მუშაობს **platform provider-ებთან** ორი მოდელით `⚠ გადასამოწმებელი`:
  - **(a) Operator-contracted / „bring your own contract"** — ოპერატორს აქვს Sportradar-თან ხელშეკრულება, ჩვენი პლატფორმა მუშაობს ოპერატორის token-ით/bookmaker ID-ით. მცირე B2B-სთვის ყველაზე რეალისტური სტარტი.
  - **(b) Reseller / distribution agreement** — ჩვენ ვყიდულობთ და ვანაწილებთ ოპერატორებზე. მოითხოვს ცალკე წერილობით უფლებას (Sportradar-ის T&C კრძალავს resell/sub-license/distribute-ს წერილობითი თანხმობის გარეშე) და, სავარაუდოდ, ჩვენს ლიცენზიას.
- Sportradar-ს აქვს Acceleradar — უფასო მონაცემების პროგრამა სპორტ-ტექ სტარტაპებისთვის (გამოცხადდა 2016-ში; < 1 წლის კომპანია, ≤ $500k დაფინანსება; ძირითადად აშშ-ის ლიგები/media). **ბეთინგ-ფიდზე არ ვრცელდება და მოქმედების სტატუსი უცნობია — `⚠ გადასამოწმებელი`**, მაგრამ ღირს კითხვა.

### 4.2. ნაბიჯები

1. **კვირა 3–4 — მომზადება**
   - კომპანიის რეგისტრაცია (შპს, საქართველო) — Sportradar-ის KYC-ს სჭირდება იურიდიული პირი.
   - **Company deck** (10–12 სლაიდი): გუნდი, პრობლემა, პროდუქტი (B2B feed integration + admin + monitoring), სამიზნე ბაზარი (CIS/კავკასია/აღმოსავლეთ ევროპა/LatAm — `⚠ დასაზუსტებელი`), ბიზნეს-მოდელი, pipeline (ოპერატორები, ვისთანაც საუბარია — LOI-ები).
   - **Use case one-pager:** რომელ პროდუქტებს ვითხოვთ (UOF pre-match + live, ფეხბურთი/ჩოგბურთი/კალათბურთი/e-sports), რა მოცულობით, რომელ ბაზრებზე, მოდელი (a) თუ (b).
   - **Technical one-pager + დემო** სიმულატორზე: E2E ვიდეო, არქიტექტურის დიაგრამა, recovery/monitoring-ის ჩვენება — აჩვენებს, რომ Integration Manager-ის დროს არ დავკარგავთ.
   - 1–2 **LOI** (Letter of Intent) ოპერატორებისგან — ყველაზე ძლიერი არგუმენტი.
2. **კვირა 5 — პირველი კონტაქტი**
   - ფორმა sportradar.com / betradar.com-ზე („Contact sales") + LinkedIn-ით რეგიონის Sales/Partner manager-ის მოძებნა (CEE/CIS რეგიონი).
   - კონფერენციები: ICE (ლონდონი/ბარსელონა), SBC Summit (ლისაბონი), SiGMA — Sportradar-ის სტენდზე შეხვედრის წინასწარ დაჯავშნა `⚠ თარიღები გადასამოწმებელი`.
3. **კვირა 6–7 — Discovery call:** დავსვათ კითხვები (4.3), ვაჩვენოთ დემო, ვითხოვოთ Integration env წვდომა (თუნდაც დროებითი, NDA-ით).
4. **კვირა 8+ — NDA → (Trial/Integration) agreement → Integration Manager kickoff.**
5. **პარალელურად:** ერთი ოპერატორი, რომელსაც უკვე აქვს Sportradar-ის კონტრაქტი, შეიძლება იყოს ჩვენი „შესასვლელი" (მოდელი a) — ოპერატორის ანგარიშზე integration token-ით ტესტი.

### 4.3. კითხვები Sportradar-ისთვის (checklist)

**წვდომა და ტესტირება**
1. შეუძლიათ თუ არა **platform provider-ს** (ჯერ ოპერატორის გარეშე) Integration environment-ზე წვდომის მიცემა? რა ვადით (კვირები/თვეები) და რა პირობებით (NDA, დეპოზიტი, LOI)?
2. Integration env-ზე რომელი **producers** და სპორტებია ხელმისაწვდომი? შეზღუდულია თუ არა message rate / ივენთების რაოდენობა?
3. **Replay Server** — შედის თუ არა integration პაკეტში? არის თუ არა ლიმიტი ერთდროულ replay-ებზე? დაემატა თუ არა ახალი სცენარები (3-ზე მეტი)?
4. რას მოიცავს **integration review** vs **certification**? ვინ ახორციელებს, რამდენი ხანი გრძელდება, ფასიანია თუ არა, რა არის test case-ების სია?

**კომერციული და ლიცენზია**
5. **Pricing მოდელები**: ფიქსირებული თვიური / GGR-ის % / per-event / per-sport / per-producer? არის თუ არა სტარტაპ/„ramp-up" ფასი პირველი 6–12 თვე?
6. **რედისტრიბუციის უფლება B2B-სთვის:** შეგვიძლია თუ არა ჩვენი პლატფორმიდან რამდენიმე ოპერატორს მივაწოდოთ odds/data? სჭირდება თუ არა ცალკე **reseller/distribution agreement**? ან თითოეულ ოპერატორს საკუთარი კონტრაქტი სჭირდება (მოდელი a)?
7. მოდელ (a)-ში: შეუძლია თუ არა ერთ ჩვენს ინსტანციას ემსახუროს რამდენიმე ოპერატორს **რამდენიმე token-ით** (multi-tenant)? `node_id`-ის/ბინდინგების რეკომენდაცია?
8. რა **ლიცენზიებს/KYC დოკუმენტებს** ითხოვენ ჩვენგან (B2B supplier license? AML პოლიტიკა? UBO)? რომელი იურისდიქციის ლიცენზია მიიჩნევა საკმარისად?
9. რომელ **ქვეყნებში/ბაზრებზე** არის შეზღუდვა (sanctioned/unregulated markets) — აისახება თუ არა კონტრაქტში geo-restrictions?
10. მონაცემების **შენახვა/არქივი**: შეგვიძლია თუ არა odds history-ს შენახვა და ანალიტიკისთვის/ML-ისთვის გამოყენება კონტრაქტის დასრულების შემდეგ?

**სტრიმები, ვიჯეტები, დამატებითი პროდუქტები**
11. **Streaming rights** (Live Channel / streaming): ვის ეკუთვნის უფლება — ოპერატორს თუ პლატფორმას? geo-blocking-ის და „bet-to-watch" მოთხოვნები? (IMG ARENA-ს პორტფოლიო 2025 წლის ნოემბრიდან Sportradar-შია — რა იცვლება?)
12. **Live Match Tracker / Widgets**: ლიცენზირება ოპერატორზე თუ პლატფორმაზე? white-label-ის/სტილიზაციის შეზღუდვები? ფასი?
13. **MTS (Managed Trading Services), Betradar Ctrl, Custom Bet / Bet Builder** — ხელმისაწვდომია თუ არა platform provider-ის მეშვეობით?

**ტექნიკური / SLA**
14. **SLA**: uptime, latency (ფიდის), support-ის საათები (24/7?), incident communication, ცვლილებების (breaking changes) შეტყობინების ვადები. შეგვიძლია თუ არა **XSD-ების** ჩართვა ჩვენს (დახურულ) კოდში და ჩვენი სიმულატორის **ჩვენება ოპერატორებისთვის**?
15. **Production access-ის** მოთხოვნები: IP whitelist-ის რაოდენობა, ცალკე token per environment, რეკომენდებული ჰოსტინგ-რეგიონი (EU) latency-სთვის, AMQP vs ახალი ინტერფეისები (არის თუ არა გეგმაში gRPC/WebSocket ალტერნატივა)?
16. არის თუ არა **პარტნიორ/მიმწოდებელთა პროგრამა** (technology partner / certified platform provider listing), რაც ოპერატორების მოზიდვაში დაგვეხმარება?
17. **ტესტირების ვადა და ხარჯი**, თუ კონტრაქტზე ხელს არ მოვაწერთ — როდის გაითიშება წვდომა?

---

## 5. უფასო რესურსების ცხრილი

> „კომერციული გამოყენება" = შეგვიძლია თუ არა გამოვიყენოთ პროდუქტში, რომელსაც ოპერატორებს მივყიდით. **არცერთი ქვემოთ ჩამოთვლილი უფასო წყარო არ იძლევა ოპერატორებზე მონაცემების რედისტრიბუციის უფლებას** — ყველა გამოიყენეთ **პროტოტიპისთვის / სიმულატორის „realism"-ისთვის / დემოსთვის**.

| რესურსი | რა არის | უფასო ლიმიტები | კომერციული გამოყენება? | ბმული |
|---|---|---|---|---|
| **Sportradar UOF docs** | UOF-ის ოფიციალური დოკუმენტაცია (environments, replay, messages, FAQ) | საჯარო | დოკუმენტაცია — მხოლოდ საცნობაროდ | https://docs.sportradar.com/uof |
| **UnifiedOddsSdkJava** (v4.12.0, 2026-09) | ოფიციალური Java SDK; შეიცავს **XSD-ებს** (`UnifiedFeed.xsd`, descriptions, response, replay, custombet) და **ნიმუშ-XML-ებს** (`odds_change`, `bet_settlement`, `bet_stop`, `bet_cancel`, `fixture_change`, `alive`, REST პასუხები) | უფასო | **არა open-source** — Sportradar SDK License: აკრძალულია მოდიფიკაცია/რედისტრიბუცია/derivative works; გამოყენება ინტეგრაციისთვის — დიახ | https://github.com/sportradar/UnifiedOddsSdkJava |
| **UnifiedOddsSdkNetCore** (v3.12.0, 2026-09) | ოფიციალური .NET SDK (netstandard2.0); ~600 XSD (`ext/`), ~200 ნიმუშ-XML (Tests) | უფასო | იგივე SDK License | https://github.com/sportradar/UnifiedOddsSdkNetCore |
| **minus5/go-uof-sdk** | community Go SDK, testdata XML | უფასო | MIT (კოდი); ნიმუშ-მონაცემების წარმომავლობა `⚠` | https://github.com/minus5/go-uof-sdk |
| **Sportradar Developer Portal trial** | Sports Data API-ები (Soccer, Odds Comparison და ა.შ.) | 30 დღე, 1,000 call, 1 QPS, key per sport | **არა** — მხოლოდ შიდა შეფასება, ჩვენება აკრძალულია | https://developer.sportradar.com |
| **The Odds API** | odds-ის აგრეგატორი (ბევრი ბუქმეიკერი, JSON) | 500 credit/თვე (credit = markets × regions; historical ×10). ფასიანი: $30/20k, $59/100k… `⚠ ფასები შეიძლება შეიცვალოს` | ნაწილობრივ — UI/dashboard-ში დიახ, **raw data-ს feed/API-ად გადაყიდვა — აკრძალულია** | https://the-odds-api.com |
| **API-Football (API-Sports)** | ფეხბურთი (+ სხვა 11 სპორტი ერთ ანგარიშზე), fixtures, live, odds | 100 req/დღე, ყველა endpoint; Pro $19/თვე 7,500/დღე | ⚠ ToS გადასამოწმებელი; რედისტრიბუცია — სავარაუდოდ აკრძალული | https://www.api-football.com |
| **football-data.org** | ფეხბურთი: fixtures/results/tables | 12 ტურნირი, 10 req/წთ | **არა** — უფასო ტიერი პირადი/არაკომერციული | https://www.football-data.org |
| **OpenLigaDB** | community/crowd-sourced, ძირითადად გერმანული ფეხბურთი | უფასო, key არ სჭირდება | ODbL (attribution + share-alike) `⚠`; ხარისხი community-ზე დამოკიდებული | https://www.openligadb.de |
| **TheSportsDB** | მეტამონაცემები, ლოგოები, ივენთები | test key `123`, ~30 req/წთ; livescore — Patreon (~$1+/თვე) `⚠`; live 5–10 წთ დაყოვნებით | ⚠ ToS გადასამოწმებელი | https://www.thesportsdb.com |
| **Sportmonks** | Football API | უფასო გეგმა: 2 ლიგა (დანიის Superliga, შოტლანდიის Premiership); odds — ფასიან გეგმებში | ფასიანზე — ToS-ის მიხედვით `⚠` | https://www.sportmonks.com/football-api/free-plan/ |
| **Betfair Exchange API** | ბირჟის odds (delayed key) | Delayed key უფასო (1–180 წმ snapshot); Live key £499; Software Vendor ლიცენზია £999 | **არა** უფასოდ — კომერციული გამოყენება Betfair-ის თანხმობით; **⚠ საქართველოდან ანგარიშის გახსნა შეიძლება შეზღუდული იყოს** | https://developer.betfair.com |
| **RabbitMQ, PostgreSQL, Redis, Prometheus, Grafana, Loki** | ინფრასტრუქტურა (OSS) | უფასო (self-host) | დიახ (MPL/PostgreSQL/BSD/Apache/AGPL — Grafana/Loki AGPL, შიდა გამოყენება OK) | — |
| **Grafana Cloud Free** | ჰოსტინგ-მონიტორინგი | ~10k series, 50GB logs `⚠ გადასამოწმებელი` | დიახ | https://grafana.com/products/cloud/ |
| **Oracle Cloud Always Free / GCP/AWS free tiers** | VM-ები dev/stage-სთვის | Oracle: ARM VM-ები `⚠`; AWS/GCP — შეზღუდული | დიახ | — |

---

## 6. ალტერნატიული / შემდეგი პროვაიდერები

| პროვაიდერი | რას გვთავაზობს | ტიპი | Trial / Sandbox | შენიშვნა ჩვენთვის |
|---|---|---|---|---|
| **Sportradar / Betradar** | UOF (pre-match + live odds), MTS, Ctrl, LMT/ვიჯეტები, streaming; 2025-11-დან + IMG ARENA-ს პორტფოლიო (~70 უფლებამფლობელი, ~38k data / ~29k streaming ივენთი წელიწადში) | ბაზრის ლიდერი | Integration env + Replay — მხოლოდ კლიენტებს/sales-ით | პირველი სამიზნე; ძვირი, მკაცრი KYC |
| **Genius Sports / Betgenius** | ოფიციალური მონაცემები (NFL, EPL და სხვ.), odds feeds, managed trading, streaming, BetBuilder | ოფიციალური მონაცემების ლიდერი | Developer Centre (developer.geniussports.com); Fixtures API-ს აქვს **UAT** გარემო მომხმარებლებისთვის; საჯარო უფასო trial — `⚠ არა` | ძლიერი მე-2 პროვაიდერი, მაგრამ enterprise ფასი |
| **LSports** | TRADE360 / OddService (100+ ბუქმეიკერის odds), Prematch/Inplay feed, JSON/XML | Mid-market | **საიტზე აცხადებენ უფასო trial-ს** და sandbox-ს | **საუკეთესო კანდიდატი მე-2 პროვაიდერად სტარტაპისთვის** — trial ხელმისაწვდომია |
| **TXODDS** | Tx FUSION (live odds, ძალიან დაბალი latency), Tx LAB (ისტორიული არქივი) | Odds/data | trial — მოთხოვნით (xml@txodds.com) | Sportmonks-ის Premium Odds-ის წყარო; ისტორიული მონაცემები ML-სთვის |
| **Kambi** | Turnkey sportsbook + **Odds Feed+** (standalone feed) | Platform + feed | საჯარო trial არა `⚠` | უფრო კონკურენტი/პარტნიორი; feed-only ვარიანტი საინტერესოა |
| **OddsMatrix (EveryMatrix)** | Sportsbook + standalone odds feed (75+ სპორტი, 23 e-sport) | Platform + feed | `⚠ არ არის საჯარო` | API-first, შეიძლება feed-ად |
| **BetConstruct** | Turnkey sportsbook/platform, live | Platform | `⚠ არ არის საჯარო` | ძირითადად კონკურენტი (რეგიონში ძლიერი) |
| **IMG ARENA** | 2025-11-დან Sportradar-ის შემადგენლობაში | — | — | ცალკე პროვაიდერად აღარ განიხილება |
| **The Odds API / OddsPapi / odds-api.io / SportsGameOdds** | odds-ის აგრეგატორები (scraped/bookmaker odds) | Developer-oriented | უფასო ტიერები | **არ არის** ოფიციალური/ლიცენზირებული ფიდი ოპერატორისთვის; მხოლოდ პროტოტიპი/ანალიტიკა |

**რეკომენდაცია:** Multi-provider ფაზისთვის პირველი კანდიდატი — **LSports** (trial-ის ხელმისაწვდომობის გამო), შემდეგ **Genius Sports** ან **TXODDS**, ბაზრის მოთხოვნიდან გამომდინარე.

---

## 7. იურიდიული და ლიცენზირების checklist

> ⚖️ **DISCLAIMER:** ეს სექცია არის **მაღალი დონის მიმოხილვა** და **არ არის იურიდიული კონსულტაცია**. რეგულაცია ხშირად იცვლება (მაგ. საქართველოში 2024–2026 წლებში რამდენიმე რეფორმა). ნებისმიერი გადაწყვეტილების წინ **აუცილებლად გაიარეთ კონსულტაცია** iGaming-ში სპეციალიზებულ იურისტთან (საქართველოში და სამიზნე იურისდიქციებში).

### 7.1. კომპანია და საბაზისო

- [ ] **შპს რეგისტრაცია** (საჯარო რეესტრი) — სწრაფი და იაფი; UBO-ს გამჭვირვალე სტრუქტურა (Sportradar-ის და ლიცენზიების KYC-სთვის).
- [ ] **ბანკი:** iGaming-თან დაკავშირებული B2B-ც შეიძლება ბანკმა მაღალი რისკის კატეგორიად ჩათვალოს — წინასწარ ჰკითხეთ (TBC/Bank of Georgia) `⚠`.
- [ ] **Virtual Zone Person სტატუსი** (IT კომპანია, 0% მოგების გადასახადი უცხოელ კლიენტებზე გაყიდულ IT სერვისებზე; დივიდენდზე 5%; ხელფასებზე 20% საშემოსავლო + საპენსიო). **⚠ გადასამოწმებელი:** ვრცელდება თუ არა გამბლინგ-ოპერატორებისთვის გაწეულ IT სერვისებზე; ქართველი კლიენტისგან შემოსავალი არ ექვემდებარება შეღავათს.
- [ ] **სტარტაპის სტატუსი** (Innovative Startup) — GITA-ს გრანტის ან ≥100k GEL ინვესტიციის შემთხვევაში `⚠`.
- [ ] **Data protection:** საქართველოს პერსონალური მონაცემების დაცვის კანონი (2024 რედაქცია) + GDPR, თუ EU ოპერატორებს ვემსახურებით (DPA-ები ოპერატორებთან). ფიდის მონაცემები პერსონალური არ არის, მაგრამ admin-ის მომხმარებლები/ლოგები — დიახ.

### 7.2. საქართველო — გამბლინგის რეგულაცია

- **რეგულატორი:** შემოსავლების სამსახური (Revenue Service), ფინანსთა სამინისტრო; კანონი „სათამაშო ბიზნესის მოწყობის შესახებ" (ლატარიების, აზარტული და სხვა მომგებიანი თამაშობების შესახებ) — ნებართვის (permit) რეჟიმი.
- **2024 რეფორმა (ძალაშია 2024-07-01):** ⚠ docs/05 §5-ის მიხედვით 25+ ასაკის მოთხოვნა 2021 წლის დეკემბრის ცვლილებით 2022-03-01-დან მოქმედებს; გადაამოწმეთ იურისტთან. ასაკი 25+, რეკლამის აკრძალვები, GGR გადასახადი 10%→15%, მოგების გატანის გადასახადი 2%→6%, ლიცენზიის გარეშე საქმიანობაზე ჯარიმა ~$30,000/დარღვევა.
- **2024-12-01:** ახალი წესები ონლაინ გამბლინგზე (ქართველ მოთამაშეებზე 15% GGR; უცხოელ მოთამაშეებზე 5%; კვარტალური მოსაკრებლები) `⚠`.
- **2026-06-01:** გაზრდილი ჯარიმები (კაზინო 7k→20k GEL და სხვ.) `⚠`.
- **International iGaming License (2026):** საერთაშორისო ბაზრებზე მომუშავე ოპერატორებისთვის — 5-წლიანი ნებართვა, ~**100,000 GEL/წელი** (~€33k) ყოველწლიურად წინასწარ, **5% GGR + 1% მონიტორინგის მოსაკრებელი**, 0% დღგ; სერტიფიცირება — **Random Systems Georgia (RSG/RSIG)**; pilot — ივლისი 2026, ოფიციალური გაშვება — **2026-09-28**. `⚠ გადასამოწმებელი` — ეს ძირითადად **B2C** ოპერატორებზეა, ახალი ამბავია და დეტალები იცვლება.
- **B2B მიმწოდებელი საქართველოში:** მესამე მხარის წყაროების მიხედვით არსებობს **B2B permit / „Game Service Provider"**-ის მსგავსი რეჟიმი მიმწოდებლებისთვის, რომლებიც აწვდიან პლატფორმას/RNG-ს/მართვის სისტემებს ლიცენზირებულ ოპერატორებს, RSIG-ის პროდუქტის სერტიფიცირებით; ფასი ~100,000 GEL/წელი, გადაწყვეტილება ~20 დღეში. **⚠ გადასამოწმებელი (კრიტიკული!)** — უნდა დადგინდეს: (1) სჭირდება თუ არა **odds/data feed-ის ინტეგრაციის და admin-ის** მიმწოდებელს ეს ნებართვა; (2) რა ზღვარია „tooling/IT სერვისსა" და „gaming supply"-ს შორის. ბმული: https://www.rsig.ge/en/permits-and-processes

### 7.3. საერთაშორისო B2B ლიცენზიები (შედარება)

| იურისდიქცია | ლიცენზია | ვის სჭირდება (მოკლედ) | ღირებულება (მიახლ.) | შენიშვნა |
|---|---|---|---|---|
| **Malta (MGA)** | **B2B Critical Gaming Supply** | „material elements of a game"-ის მიწოდება/მართვა; რეგულირებული ჩანაწერების (bets, outcomes) დამუშავების სისტემები; **risk management**; control system | განაცხადი €5,000; წლიური €25k (≤€5M შემოსავალი) / €30k / €35k; System Audit ~€2.5k–7.5k; ვადა 10 წელი | ევროპისთვის „ოქროს სტანდარტი"; სუბსტანცია მალტაში `⚠` |
| **UK (UKGC)** | **Remote Gambling Software** operating licence | ვინც აწარმოებს/აწვდის/აინსტალირებს/ადაპტირებს „gambling software"-ს GB-ლიცენზირებული ოპერატორებისთვის (მდებარეობის მიუხედავად) | განაცხადის ფასი დამოკიდებულია ბრუნვაზე (~£1.4k-დან) + წლიური `⚠` | ზოგადი ბიზნეს-პროგრამა არ ითვლება; **odds feed integration/trading tools — შესაძლოა ითვლებოდეს** → იურისტი |
| **Curaçao (CGA, LOK)** | **B2B license** / B2B Certificate (non-critical) | კრიტიკული B2B მიმწოდებლები; არაკრიტიკულისთვის — 3-წლიანი Certificate | განაცხადი ~€4,592; წლიური supervisory fee ~€24,490; დამატებითი მოსაკრებლები | LOK რეჟიმი 2024-დან; B2B განაცხადები 2025-დან `⚠` |
| **საქართველო** | B2B permit (Game Service Provider) `⚠` | იხ. 7.2 | ~100k GEL/წელი `⚠` | იხ. 7.2 |

**ჩვენი სავარაუდო სტრატეგია (იურისტთან დასადასტურებელი):**
1. პირველ 6–12 თვეს ვმუშაობთ როგორც **ტექნოლოგიური/IT მიმწოდებელი** (software + hosting) ოპერატორისთვის, რომელსაც თავად აქვს Sportradar-ის კონტრაქტი და ლიცენზია (მოდელი a) — ჩვენი ლიცენზირების ტვირთი მინიმალურია (`⚠ თითოეულ იურისდიქციაში ცალკე შეფასება`).
2. პირველ ფასიან კლიენტებზე დაყრდნობით ვირჩევთ ლიცენზიას: **Curaçao B2B** (იაფი, სწრაფი) ან **MGA Critical Supply** (ევროპული ბაზრები) ან **საქართველოს B2B** (ადგილობრივი ოპერატორები).

### 7.4. მონაცემების ლიცენზირება — ტიპური შეზღუდვები (ძალიან მნიშვნელოვანი!)

- [ ] **Resell / sub-license / redistribution — აკრძალულია** პროვაიდერის წერილობითი თანხმობის გარეშე (Sportradar-ის B2B T&C-ში ეს პირდაპირ წერია). ოპერატორებზე მონაცემების გავრცელებას სჭირდება **reseller/distribution agreement** ან თითოეული ოპერატორის **საკუთარი კონტრაქტი**.
- [ ] **Third-party service provider clause:** ოპერატორი ვალდებულია, რომ მისი მესამე მხარის პროვაიდერი (ე.ი. ჩვენ — „betting software company") **კონტრაქტით** ვალდებულდეს, არ გადაყიდოს/არ გაავრცელოს მონაცემები. → ჩვენს MSA-ში ეს პუნქტი უნდა იყოს.
- [ ] **Per-operator / per-brand / per-domain** ლიცენზირება; ახალი ბრენდი/საიტი = ახალი შეთანხმება `⚠`.
- [ ] **Territory restrictions:** მხოლოდ ლიცენზირებულ/ნებადართულ ბაზრებზე; sanctioned ქვეყნები გამორიცხულია.
- [ ] **Data retention:** კონტრაქტის შეწყვეტისას მონაცემების წაშლის ვალდებულება; ისტორიის გამოყენება ML-ისთვის — ცალკე უფლება.
- [ ] **Display vs. betting use:** media/display უფლება ≠ betting უფლება; trial data — მხოლოდ შიდა შეფასება.
- [ ] **Official data / league rights:** ზოგი ლიგის მონაცემი/სტრიმი ექსკლუზიურია კონკრეტულ პროვაიდერთან (მაგ. Genius — EPL/NFL official data); სტრიმები — geo-blocking და „bet-to-watch" პირობები.
- [ ] **Branding/trademark:** პროვაიდერის ლოგოს/ბრენდის გამოყენება — მხოლოდ თანხმობით (SDK License-იც კრძალავს).
- [ ] **SDK License** — იხ. ფაზა 0-ის შენიშვნა: არ ჩავდოთ SDK კოდი/ნიმუშები ჩვენს რეპოში.
- [ ] **Aggregator-ების (The Odds API და სხვ.) ToS:** raw data-ს API/feed-ად გაყიდვა აკრძალულია — ოპერატორზე მისაწოდებლად გამოუსადეგარია.

### 7.5. KYC / AML / Compliance (B2B-სთვის ტიპური მოთხოვნები)

- [ ] UBO/დირექტორების KYC (პასპორტი, მისამართი, ნასამართლობა), Personal History Disclosure ფორმები.
- [ ] AML/CFT პოლიტიკა (საქართველოში — ფინანსური მონიტორინგის სამსახურის მოთხოვნები, თუ ვხვდებით ანგარიშვალდებული პირის კატეგორიაში `⚠`).
- [ ] Responsible gaming-თან დაკავშირებული ფუნქციების მხარდაჭერა (ოპერატორისთვის).
- [ ] ISO 27001-ის მსგავსი ინფორმაციული უსაფრთხოების პოლიტიკები (ლიცენზიები/ოპერატორები ხშირად ითხოვენ); ლოგების, აუდიტის, change management-ის ჩანაწერები.
- [ ] ოპერატორის KYC (B2B-ში ჩვენ ვამოწმებთ, ლიცენზირებულია თუ არა კლიენტი) — Sportradar ამას ითხოვს.

---

## 8. დაფინანსება და კრედიტები

> **⚠ კრიტიკული შენიშვნა:** ბევრი სახელმწიფო გრანტი და ზოგი კორპორატიული პროგრამა **გამორიცხავს გამბლინგთან დაკავშირებულ საქმიანობას**. GITA-ს პირობებში გამბლინგის გამორიცხვის შესახებ ინფორმაცია ვერ ვიპოვეთ — **`⚠ გადასამოწმებელი` (განცხადებამდე პირდაპირ ჰკითხეთ GITA-ს)**. შესაძლოა პროდუქტის პოზიციონირება მოხდეს როგორც „sports data infrastructure / real-time data platform" (რომელიც გამოიყენება media/fantasy/analytics-შიც), მაგრამ **სიმართლე განაცხადში აუცილებელია**.

| პროგრამა | რა | თანხა (მიახლ.) | პირობები | სტატუსი |
|---|---|---|---|---|
| **GITA — თანადაფინანსების გრანტი (Matching/Co-financing)** | ინოვაციური სტარტაპები, საერთაშორისო პოტენციალით | **150,000 GEL**-მდე (ადრე 100k); თანადაფინანსება `⚠ % დასაზუსტებელი` | ნახევარწლიური კონკურსები; 13-ე რაუნდი — 2024 | `⚠ 2026 წლის რაუნდი გადასამოწმებელი` |
| **GITA — Innovative Startups Acceleration (Georgia Startup Academy, 500 Global-თან)** | აქსელერაცია + ეტაპობრივი გრანტები | Stage 2: **50,000 GEL**, Stage 3: **150,000 GEL** | ეტაპობრივი შერჩევა | `⚠` |
| **GITA — Matching Grant (ინვესტორთან)** | პროდუქტის სკალირება | **650,000 GEL**-მდე, კერძო ინვესტორის თანაბარი ინვესტიციით | ვალიდირებული პროდუქტი | `⚠ (2019-დან; მიმდინარე სტატუსი)` |
| **GITA — მინი/რეგიონული გრანტები** | ადრეული იდეები | 15,000–30,000 GEL `⚠` | | `⚠` |
| **Startup Georgia** | სახელმწიფო სტარტაპ-პროგრამა | ~100,000 GEL-მდე `⚠` | | `⚠ შეიძლება შეჩერებული/გაერთიანებული იყოს` |
| **AWS Activate — Founders** | ღრუბლოვანი კრედიტი | **$1,000** (24 თვე) | pre-Series B, < 10 წელი, ვებსაიტი | ღია |
| **AWS Activate — Portfolio** | | **$100,000**-მდე | აქსელერატორის/VC-ის (Activate Provider) მეშვეობით | GITA/500 Global-ის აქსელერაციის შემდეგ შესაძლოა |
| **Google for Startups Cloud — Start** | | **$2,000**-მდე (1 წელი) | არა equity-funded | ღია |
| **Google for Startups Cloud — Scale** | | **$200,000**-მდე (2 წელი) | Pre-seed–Series A equity-funded | |
| **Microsoft for Startups (2025-07-დან)** | | Self-service: **$5,000**; Investor Network: **$100,000**-მდე | ახალი Azure მომხმარებელი / ინვესტორის რეფერალი | ღია |
| **Sportradar Acceleradar** | უფასო მონაცემები სპორტ-ტექ სტარტაპებს | — | < 1 წელი, ≤ $500k | `⚠ 2016; ბეთინგზე არ ვრცელდება; სტატუსი უცნობია` |
| **Startupbootcamp / 500 Global / ადგილობრივი ანგელოზები** | აქსელერაცია + ინვესტიცია | ვარირებს | | `⚠` |

**პრაქტიკული გეგმა:** (1) დაუყოვნებლივ — AWS Founders / Google Start / Microsoft $5k (ერთ-ერთი ძირითად ღრუბლად, სხვები — backup); (2) კვირა 4–8 — GITA-ს მიმდინარე კონკურსის შემოწმება და განაცხადი (თუ გამბლინგ-B2B დასაშვებია); (3) აქსელერატორში მოხვედრის შემთხვევაში — Portfolio/Scale ტიერები.

---

## 9. რისკების რეესტრი

> Impact / Likelihood: H = მაღალი, M = საშუალო, L = დაბალი

| # | რისკი | Impact | Likelihood | Mitigation | Owner |
|---|---|---|---|---|---|
| R1 | **Sportradar არ გვაძლევს Integration env-ზე წვდომას** (ან აჭიანურებს) კონტრაქტამდე | H | H | Outreach ადრე (კვირა 5); LOI ოპერატორებისგან; ოპერატორის ანგარიშით შესვლა (მოდელი a); პარალელურად LSports trial; სიმულატორი, როგორც დამოუკიდებელი ღირებულება (QA/ტესტ-tool ოპერატორებისთვის) | CEO/BD |
| R2 | **მონაცემების ლიცენზიის შეზღუდვები** — რედისტრიბუცია ოპერატორებზე აკრძალული/ძვირი | H | H | ბიზნეს-მოდელი (a) „ოპერატორის კონტრაქტი + ჩვენი ტექნოლოგია"; წერილობითი დადასტურება კონტრაქტამდე; იურისტი | CEO + Legal |
| R3 | **Data model divergence** — სიმულატორი ≠ ნამდვილი ფიდი | M | H | სიმულატორი XSD-ზე; SDK-ის ნიმუშ-XML-ებით ვალიდაცია (შიდა); ფაზა 2-ში divergence report; adapter-ში tolerant parsing + unknown-field ლოგირება; contract tests | Tech Lead |
| R4 | **Latency / throughput** პრობლემები live-ზე (peak — შაბათ-კვირის ფეხბურთი) | H | M | Load test სიმულატორით (peak×3); EU რეგიონში ჰოსტინგი; lag-მეტრიკა და alert-ები; backpressure; Replay scenarios stress-ისთვის | DevOps |
| R5 | **Compliance / ლიცენზირება** — B2B ნებართვა საჭირო აღმოჩნდება (GE/MGA/UKGC) | H | M | ადრეული იურიდიული კონსულტაცია (კვირა 4–6); ბიუჯეტში რეზერვი; ჯერ იმ ბაზრებზე, სადაც tooling-ს ნებართვა არ სჭირდება `⚠` | CEO + Legal |
| R6 | **SDK/ნიმუშების ლიცენზიის დარღვევა** (კოდის ჩადება/რედისტრიბუცია) | M | M | Repo policy: Sportradar-ის SDK კოდი/XML არ შედის რეპოში; საკუთარი სინთეტიკური fixtures; code review checklist | Tech Lead |
| R7 | **Integration env 24/5** — შაბათ-კვირას ტესტირება შეუძლებელია | L | H | Replay Server შაბათ-კვირას; სიმულატორი 24/7 | QA |
| R8 | **Recovery-ის არასწორი იმპლემენტაცია** → არასწორი odds/settlement ოპერატორთან (ფინანსური ზარალი) | H | M | Recovery state machine-ის ავტოტესტები; producer-down → auto-suspend; Replay რეგრესია; SDK-ის ლოგიკის შესწავლა | Tech Lead |
| R9 | **ძირითადი ადამიანის დაკარგვა** (bus factor 1) | H | M | დოკუმენტაცია/ADR-ები, pair programming, ESOP | CEO |
| R10 | **ფინანსების ამოწურვა** სანამ პირველი შემოსავალი მოვა | H | M | Lean ბიუჯეტი; გრანტები/კრედიტები; სიმულატორის/monitoring tool-ის ადრეული მონეტიზაცია (QA-as-a-service ოპერატორებისთვის) | CEO |
| R11 | **გრანტ-პროგრამები გამორიცხავს გამბლინგს** | M | M | წინასწარ კითხვა; კერძო ინვესტორები/ანგელოზები; კრედიტები | CEO |
| R12 | **საბანკო მომსახურების პრობლემა** (high-risk ინდუსტრია) | M | M | ადრეული საუბარი ბანკთან; B2B ტექ-მიმწოდებლის პოზიციონირება; რეზერვული ბანკი/EMI | CEO |
| R13 | **რეგულაციის ცვლილება საქართველოში** (2024–2026 ხშირი ცვლილებები) | M | M | რეგულარული მონიტორინგი (კვარტალური); ადგილობრივი იურისტი | Legal |
| R14 | **Vendor lock-in** Sportradar-ზე (ფასის ზრდა, პირობები) | M | M | Provider-agnostic canonical model; მე-2 პროვაიდერი ფაზა 3-ში | Tech Lead |
| R15 | **უსაფრთხოება** — token-ების გაჟონვა, DDoS, ოპერატორის მონაცემები | H | L | Secret manager, IP allowlist, MFA admin-ში, აუდიტ-ლოგი, pentest ფაზა 3-მდე | DevOps |

---

## 10. ბიუჯეტი — პირველი 6 თვე (lean)

> **დაშვებები:** 1 USD ≈ 2.7 GEL `⚠`; ხელფასები — თბილისის ბაზარი (gross, 20% საშემოსავლო + 2% საპენსიო დამსაქმებლის წილი გათვალისწინებულია მიახლოებით) `⚠ ბაზრის კვლევა საჭიროა`; Sportradar-ის **production** ღირებულება 6 თვეში **არ შედის** (integration — უფასო; production — კონტრაქტის შემდეგ, ჩვეულებრივ ოპერატორის ხარჯი მოდელ (a)-ში). ღრუბლის ხარჯის დიდი ნაწილი იფარება კრედიტებით.

### სცენარი A — „Bootstrap" (2 თანადამფუძნებელი ხელფასის გარეშე + 1 დაქირავებული)

| კატეგორია | თვიური (USD) | 6 თვე (USD) | შენიშვნა |
|---|---|---|---|
| Backend Developer (mid) ×1 | 2,500 | 15,000 | `⚠` |
| Frontend/Fullstack freelance (part-time, ~40%) | 800 | 4,800 | ფაზა 1–2 |
| DevOps freelance (~20 სთ/თვე) | 500 | 3,000 | |
| Cloud/infra (კრედიტების შემდეგ) | 50–150 | ~600 | AWS/GCP/Azure კრედიტები + Hetzner/Oracle free |
| სტატიკური IP / domain / email / SaaS (GitHub, Grafana Cloud free, Notion/Linear free) | 60 | 360 | |
| კომპანიის რეგისტრაცია + ბუღალტერია | 200 | 1,300 | რეგისტრაცია ~100 + ბუღალტერი ~200/თვე `⚠` |
| იურიდიული კონსულტაცია (iGaming/ლიცენზირება/კონტრაქტები) | — | 3,000 | 2–3 სესია + MSA/NDA შაბლონები `⚠` |
| კონფერენცია (1 ღონისძიება, მაგ. SBC/ICE — სავიზიტორო ბილეთი + მგზავრობა) | — | 1,500 | Sportradar-თან და ოპერატორებთან შეხვედრა `⚠` |
| გაუთვალისწინებელი (10%) | — | 3,000 | |
| **სულ** | | **≈ 32,500** | |

### სცენარი B — „Lean team" (4–5 ადამიანი, ხელფასებით)

| კატეგორია | თვიური (USD) | 6 თვე (USD) |
|---|---|---|
| Tech Lead / Senior Backend | 4,500 | 27,000 |
| Backend Developer | 2,500 | 15,000 |
| Fullstack/Frontend | 2,200 | 13,200 |
| DevOps/QA (part-time) | 1,200 | 7,200 |
| CEO/BD (მინიმალური ხელფასი) | 1,500 | 9,000 |
| Infra + SaaS | 250 | 1,500 |
| იურიდიული + ბუღალტერია | — | 5,500 |
| კონფერენციები/მგზავრობა (1–2) | — | 4,000 |
| სავარაუდო ლიცენზიის წინასწარი ხარჯები (due diligence, დოკუმენტები; **არა** ლიცენზიის საფასური) | — | 2,000 |
| გაუთვალისწინებელი (10%) | — | 8,500 |
| **სულ** | | **≈ 93,000** |

**არ შედის (6 თვის შემდეგ, შესაძლო):** B2B ლიცენზია (Curaçao ~€29k პირველ წელს; MGA ~€30k+ აუდიტით; საქართველო ~100k GEL/წელი `⚠`), Sportradar/LSports production კონტრაქტები, pentest/ISO 27001, მეორე პროვაიდერის ფასი, სტრიმინგის უფლებები.

**დაფინანსების წყარო:** სცენარი A — დამფუძნებლების სახსრები + ღრუბლის კრედიტები; სცენარი B — GITA გრანტი (150k GEL ≈ $55k `⚠`) + ანგელოზი/pre-seed.

---

## 11. წყაროები

> ბოლო ნახვა: 2026-10-02. საიტები, რომლებიც პირდაპირ ვერ გაიხსნა, აღნიშნულია „(ძიების ამონარიდით)".

**Sportradar / Betradar — UOF, წვდომა, SDK**
- UOF Environments (ძიების ამონარიდით): https://docs.sportradar.com/uof/introduction/environments
- UOF Replay Server (ძიების ამონარიდით): https://docs.sportradar.com/uof/replay-server
- UOF SDK Replay: https://docs.sportradar.com/uof/sdk/features/replay
- UOF FAQ: https://docs.sportradar.com/uof/support-and-history/faq/uof-faq
- UOF Start Guide (PDF): https://iodocs.betradar.com/unifiedsdk/UOF_Start_Guide.pdf
- UOF Integration Process (PDF): https://iodocs.betradar.com/unifiedsdk/UOF_Integration_Process.pdf
- Betradar Unified Odds Feed (sportsbook support): https://betradar.com/sportsbook-support/unified-odds-feed/
- UnifiedOddsSdkJava (LICENSE.md, XSD-ები, ნიმუშები — პირდაპირ შემოწმდა, v4.12.0): https://github.com/sportradar/UnifiedOddsSdkJava
- UnifiedOddsSdkNetCore (v3.12.0): https://github.com/sportradar/UnifiedOddsSdkNetCore
- minus5/go-uof-sdk (MIT): https://github.com/minus5/go-uof-sdk
- efcasado/betradar_uof_sdk (Elixir): https://github.com/efcasado/betradar_uof_sdk
- Sportradar Developer Portal: https://developer.sportradar.com/
- Sportradar API Trial: https://store.sportradar.com/en/trials/api-trial.php
- Master Terms and Conditions for Non-Betting Services: https://developer.sportradar.com/sportradar-updates/page/master-terms-and-conditions-for-non-betting-services
- Sportradar General T&C (MTS / B2B clients): https://sportradar.com/general-terms-and-conditions-mts-b2b-clients/?lang=en-us
- Live Match Tracker docs: https://apidocs.sportradar.com/resources/widgets/docs/lmt/lmt-plus
- Acceleradar: https://thefsga.org/sportradar-introduces-acceleradar/
- Sportradar completes IMG ARENA acquisition (2025-11): https://www.sportsvideo.org/2025/11/04/sportradar-completes-acquisition-of-img-arena-and-its-strategic-portfolio-of-global-sports-betting-rights/

**უფასო / ალტერნატიული მონაცემები**
- The Odds API: https://the-odds-api.com/ · Terms: https://the-odds-api.com/terms-and-conditions.html
- The Odds API free tier analysis: https://oddspapi.io/blog/the-odds-api-free-tier-limits/
- API-Football: https://www.api-football.com/news/post/how-to-get-started-with-api-football-the-complete-beginners-guide
- football-data.org: https://www.football-data.org/client/register · free tier limits: https://www.thestatsapi.com/blog/football-data-org-free-tier-limits-2026
- OpenLigaDB: https://www.openligadb.de/
- TheSportsDB docs: https://www.thesportsdb.com/documentation
- Sportmonks free plan: https://www.sportmonks.com/football-api/free-plan/
- Betfair API costs: https://support.developer.betfair.com/hc/en-us/articles/115003864531-Are-there-any-costs-associated-with-API-access · Vendors: https://support.developer.betfair.com/hc/en-us/articles/360002190732-Do-Software-Vendors-have-to-pay-to-access-the-Betfair-API

**პროვაიდერები**
- LSports: https://www.lsports.eu/ · TRADE: https://www.lsports.eu/trade/ · Sports data cost: https://www.lsports.eu/blog/sports-data-cost/
- Genius Sports Developer Centre: https://developer.geniussports.com/ · Odds feeds: https://www.geniussports.com/bet/odds-feeds-api/
- Betgenius: https://betgenius.com/sportsbook-management
- TXODDS: https://txodds.net/ · TXAPI guide: https://txodds.com/docs/TXODDS_TXAPI_Market_Odds_Feed_User_Guide.pdf
- Kambi / OddsMatrix / BetConstruct overview: https://www.lsports.eu/blog/best-odds-compiler-software/ · https://track360.io/blog/sportsbook-platform-providers-vendor-comparison-kambi-altenar-betby-2026

**რეგულაცია**
- Georgia Boosts Gambling Oversight and Taxes in 2024: https://georgiatoday.ge/georgia-boosts-gambling-oversight-and-taxes-in-2024/
- Georgia Gambling Law (Legal500): https://www.legal500.com/guides/chapter/georgia-gambling-law/
- RS Georgia — B2B/B2C permits: https://www.rsig.ge/en/permits-and-processes
- Georgia International iGaming licence: https://www.igamingtoday.com/georgia-rolls-out-international-igaming-licence-with-6-ggr-tax/ · https://focusgn.com/asia-pacific/georgia-international-igaming-licence · https://affpapa.com/georgia-launches-its-international-igaming-license/
- Georgia gaming licence overview: https://legarithm.io/license/gambling/georgia/
- MGA Licence Fees guidance (PDF): https://www.mga.org.mt/app/uploads/Guidance-Note-Licence-Fees-and-Taxation-1.pdf · Game providers/B2B: https://www.mga.org.mt/licensee-hub/applications/b2b-licences/game-providers-and-back-office/
- MGA B2B overview: https://www.softswiss.com/knowledge-base/malta-igaming-license-guide/
- UKGC licences & fees: https://www.gamblingcommission.gov.uk/licensees-and-businesses/licences-and-fees · Remote gambling software: https://www.gamblingcommission.gov.uk/licensees-and-businesses/guide/gambling-software-and-online-businesses-sector-specific-compliance · Keystone Law: https://keystonelaw.com/keynotes/gambling-software-licences-applications/
- Curaçao LOK fees: https://agbrief.com/news/world/03/11/2025/curacao-gaming-authority-updates-fee-policy-under-new-lok-framework/ · https://www.chambersandco.com/curacao-gaming-license-2025-new-lok-regulations-requirements-fees/

**დაფინანსება / კრედიტები / გადასახადები**
- GITA: https://gita.gov.ge/en · Matching grants winners: https://gita.gov.ge/en/news/winners-of-startup-matching-grants-program-revealed-72E55ysBi
- GITA 150k GEL: https://cbw.ge/startup/20-startups-received-150000-gel-funding-from-gita · Acceleration program: https://grants.gov.ge/en/Grants?call=271
- AWS Activate: https://aws.amazon.com/aws-startups/learn/applying-for-aws-activate-credits-a-step-by-step-guide/
- Google for Startups Cloud: https://cloud.google.com/startup/benefits
- Microsoft for Startups changes (2025-07): https://learn.microsoft.com/en-us/startups/changes-microsoft-for-startups
- Virtual Zone (Andersen Georgia): https://ge.andersen.com/virtual-zone-person-georgia/
- Startups in Georgia: taxes and benefits: https://en.justadvisors.ge/startapy-gruziya-nalogi-lgoty-investicii-2025

---

### დანართი: შემდეგი ნაბიჯები (ამ კვირაში)

1. ჩამოტვირთეთ SDK-ები, ამოიღეთ XSD-ები შიდა `schemas/`-ში (`.gitignore`-ით ან private repo-თი, ლიცენზიის გათვალისწინებით).
2. გადაწყვიტეთ სტეკი (ADR-001..003) — არქიტექტურის გუნდთან ერთად.
3. დაიწყეთ კომპანიის რეგისტრაცია + ღრუბლის კრედიტების განაცხადები.
4. დაჯავშნეთ 1 საათიანი კონსულტაცია iGaming იურისტთან თბილისში (კითხვები: 7.2 B2B permit, Virtual Zone, ბანკი).
5. დაიწყეთ deck-ის და one-pager-ის დრაფტი (სექცია 4.2).
