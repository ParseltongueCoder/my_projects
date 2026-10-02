# ADR-001 — Production stack: .NET 10 + ოფიციალური Sportradar .NET SDK

- **სტატუსი:** მიღებულია · 2026-10-02
- **ცვლის:** docs/04 §0 და §6.1-ის რეკომენდაციას (Java + Spring Boot)

## კონტექსტი

docs/04-ში Java იყო რეკომენდებული, იმ პირობით, რომ „თუ გუნდი .NET-ზეა — აირჩიეთ .NET + ოფიციალური .NET SDK; არქიტექტურა უცვლელია, ნუ შეურევთ ორივეს“. გუნდი .NET-ზე მუშაობს.

## გადაწყვეტილება

1. **ყველა production სერვისი:** .NET 10 (LTS), ASP.NET Core, C#.
2. **UOF adapter:** `Sportradar.OddsFeed.SDKCore` (NuGet, v3.12.0 დღეის მდგომარეობით) — როგორც შეუცვლელი dependency (SDK License Agreement კრძალავს მოდიფიკაციას/რედისტრიბუციას).
3. **სიმულატორიც .NET-ზეა** (docs/03-ში Python იყო გეგმაში): ერთი ენა, ერთი CI, გუნდს შეუძლია მისი შენარჩუნება. docs/03-ის Python ჩონჩხები რჩება დიზაინის რეფერენსად; რეალიზაცია — `uof-simulator/`.
4. დანარჩენი stack docs/04-დან უცვლელია: NATS JetStream, PostgreSQL 18, Valkey, React + Refine admin, Keycloak, OTel/Prometheus/Grafana.

## შედეგები

- .NET მხარე, რომელიც docs/04-ში Java-სთვის იყო აღწერილი (Spring Boot → ASP.NET Core / Worker Service; JAXB → XmlSerializer; Micrometer → OpenTelemetry .NET).
- NuGet პაკეტები: `RabbitMQ.Client` 7.x, `NATS.Net`, `Npgsql`, `StackExchange.Redis` (Valkey-თან თავსებადი), `OpenTelemetry.*`.
- SDK-ს მოაქვს `OpenTelemetry.*` 1.11.2, რომელსაც ცნობილი advisory აქვს (NU1902) — პირდაპირი PackageReference-ით ავწიეთ 1.19.1-მდე (იხ. `tests/UofSim.SdkSmoke`).
- SDK-ს სჭირდება რეალური cultures → სერვისებში, რომლებიც SDK-ს იყენებენ, `InvariantGlobalization=false`.

## დადასტურება

`uof-simulator/tests/UofSim.SdkSmoke` — ოფიციალური .NET SDK უკავშირდება სიმულატორს (`SelectCustom()`), გადის startup-ს, recovery-ს (producers 1 და 3 → UP), იღებს `odds_change` / `bet_stop` / `bet_settlement`-ს და chaos ტესტში აფიქსირებს ProducerDown → recovery (`after=`) → ProducerUp. გაშვებულია ლოკალურად 2026-10-02-ს.
