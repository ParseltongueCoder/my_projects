# ფაზა 1 — ტესტირების გზამკვლევი

ფაზა 1 მთლიანად იშვება ერთ კომპიუტერზე, Docker-ში. ყველაფერი ეშვება ერთი ბრძანებით:

- სიმულატორი ცვლის Betradar-ს;
- RabbitMQ;
- PostgreSQL;
- UOF adapter;
- Keycloak;
- Feed Ops ადმინი;
- Prometheus და Grafana.

Sportradar-ის ანგარიში **არ არის საჭირო**.

## 1. რა უნდა იყოს დაყენებული

| | |
|---|---|
| Docker Desktop (Windows/macOS) ან Docker Engine + Compose v2 (Linux) | 8 GB RAM Docker-ისთვის საკმარისია |
| Git | Windows-ზე Git for Windows, რომელსაც მოყვება **Git Bash** და `openssl` |
| თავისუფალი პორტები | 3000, 5432, 5671, 5672, 8080, 8081, 8088, 8180, 9090, 15672 |

თუ ლოკალურად უკვე გაქვთ PostgreSQL 5432 პორტზე, გააჩერეთ. სხვა გზაა `platform/docker-compose.yml`-ში `"5432:5432"`-ის შეცვლა, მაგალითად `"55432:5432"`-ით.

## 2. გაშვება

ბრძანებები Windows-ზე გაუშვით **Git Bash**-ში, macOS/Linux-ზე ჩვეულებრივ ტერმინალში:

```bash
git clone https://github.com/ParseltongueCoder/my_projects.git
cd my_projects
git checkout claude/bettor-api-integration-hqk4e1

cd betradar-integration/platform
../uof-simulator/tools/gen-tls.sh          # ერთხელ: dev TLS სერტიფიკატი RabbitMQ-სთვის
docker compose up -d --build               # პირველი build 5–10 წუთი
docker compose ps                          # ყველა სერვისი უნდა იყოს running
```

Keycloak-ის ჩართვას დაახლოებით 30–60 წამი სჭირდება.

| რა | მისამართი | შესვლა |
|---|---|---|
| **Feed Ops ადმინი** | http://localhost:8088 | `operator` / `operator` (სრული), `viewer` / `viewer` (მხოლოდ ნახვა) |
| Grafana, dashboard „UOF Feed Health“ | http://localhost:3000 | ანონიმურად (admin/admin) |
| Prometheus alert-ები | http://localhost:9090/alerts | — |
| RabbitMQ management | http://localhost:15672 | `admin` / `admin` |
| Keycloak admin | http://localhost:8180 | `admin` / `admin` |
| სიმულატორის API | http://localhost:8080/sim/status | — |

## 3. სატესტო სცენარები

ყველა სცენარი ადმინიდან სრულდება. შედით `operator`-ით და გახსენით **Simulator** გვერდი.

### T1. სრული მატჩი: გოლები, settlement, rollback
1. **Simulator** → `derby_settlement_rollback` → **Start**. ნაგულისხმევი speed 2× ნიშნავს დაახლოებით 25 წამს; ნელა სანახავად დააყენეთ 1×.
2. **Overview**: მატჩი *Kutaisi Eagles v Rustavi Steel* ჯერ არის `not_started`, შემდეგ `live`. Live მატჩებისა და მარკეტების რაოდენობა ღილაკის დაჭერის გარეშე იცვლება.
3. **Events** → მატჩი → **Markets**:
   - სახელები გახსნილია: „1x2“, „Total 2.5“, „Over 2.5“, გუნდების სახელები outcome-ებში;
   - odds იცვლება ყოველი გოლის შემდეგ;
   - bet stop-ის დროს მარკეტები `suspended` ხდება, შემდეგ ისევ `active`;
   - შესვენებაზე (halftime) მარკეტები შეჩერებულია.
4. **დასრულების შემდეგ:**
   - ანგარიში არის **2:1**;
   - მარკეტები `settled`-ია;
   - **Settlements** ჩანართზე ჩანს certainty 1-ის settlement, მერე **rollback** (გადახაზული / rolled back), მერე certainty 2-ის settlement.
5. **Bet stops** ჩანართზე ჩანს სამი bet stop, წყარო `feed`.

### T2. გაუქმებული მატჩი
1. **Simulator** → `abandoned_match` → **Start**.
2. *Batumi Waves v Kutaisi Eagles*: ჯერ live, შემდეგ bet stop, შემდეგ ყველა მარკეტი `cancelled` (bet_cancel).

### T3. Producer-ის გათიშვა (ყველაზე მნიშვნელოვანი უსაფრთხოების ტესტი)
1. გაუშვით T1 და, სანამ მატჩი live-ია, Simulator → **LO #1** → **Silent**. ნაგულისხმევი ხანგრძლივობაა 40 წამი; ჯობს speed 1×-ით გაშვება, რომ მატჩი გათიშვამდე არ დასრულდეს.
2. 10–20 წამში:
   - **Producers** გვერდზე LO ხდება `down`;
   - მატჩის **ყველა ღია მარკეტი** ხდება `suspended`;
   - **Bet stops** ჩანართზე ჩნდება ჩანაწერი წყაროთი `producer_down`.
3. გათიშვის დასრულების შემდეგ SDK აკეთებს recovery-ს და producer ბრუნდება `up`-ზე. მარკეტები იხსნება **მხოლოდ** იმ შემთხვევაში, თუ feed-ის მიხედვით active-ია.
4. Grafana-ში `uof_producer_up` ეცემა 0-მდე და ბრუნდება. Prometheus-ში 30 წამის შემდეგ ჩნდება alert `UofProducerDown`.

### T4. Raw შეტყობინებები
1. **Feed messages** → ფილტრი `bet_settlement` ან `rollback_bet_settlement`.
2. ხაზზე დაჭერით იხსნება გვერდითი პანელი ორიგინალი XML-ით (raw archive). შეტყობინების სტატუსი არის `processed`.

### T5. როლები
1. გამოდით (header → logout) და შედით `viewer`-ით.
2. ყველა გვერდი ჩანს. Simulator-ზე ღილაკები გათიშულია და ჩანს შეტყობინება „Read-only“.

### T6. Recording-ის replay
Simulator → `demo_match (recording)` → **Replay**. ჩაწერილი მატჩი თავიდან გადის feed-ში და progress bar აჩვენებს მიმდინარეობას.

## 4. სუფთა მდგომარეობა

ერთი და იგივე სცენარის მეორედ გაშვებისას უკვე settled მარკეტები settled რჩება. ეს რეალური feed-ის სწორი ქცევაა. სცენარის თავიდან გასატესტად ბაზა უნდა გასუფთავდეს:

```bash
docker compose down -v      # აჩერებს და შლის ბაზას
docker compose up -d
```

გაჩერება ბაზის შენარჩუნებით: `docker compose down`.

## 5. თუ რამე არ მუშაობს

| სიმპტომი | გამოსავალი |
|---|---|
| `gen-tls.sh: openssl: command not found` | Windows-ზე გაუშვით Git Bash-ში, არა PowerShell-ში |
| `rabbitmq` restart-დება პირველ გაშვებაზე | ნორმალურია: ერთხელ restart-დება და ჩაირთვება (`docker compose logs rabbitmq`) |
| ადმინის შესვლის გვერდი არ იხსნება | Keycloak ჯერ იტვირთება; დაელოდეთ 1 წუთი (`docker compose logs keycloak`) |
| ადმინში ცარიელია | სცენარი ჯერ არ გაგიშვიათ, ან adapter ჯერ არ ჩართულა: `docker compose logs uof-adapter` |
| `port is already allocated` | სხვა პროგრამა იყენებს პორტს (ხშირად 5432 ან 8080); გააჩერეთ, ან შეცვალეთ პორტი compose-ში |
| Windows: `/bin/sh^M` ან `exec format error` | რეპო დაკლონილი იყო `.gitattributes`-მდე: `git rm --cached -r . && git reset --hard` |

ხარვეზი ჩაიწერეთ შემდეგი ინფორმაციით: სცენარი, ნაბიჯი, რას ელოდით, რა მოხდა, screenshot. ასევე დაურთეთ `docker compose logs uof-adapter admin-api > logs.txt`.

## 6. ავტომატური ტესტები (დეველოპერებისთვის, სურვილისამებრ)

საჭიროა .NET 10 SDK და Node 24:

```bash
cd betradar-integration/uof-simulator && dotnet test           # 74 ტესტი
cd ../platform && dotnet test tests/Platform.Tests             # DB ტესტები: იხ. platform/README.md
cd ../admin/admin-web && npm ci && npx ng test feed-ops --watch=false && npx ng test ui --watch=false
```
