# 05 — Operator Back Office: საბაზრო კვლევა (B2B sportsbook-ები), gap analysis და საქართველოს რეგულაცია

> **სტატუსი:** სამუშაო დრაფტი v0.1 · **თარიღი:** 2026-10-04 · **ქალაქი:** თბილისი
> **აუდიტორია:** დამფუძნებლები, product, BO-ს დიზაინის გუნდი (CAT/ODDS/LIM/BET/CASH/CUS/PROMO/REP/ADM მოდულების ავტორები)
> **ნიშნები:** `⚠` — ფაქტი ვერ გადამოწმდა პირველწყაროდან, ან წყაროები ერთმანეთს ეწინააღმდეგება, ან სწრაფად იცვლება.
>
> **მეთოდი:** კვლევისას vendor-ების საიტები (altenar.com, betconstruct.com, everymatrix.com, digitain.com, betby.com, legal500.com, georgiatoday.ge და სხვ.) პირდაპირ **ვერ გაიხსნა** (ქსელის egress-შეზღუდვა), ამიტომ შინაარსი აღებულია ძიების შედეგების ამონარიდებიდან (ოფიციალური product-გვერდები, ბლოგები, პრეს-რელიზები, ინდუსტრიული მედია — SBC, iGB, Yogonet, Gambling Insider, Focus GN, SiGMA). ეს არის **მარკეტინგული** წყაროები — ისინი აჩვენებენ, რას „ყიდიან" vendor-ები, და არა ზუსტ UI-ს. ⚠ ყველა vendor-specific დეტალი მიიჩნიეთ „ღიად დეკლარირებულად", არა დემოში ნანახად.
>
> **დაკავშირებული დოკუმენტები:** [01 — Roadmap](01-roadmap-and-access.md) (§7 იურიდიული), [02 — UOF data model](02-uof-data-model.md), [04 — Platform architecture](04-platform-architecture.md). მოდულის კოდები — shared brief-ის მიხედვით (CAT, I18N, ODDS, CFG, LIM, BET, CASH, CUS, CMS, REP, ADM, PROMO, INT, NOTIF).

---

## სარჩევი

1. [Executive summary](#1-executive-summary)
2. [Vendor-ების მოკლე მიმოხილვა](#2-vendor-ების-მოკლე-მიმოხილვა)
3. [კონსოლიდირებული მოდულების ტაქსონომია](#3-კონსოლიდირებული-მოდულების-ტაქსონომია)
4. [Gap analysis — მომხმარებლის სია vs ინდუსტრიის სტანდარტი](#4-gap-analysis)
5. [საქართველოს რეგულაცია — რა უნდა შეძლოს BO-მ](#5-საქართველოს-რეგულაცია)
6. [დიფერენციაციის იდეები ახალი მოთამაშისთვის](#6-დიფერენციაციის-იდეები)
7. [წყაროები](#7-წყაროები)

---

## 1. Executive summary

- **ყველა** სერიოზული B2B sportsbook (Altenar, BetConstruct, Kambi, Digitain, OddsMatrix, Sportingtech, BtoBet, OpenBet, Amelco, GiG, Betby, GR8 Tech) ერთსა და იმავე ბირთვს ყიდის: **offering/catalog management → pricing/margin → limits & risk → bet acceptance → settlement → cash-out → player management/segmentation → bonus engine → reporting/BI**. განსხვავება არის სიღრმეში და იმაში, ვინ აკეთებს trading-ს (ოპერატორი თუ vendor — „Managed Trading Services").
- მომხმარებლის მოთხოვნილი სია კარგად ფარავს **catalog/content, odds, limits, customers, cash-out, reports, admin, promo**-ს. **ყველაზე დიდი ხვრელები:**
  1. **Bet acceptance pipeline** — live bet delay, odds-change acceptance (accept higher/any), manual referral (trader approval queue), counter-offer.
  2. **Customer risk profiling** — risk group / **stake factor** (customer confidence factor), ავტომატური სეგმენტაცია.
  3. **Liability / exposure monitoring** — real-time ticker, liability per event/market/outcome, alerts.
  4. **Results & settlement management** — manual settlement, resettlement, void, ticket-level correction, (ჩვენი manual მარკეტებისთვის აუცილებელი).
  5. **Responsible gambling + Georgian compliance** — ban-registry check (Revenue Service), ასაკი 25+, limits, regulatory reporting, audit.
  6. **Engagement tools** — bet builder, odds/price boosts, acca boost/insurance, early payout, featured events/homepage layout.
  7. **Bonus abuse / arbitrage / multi-account detection**, **NOTIF (alerts)**, **multi-currency**, **audit log**.
- **რეკომენდაცია P0-ისთვის (პირველი პილოტი):** CAT, I18N, CFG, ODDS (margin), LIM (+stake factor), BET (acceptance + delay + ticket search + manual settle/void), CASH (enable/disable + margin), CUS (mirror + limits + risk group + ban flag), ADM (RBAC + audit), REP (GGR/turnover/liability ბაზისური), INT (PAM), NOTIF (ბაზისური). PROMO-დან P0-ში მხოლოდ **freebet** (ხელით/სეგმენტზე გაცემა). დანარჩენი — P1/P2 (§4.3).

---

## 2. Vendor-ების მოკლე მიმოხილვა

> ფორმატი: **რას აცხადებენ** (მოდულები) · **გამორჩეული ფიჩერი** · **ჩვენთვის გაკვეთილი**.

### 2.1. Altenar (Malta / ფართო LatAm + EU)
- **მოდულები:** Back office real-time მონიტორინგით (performance, bet placement, financial activity); **Event View** — ივენთის დონეზე settings, **custom trading templates per event**, event-specific limits და status, market-level custom margins და **manual odds**; 24/7 risk management & trading support (2013-დან); fraud detection, irregular pattern flagging; retail solution (betting shops).
- **Promo:** Bonus Tool — cashback on demand, bet void, **early payout**, **accumulator insurance**, VIP custom campaigns, rollover (wagering) settings; **Bet Boost** სამი რეჟიმით — Normal (მარჟის ნაწილი რჩება), **0% margin** (fair price), **Super/Golden boost** (fair-ზე მაღლა); **Bore Draw** (0-0 refund), Bet Builder, Cashout.
- **გაკვეთილი:** „trading template" — ივენთზე ერთი ღილაკით მიებმება margin/limit/delay პროფილი. ჩვენთან ეს = `bo.setting`-ის **preset/template** (key-ების ნაკრები, რომელიც scope-ზე ერთდროულად ვრცელდება).
- წყარო: [Event trading tools guide](https://altenar.com/en-us/blog/sportsbook-features-guide-event-trading-tools/), [Bet boost](https://altenar.com/blog/sportsbook-features-guide-bet-boost-promo-tool/), [Early payouts](https://altenar.com/blog/sportsbook-features-guide-early-payouts/), [Bonus system](https://altenar.com/blog/which-bonus-system-to-choose-for-your-sports-betting-businesses/), [Risk mgmt support](https://altenar.com/en-us/blog/sportsbook-guide-risk-management-support/)

### 2.2. BetConstruct (Armenia — რეგიონის უახლოესი კონკურენტი)
- **მოდულები:** BackOffice — markets choice, margins, limits, player view, reports; customizable **KPI dashboard**, real-time player & betting statistics; **mobile BackOffice** (sportsbook/casino/turnover/bets/reports ტელეფონიდან); Risk Management tool — player categorisation (multi-account + behaviour analysis), ახალი რეგისტრირებული მოთამაშეების ბეთების მონიტორინგი, **„event time vs bet acceptance time"** შემოწმება (late-bet / courtsiding დაცვა), კატეგორიის მიხედვით limits; შიდა ინსტრუმენტები Odds Comparison, FeedConstruct, Umbrella.
- **გამორჩეული:** turnkey/white-label ეკოსისტემა (PAM+CRM+payments+casino) — ჩვენ ამას არ ვაკეთებთ (ოპერატორს აქვს PAM).
- **გაკვეთილი:** mobile BO და KPI dashboard რეგიონში მოსალოდნელი „hygiene" ფიჩერია; player categorisation — must-have.
- წყარო: [Sportsbook product](https://www.betconstruct.com/products/sportsbook), [Mobile BO](https://focusgn.com/betconstruct-optimizes-backoffice-for-mobile-use), [Risk Management tool](https://www.yogonet.com/international/news/2025/04/21/102276-betconstruct-highlights-realtime-risk-management-tool-to-enhance-sportsbook-security), [Risk services](https://www.betconstruct.com/services/risk-management)

### 2.3. Kambi (Sweden — premium managed sportsbook)
- **მოდულები:** turnkey — odds compiling, risk management, customer intelligence, content management tools, retail; **price differentiation tool** (ოპერატორი არეგულირებს მარჟას სტრატეგიის მიხედვით); bespoke pricing & risk; Odds Feed+ (feed-only მოდელი).
- **Engagement:** **Bet Builder** (pre-match + in-play, cash-out-ით), **2Up** (early payout ფეხბურთში), Free Bets, **Odds Boosts**, targeted campaigns.
- **გაკვეთილი:** Kambi-ში ოპერატორი მარჟას „ამოძრავებს", trading-ს კი Kambi აკეთებს. ჩვენი პოზიცია: ოპერატორს აქვს Sportradar კონტრაქტი — ე.ი. ფასი = feed + ოპერატორის margin ფენა (ODDS).
- წყარო: [Turnkey](https://www.kambi.com/what-we-do/turnkey-sportsbook/), [Bet Builder](https://www.kambi.com/kambi-what-we-do-b2b-sports-betting-platform/bet-builder/), [Odds Feed+](https://www.kambi.com/kambi-what-we-do-b2b-sports-betting-platform/odds-feed-plus/), [SBC Americas profile](https://sbcamericas.com/2024/10/17/kambi-premium-sports-betting-solutions/)

### 2.4. Digitain (Armenia)
- **მოდულები:** Centrivo პლატფორმა — backoffice, CRM, affiliate tracking, retail; player management (KYC/AML), KPI dashboards, marketing/promotions (bonuses, loyalty), **CMS**; 24/7 risk management; ალგორითმული betting-pattern მონიტორინგი, **manual granular limits & odds adjustments**, **liability reporting**; „single back office for all clients" (multi-brand).
- **გაკვეთილი:** multi-brand/multi-tenant ერთ BO-ში — ჩვენი `operator_id` მოდელი იგივე იდეაა; platform-staff-ს სჭირდება cross-operator view.
- წყარო: [Sportsbook software](https://www.digitain.com/products/sportsbook-software/), [SoftGamings — single BO](https://www.softgamings.com/blog/sportsbooks-unique-business-tool-a-single-back-office-for-all-clients/), [igamingplatformproviders review](https://igamingplatformproviders.com/review/digitain)

### 2.5. EveryMatrix OddsMatrix
- **მოდულები:** payout (margin) control **sport-დან market-მდე**; **„follow competitor payout"** — ავტომატურად მიჰყვება კონკურენტის მარჟას შერჩეულ მარკეტებზე; outcome-ზე manual tweak **ფიქსირებული probability offset-ით, რომელიც ბაზრის მოძრაობას მიჰყვება**; **manual bet acceptance configurable limits-ით „valuable punters"-ისთვის**; 24/7 Managed Trading; omnichannel retail.
- **გაკვეთილი:** manual odds ორი სახის უნდა იყოს: (a) **absolute override** (ფასი იყინება, feed აღარ ცვლის — საშიში), (b) **relative offset** (feed-ს მიჰყვება +Δ probability/odds). (b) — ნაგულისხმევი.
- წყარო: [OddsMatrix sportsbook](https://everymatrix.com/oddsmatrix/sportsbook/), [OddsMatrix](https://oddsmatrix.com/), [Omnichannel](https://everymatrix.com/oddsmatrix/omnichannel/)

### 2.6. Sportingtech (Portugal/UK, ძლიერი ბრაზილიაში)
- **მოდულები:** BO — sportsbook odds, risk, casino content, campaigns, player engagement; **controllable risk** — limits, exposure monitoring, dynamic risk settings; **custom alerts** (performance thresholds, fraud risk, player activity spikes); **risk profiles, liability dashboards, automated rules**; sports/competition visibility control.
- **გაკვეთილი:** NOTIF ცალკე მოდულად — „threshold alerts" ოპერატორის მიერ კონფიგურირებადი.
- წყარო: [Back office](https://sportingtech.com/platform/back-office/), [Sportingtech](https://sportingtech.com/)

### 2.7. BtoBet (Neuron / Neuron3)
- **მოდულები:** pre-match, live, virtual, **jackpot betting**; real-time მონიტორინგი key player info-თ; predefined + custom reports; **ავტომატური ბეთორების სეგმენტაცია betting behaviour-ის მიხედვით**; risk parameters; AI pattern detection.
- **გამორჩეული:** risk management-ის **სამი მოდელი** — (1) ოპერატორის საკუთარი trading გუნდი, (2) „Neuron Trader" — vendor-ის გუნდი ოპერატორის სტრატეგიით, (3) hybrid support.
- **გაკვეთილი:** ჩვენი BO უნდა იყოს გამოსადეგი **ორივე** რეჟიმში — ოპერატორის ტრეიდერი და ჩვენი (platform) ტრეიდერი ოპერატორის სახელით (RBAC: platform user „acting for operator").
- წყარო: [Neuron sports platform](https://archive.btobet.com/en/products/neuron-sports-platform), [Sportsbook](https://archive.btobet.com/en/sportsbook), [Neuron3](https://www.btobet.com/en/neuron3)

### 2.8. OpenBet (Endeavor → Light & Wonder?) ⚠ (მფლობელობა გადაამოწმეთ)
- **მოდულები:** Trading Tools — **Risk Manager** (real-time risk, multiple books one view), **Global Tickers**, **Global Risk** (cross-operator activity trace), **liability alerts**; Managed Trading — liability monitoring, limit setting, **customer profiling**.
- **გაკვეთილი:** „ticker" (ბეთების ცოცხალი ნაკადი ფილტრებით) — ტრეიდერის მთავარი ეკრანი. Platform-level „Global Risk" = ჩვენი უპირატესობა multi-tenant-ში: sharp მოთამაშე, რომელიც რამდენიმე ოპერატორთან თამაშობს (⚠ პერსონალური მონაცემების/კონტრაქტის შეზღუდვები — იხ. §6).
- წყარო: [Trading tools](https://www.openbet.com/products/trade/trading-tools), [Managed Trading](https://www.openbet.com/products/trade/managed-trading-services)

### 2.9. Amelco (UK — ძლიერი racing-ში)
- **მოდულები:** feed manager, bonus campaigns, modular; **real-time risk** streamed prices-ით და **expected P&L**-ით; ეკრანები: P&L, exposure, **running-up money** (multiples-ის შემდეგ ფეხზე გადასული თანხა), **cashout P&L**, current/average price; automated alerts; custom risk parameters.
- **გაკვეთილი:** liability-ის ეკრანზე არ კმარა „stake sum" — საჭიროა **worst-case payout per outcome**, **running-up money** აკუმულატორებისთვის და cash-out-ის P&L.
- წყარო: [Risk management tools](https://www.amelco.co.uk/solutions/risk-management-tools), [Sportsbook](https://www.amelco.co.uk/solutions/sportsbook), [Managed services](https://amelco.co.uk/managed-services/)

### 2.10. Delasport (Bulgaria)
- **მოდულები:** sportsbook management, risk tool, CRM, payment system, **anti-fraud**, marketing; player tracking & segmentation; campaigns/promotions; **bonus engine**; CRM+CMS; real-time BI; **agent** მოდელი (incoming bets monitoring for agents, balance & credit control) — აზიური/retail-agent ბაზრებისთვის.
- წყარო: [Sportsbook solution](https://www.delasport.com/sportsbook-solution/), [casinnovate review](https://www.casinnovate.com/software-reviews/delasport/)

### 2.11. GiG SportX (Malta)
- **მოდულები:** precision trading, omnichannel, **GiG Tracer** analytics (margins/markets/events real-time), 24/7 trading; **no-code rules engine** ბიზნეს-ლოგიკისთვის; bet builder, cash-out.
- **გაკვეთილი:** „rules engine" — LIM/PROMO/NOTIF-ის წესები (if/then) კონფიგურაციით და არა კოდით. ჩვენთან P1-ში — მარტივი rule DSL (jsonb).
- წყარო: [SportX](https://www.gig.com/products/sport-x/), [GiG](https://www.gig.com/)

### 2.12. Betby (Malta/Cyprus)
- **მოდულები:** სრული managed operations — risk, trading, **client segmentation**, reporting; one wallet / one reporting; AI/ML risk automation, suspicious activity; **multi-match bet builder** (ბაზარზე იშვიათი).
- წყარო: [Betby sportsbook](https://betby.com/en/sportsbook/), [Gambling Insider profile](https://www.gamblinginsider.com/magazine/978/company-profile-betby-3)

### 2.13. Playtech BGT Sports
- **მოდულები:** fully managed trading (130+ traders/settlers 24/7), retail estate — **tills (cashier), SSBT**, odds walls, customer card, **BetTracker** (retail ticket tracking app), retail cash-out.
- **გაკვეთილი:** საქართველოში retail (ფიზიკური bet-shop-ები/სლოტ-კლუბები) დიდია ⚠ — retail/cashier მოდული P2 დიფერენციატორია.
- წყარო: [Playtech Sports](https://www.playtech.com/products/sports/), [BetTracker](https://www.gamingintelligence.com/business/43637-playtech-bgt-sports-rolls-out-bettracker-product-with-uk-bookmakers), [Retail cash-out](https://calvinayre.com/2018/03/21/press-releases/playtech-bgt-sports-unveils-retail-cash-improvements)

### 2.14. SBTech (→ DraftKings, 2020)
- **მოდულები:** live reporting, real-time tools, segmentation; performance dashboards; breakdown **per sport/league/country/customer/VIP level/risk level**; შიდა CRM widgets-ისთვის/personalisation; time- და risk-based controls, auto-adjusting limits/payouts; Bet Builder („thousands of combinations").
- წყარო: [casinnovate SBTech review](https://www.casinnovate.com/software-reviews/sbtech/)

### 2.15. GR8 Tech (Ukraine/Cyprus — რეგიონული კონკურენტი)
- **მოდულები:** AI risk core — **arbitrage betting, bonus abuse, multi-accounting**, suspicious payments; **automated player risk segmentation + real-time bet scoring**; dynamic player-level restrictions; risk-based bet acceptance; BO-ში risk score, history, activity logs; CRM bonus orchestration (Free Bets, Cashback, Deposit bonus); customizable cashout engine; iFrame integration; Managed Trading (2026).
- წყარო: [GR8 Sportsbook](https://gr8.tech/sportsbook/), [Risk playbook](https://gr8.tech/blog/sportsbook-risk-management/), [MTS launch](https://www.yogonet.com/international/news/2026/08/17/125888-gr8_tech-launches-managed-trading-services-for-sportsbook-operators)

### 2.16. Sportradar (როგორც tool-provider: MTS, Alpha Odds, Insight Tech, Ctrl)
> ⚠ მხოლოდ საჯარო მარკეტინგული აღწერები; proprietary მასალა რეპოში არ ჩაიდება.
- **MTS (Managed Trading Services):** თითოეული ticket მოწმდება live probability-სა და ოპერატორის limits-ზე; acceptance ითვალისწინებს მიმდინარე liability-ს; **counter-offer** უარყოფის ნაცვლად; **Customer Confidence Factor** (ავტომატური risk-პროფილი, ტრეიდერს შეუძლია override); **live bet delay** — apply/skip/increase რეკომენდაციით; **Cashout Engine** (full + partial, margin control event/market/segment-ზე).
- **Insight Tech:** „ticket-inform" — ოპერატორი აგზავნის უკვე მიღებულ tickets → player profiling სიგნალები.
- **Alpha Odds:** liability-/turnover-aware ფასის skew.
- **Betradar Ctrl:** ⚠ Sportradar-ის operator-facing odds/offer მართვის ინსტრუმენტი (margins, offer selection, monitoring — average odds vs competitors, alert score, odds movement coloring).
- **გაკვეთილი ჩვენთვის:** ოპერატორს შეიძლება ჰქონდეს MTS — ჩვენი BET pipeline უნდა ჰქონდეს **pluggable external acceptance step** (`INT` → MTS ticket submit) და შიდა acceptance — როცა MTS არ არის. ⚠ კონტრაქტული საკითხი ოპერატორის Sportradar ხელშეკრულებაში.
- წყარო: [Trading & Risk](https://sportradar.com/betting-gaming/sportsbook/trading-risk/), [MTS](https://sportradar.com/betting-gaming/sportsbook/trading-risk/managed-trading-services/), [MTS brochure (PDF)](https://sportradar.com/wp-content/uploads/2022/02/Betradar_MTS_brochure_english.pdf), [Cashout Engine docs](https://docs.sportradar.com/mts/features/cashout-engine), [Alpha Odds](https://betradar.com/betting-services/alpha-odds/), [Betradar Ctrl](https://betradar.com/betting-services/pre-match-odds-service/betradar-ctrl/)

---

## 3. კონსოლიდირებული მოდულების ტაქსონომია

**სიხშირე:** **MH** = must-have (≈ყველა vendor-ს აქვს; ოპერატორი ამის გარეშე არ იყიდის) · **C** = common (უმეტესობას აქვს) · **D** = differentiator (რამდენიმე ლიდერს).
**Vendor აბრევიატურები:** AL=Altenar, BC=BetConstruct, KA=Kambi, DG=Digitain, OM=OddsMatrix, ST=Sportingtech, BT=BtoBet, OB=OpenBet, AM=Amelco, DS=Delasport, GI=GiG, BB=Betby, PT=Playtech, SB=SBTech, G8=GR8 Tech, SR=Sportradar tools. „all" = ყველა, ვისაც სერიოზული BO აქვს. ⚠ vendor-ების ჩამონათვალი ეფუძნება საჯარო აღწერებს — „არ ჩანს" ≠ „არ აქვს".

| კოდი | მოდული | ტიპური ქვე-ფიჩერები | Vendor-ები (საჯაროდ ჩანს) | სიხშირე |
|---|---|---|---|---|
| CAT | Offering / catalog management | sport/category/tournament/event/market ხე; visibility on/off; ordering/priority; **manual events & markets**; participant data (logos, short names); event merge/mapping fix; outright-ები | all; AL (Event View), ST, KA | **MH** |
| CAT | Featured / homepage layout | top events, featured leagues, banners, „popular bets", sport menu ordering, per-language/per-device | KA (content tools), DG/DS (CMS), SB (widgets) | **C** |
| I18N | Translations | entity names per language, override feed names, market/outcome templates (`{$competitor1}`), fallback | all (multi-language claim), DS | **MH** |
| ODDS | Margin / payout control | margin per sport→market, live vs prematch, per-segment margin; ladder/rounding; min/max odds | all; OM (sport→market), KA (price differentiation) | **MH** |
| ODDS | Manual odds | absolute override, **relative offset that follows feed**, suspend/unsuspend outcome | AL, OM, DG | **MH** |
| ODDS | Competitor-following / smart pricing | follow competitor margin, liability-aware skew | OM, SR (Alpha Odds) | **D** |
| CFG | Hierarchical config / templates | settings per scope, **trading templates/presets**, bulk apply | AL (templates), all implicitly | **MH** |
| LIM | Stake/payout limits | max stake/payout per market/event/ticket, min stake, max odds, max selections, per-currency | all | **MH** |
| LIM | Customer risk profiling | risk groups (VIP/normal/sharp/arber/bonus-hunter), **stake factor / confidence factor**, auto-segmentation, manual override | SR (CCF), BC, BT, SB, G8, ST, OB | **MH** |
| LIM | Liability / exposure monitoring | worst-case per outcome, per event, running-up money, P&L, cash-out P&L, thresholds | AM, OB, ST, DG, SR | **MH** |
| LIM | Bet ticker | live stream of tickets with filters (customer group, stake>X, sport), drill-down | OB (Global Ticker), BC, BT | **C** |
| BET | Bet acceptance rules | live **bet delay** (per sport/tournament/group), odds-change acceptance (none/higher/any), **manual referral / trader approval**, **counter-offer**, late-bet check, correlation rules for multiples | SR (MTS), OM (manual acceptance), BC (time check) | **MH** |
| BET | Ticket search & detail | by id/customer/event/status/date/stake; ticket timeline (placed→accepted→settled→resettled) | all | **MH** |
| BET | Settlement & resettlement | auto from feed, **manual settle**, void/cancel, resettle after correction, partial (dead heat), bulk by market | all (PT „settlers"), AM | **MH** |
| BET | Bet types | singles, multiples, systems, **bet builder (same game)**, multi-match bet builder | all (singles/multi/system); BB builder (KA, AL, SB, GI) | MH / **C** (builder) / **D** (multi-match) |
| CASH | Cash-out control | enable per sport/category/market/customer group, cash-out margin, **partial**, **auto cash-out**, suspension during danger, cash-out P&L | all; SR Cashout Engine, PT retail | **MH** (full) / **C** (partial/auto) |
| CUS | Customer management | mirror of PAM player, bet history, P&L per customer, notes, flags, risk group, per-customer limits, block betting/market types | all | **MH** |
| CUS | Fraud / abuse detection | **multi-account**, **arbitrage**, **bonus abuse**, late/ghost betting, syndicates, device/IP links | G8, BC, DS (anti-fraud), BB (AI), AL | **C** (rule-based) / **D** (ML) |
| CUS | Responsible gambling | deposit/loss/stake/session limits, self-exclusion, cool-off, reality check, **regulatory ban lists** | all (regulated markets); ⚠ ხშირად PAM-ის მხარეს | **MH** (regulated) |
| CMS | Messages / content | error/rejection messages, terms, rules pages, banners, push texts, per language | DG, DS (CMS) | **C** |
| REP | Reporting & BI | GGR/NGR, turnover, margin realised, by sport/league/market/customer/segment; KPI dashboard; scheduled exports; **mobile BO** | all; BC (KPI dashboard, mobile), SB, GI (Tracer) | **MH** |
| REP | Regulatory reporting | regulator exports, tax calc, monitoring-system feed | all (regulated); ⚠ market-specific | **MH** (regulated) |
| ADM | Admin users / RBAC / audit | roles per module, operator-scoped users, 2FA, **audit log** of every change, IP restriction | all (implicit) | **MH** |
| PROMO | Bonus engine | **freebets** (stake-not-returned), **odds/price boosts** (normal/0%/super), **acca boost** (% per # legs), **acca insurance**, cashback, **early payout / 2Up**, bore draw, risk-free bet, deposit bonus (PAM), wagering/rollover | AL, KA, DS, G8, AM, BT | **MH** (freebet) / **C** (boosts, acca) / **D** (early payout) |
| PROMO | Campaign builder / CRM | segment targeting, triggers (registration, first bet, event), schedules, budget caps, A/B | DG, DS, G8, SB, KA | **C** |
| PROMO | Jackpot / pools / tournaments | sport jackpot (13/15 matches), leaderboards, predictors | BT (jackpot), BC ⚠ | **D** |
| INT | PAM / wallet | auth/session, reserve/debit/credit/rollback, player status, limits sync | all (iFrame/API vendors: BB, G8, AL) | **MH** |
| INT | Affiliate | btag tracking, revenue-share reports | DG, DS | **C** (ხშირად PAM/გარე) |
| NOTIF | Alerts | liability threshold, big bet, odds gap vs market, feed down/producer down, settlement backlog, customer spike; channels: BO toast, email, Telegram/Slack | ST (custom alerts), AM, OB | **C** |
| RET | Retail / cashier / SSBT | shop & terminal hierarchy, cashier sessions, ticket print/barcode, payout at shop, agent credit | PT, AL, DS (agents), OM | **D** (ჩვენთვის P2) |

---

## 4. Gap analysis

### 4.1. მომხმარებლის მოთხოვნები, რომლებიც ინდუსტრიის სტანდარტია (✅ = ემთხვევა)

| მოთხოვნა | მოდული | შეფასება | შენიშვნა / რა უნდა დაემატოს, რომ „სტანდარტული" იყოს |
|---|---|---|---|
| category management | CAT | ✅ MH | + visibility/order per operator, + featured |
| event management | CAT | ✅ MH | + event status override (suspend/hide), manual event (provider `manual` row in `sb`), merge/duplicate fix |
| participant management (translations, icons/logos) | CAT/I18N | ✅ MH | logos — ⚠ ლიცენზირება (club crest-ები სავაჭრო ნიშანია); CDN + upload |
| manual translation entry | I18N | ✅ MH | + bulk import/export (CSV/XLIFF), missing-translation report |
| odds management | ODDS | ✅ MH | **გამოყავით:** margin (ზოგადი) vs manual odds (წერტილოვანი); relative offset როგორც default |
| manual markets on event / events into categories | CAT | ✅ C | manual market-ს **აუცილებლად** სჭირდება manual settlement (იხ. 4.2 #4) |
| limits on markets and events | LIM | ✅ MH | + ticket-level (max payout per ticket), customer axis (stake factor) |
| config at sport/country/league/market level | CFG | ✅ MH | + templates/presets, bulk apply, effective-value preview („რატომ არის ეს მნიშვნელობა" — inheritance trace) |
| CMS for error messages | CMS | ✅ C | + rejection reason codes → per-language ტექსტები; terms/rules pages |
| bet ticket search | BET | ✅ MH | + ticket detail timeline, + ticker (live) |
| statistics module / reports / reporting | REP | ✅ MH | გაყავით: operational dashboard (real-time) vs financial reports (T+1) vs regulatory |
| customer management + detail page with limits & restrictions | CUS | ✅ MH | + risk group, stake factor, notes, flags, linked accounts, RG status (PAM-იდან) |
| cash-out control page (per market/category) | CASH | ✅ MH | + per customer group, cash-out margin, partial/auto (P1), suspension rules |
| admin user management | ADM | ✅ MH | + **audit log viewer**, 2FA, IP allowlist |
| marketing campaign builder + freebet granting | PROMO | ✅ MH (freebet) / C (builder) | ⚠ საქართველოში რეკლამის/წახალისების შეზღუდვები — იხ. §5 |

### 4.2. რა გამოგრჩათ (სტანდარტული ან რეკომენდებული) — პრიორიტეტით

| # | ფიჩერი | მოდული | პრიორიტეტი | რატომ |
|---|---|---|---|---|
| 1 | **Bet acceptance rules**: live bet delay (sport/tournament/customer group), odds-change policy (reject / accept higher / accept any), bet_stop/producer-down → reject, max odds age | BET/CFG | **P0** | ამის გარეშე live betting = ფულის დაკარგვა (courtsiding, stale odds). ყველა vendor-ს აქვს; MTS-ის ბირთვია. |
| 2 | **Customer risk groups + stake factor** (e.g. 0.01–5.0) — effective limit = scope limit × customer factor; ავტო-შეთავაზება (rule-based P0, ML P2) | LIM/CUS | **P0** | ერთადერთი რეალური ინსტრუმენტი sharp/arber-ების წინააღმდეგ; brief-ის „customer axis min()" ამას ეფუძნება. |
| 3 | **Liability / exposure monitor** — worst-case payout per outcome/market/event, top exposures, per-operator; thresholds → auto-suspend ან alert | LIM/NOTIF | **P0** (ბაზისური) / P1 (running-up money, multiples) | ოპერატორი იღებს რისკს; vendor-ს, რომელსაც liability ეკრანი არ აქვს, ტრეიდერი არ აირჩევს. |
| 4 | **Manual settlement / resettlement / void** — market & ticket level, reason codes, feed rollback-ის დამუშავება, resettlement-ის wallet-ეფექტი (debit back) | BET/INT | **P0** | manual markets/events-ს სხვა გზით ვერ დასეტლავთ; UOF `rollback_bet_settlement`/`bet_cancel` (V00x `rollback`) უკვე გვაქვს — BO-ში სჭირდება ხილვადობა და ხელით override. |
| 5 | **Manual referral / trader approval queue** (ticket > X ან risk group = sharp → „pending", ტრეიდერი accept / reject / **counter-offer**-ს აკეთებს N წამში) | BET | P1 | OddsMatrix/MTS ამას ყიდის; VIP/high-roller ოპერატორებისთვის მნიშვნელოვანი. |
| 6 | **Audit log viewer** (who/what/before/after/reason) + mandatory reason ფულზე/limit-ზე მოქმედ ცვლილებებზე | ADM | **P0** | brief-ში ჩანაწერი არის, UI — არა; რეგულატორი/ოპერატორის compliance ითხოვს. |
| 7 | **Responsible gambling & regulatory ban-list** enforcement (PAM-დან status, ჩვენი მხრიდან double-check bet placement-ზე), ასაკი 25+ (GE) | CUS/INT | **P0 (GE)** | §5 — კანონით სავალდებულო საქართველოში. |
| 8 | **Regulatory & tax reporting** (turnover/GGR per period, winnings-withdrawal tax ⚠ PAM-ის მხარე, monitoring-system export) | REP | **P0 (GE)** / P1 სხვა ბაზრები | ოპერატორი Revenue Service-ის წინაშე ანგარიშვალდებულია; ბეთების მონაცემი ჩვენთანაა. |
| 9 | **Alerts / notifications (NOTIF)** — big bet, liability threshold, producer down/feed delay, settlement backlog, odds deviation, unmapped events | NOTIF | **P0** (ბაზისური 5 alert) / P1 (rule builder, Telegram) | ტრეიდერი 24/7 ეკრანს ვერ უყურებს; Sportingtech/Amelco/OpenBet ამას ხაზს უსვამს. |
| 10 | **Bet ticker** (real-time bets stream, ფილტრები) | BET/LIM | P1 | ტრეიდერის „მთავარი ეკრანი"; ticket search-ის real-time ვერსია. |
| 11 | **Multi-currency & money rules** — limits per currency (ან base currency + FX), rounding, display | CFG/LIM | **P0** (მინ. GEL + 1 სხვა) | რეგიონი: GEL, USD, EUR, AZN, AMD, TRY, KZT… limits ერთ ვალუტაში ბაგების წყაროა. |
| 12 | **Settlement monitoring** — unsettled markets after event end, feed-vs-manual discrepancy, rollback log | BET/REP | P1 | UOF-ში settlement ზოგჯერ იგვიანებს/ბრუნდება; ოპერატორის მოთამაშეები ჩივიან „ბეთი არ დაითვალა". |
| 13 | **Bet builder (same-game)** | BET/ODDS | P1 | Sportradar-ს აქვს Custom Bet (UOF CustomBet API ⚠ ცალკე ლიცენზია); ბაზარზე მოსალოდნელია. |
| 14 | **Odds / price boosts** (normal / 0% / super) + **acca boost** (% per legs) + **acca insurance** | PROMO/ODDS | P1 | საქართველოში ყველაზე პოპულარული აქციების ტიპი ⚠ (რეკლამის აკრძალვის ფონზე — მხოლოდ შიდა საიტზე ჩვენება). |
| 15 | **Early payout (2Up-ის მსგავსი)** | PROMO/BET | P2 | დიფერენციატორი; რთული settlement ლოგიკა. |
| 16 | **Bonus abuse / arbitrage / multi-account detection** (rule-based: odds-gap vs market avg, only-max-odds-bets, freebet-only players, shared device/IP ⚠ PAM data) | CUS/LIM | P1 (rules) / P2 (ML) | GR8 Tech, BetConstruct ამას ყიდის; freebet-ების გაცემისთანავე აბიუზი იწყება. |
| 17 | **Featured events / homepage layout / sport menu ordering** | CAT/CMS | P1 | ოპერატორის მარკეტინგ-გუნდის ყოველდღიური ინსტრუმენტი; front-end-ს (თუ ჩვენ ვაწვდით) სჭირდება. |
| 18 | **Settings templates/presets** („Top football", „Low-tier league", „Esports") | CFG | P1 | Altenar „trading templates" — ათასობით ლიგის ხელით კონფიგი შეუძლებელია. |
| 19 | **Correlation / combo rules** (same-event selections in multi forbidden, max legs, related outcomes) | BET/CFG | **P0** | bet builder-ის გარეშე same-event multi = უფასო ფული მოთამაშისთვის. |
| 20 | **Customer-level betting restrictions** (block live, block market types, max odds, cash-out disabled, freebet-only) | CUS/LIM | **P0** | brief-ში „restrictions" ჩანს — დააზუსტეთ ეს სია. |
| 21 | **Effective-config inspector** („რა margin/limit/delay ვრცელდება ამ market-ზე და საიდან") | CFG | **P0** | scope hierarchy-ის გარეშე debug შეუძლებელია; ოპერატორის support კითხვების 50% ⚠ (შეფასება). |
| 22 | **Platform (cross-operator) views** — global ticker, cross-operator sharp detection, feed health | ADM/REP | P1 | Digitain/OpenBet „Global Risk"; ჩვენ, როგორც multi-tenant, ბუნებრივად ვფლობთ. ⚠ ოპერატორებს შორის მონაცემთა გაზიარება — კონტრაქტით. |
| 23 | **Scheduled report exports & API** (CSV/XLSX, SFTP/email, BI-ში DWH ექსპორტი) | REP | P1 | ოპერატორებს აქვთ საკუთარი DWH (Power BI/Metabase). |
| 24 | **Mobile-friendly BO** (მინ. dashboard, ticker, alerts, ticket search) | REP/NOTIF | P1 | BetConstruct რეგიონში ამას აქცენტირებს. |
| 25 | **Affiliate reporting** | INT/REP | P2 | ხშირად PAM-ის/გარე პლატფორმის (Income Access, MyAffiliates) საქმეა; ჩვენ — მხოლოდ bet-level export btag-ით. |
| 26 | **A/B testing** (promo variants, layout) | PROMO/CMS | P2 | ნიშა; campaign builder-ის „control group" P1-ში საკმარისია. |
| 27 | **Retail / cashier / SSBT** | RET | P2 (ან ოპერატორის მოთხოვნით) | საქართველოში retail-ი არსებობს ⚠, მაგრამ ჩვენი პირველი პროდუქტი ონლაინია. |
| 28 | **Jackpot / predictor / leaderboards** | PROMO | P2 | BtoBet-ის ტიპის ნიშა; ფეხბურთის „ტოტო" რეგიონში პოპულარულია ⚠. |
| 29 | **Event/market comments & trader notes, shift handover log** | ADM/CAT | P1 | trading გუნდის ოპერაციული ჰიგიენა; იაფია. |
| 30 | **Data retention & GDPR/PDP tools** (export/erase player data request) | ADM/CUS | P1 | საქართველოს PDP კანონი (2024) + EU ოპერატორები. |

### 4.3. რეკომენდებული ფაზირება (შეჯამება)

- **P0 (პილოტი):** CAT (incl. manual event/market), I18N, CFG (+ inspector), ODDS (margin + manual offset), LIM (scope limits + customer stake factor + risk group + ბაზისური liability), BET (acceptance rules: delay, odds-change policy, combo rules; ticket search/detail; manual settle/void/resettle), CASH (enable/disable per scope & customer group + cash-out margin), CUS (mirror + restrictions + ban/RG status + notes), CMS (rejection messages), REP (dashboard, GGR/turnover/margin, regulatory export GE), ADM (RBAC + audit viewer + 2FA), PROMO (freebet manual/segment grant), INT (PAM), NOTIF (5 alert-ი: big bet, liability threshold, producer down, unsettled after end, unmapped event).
- **P1:** ticker, referral queue/counter-offer, bet builder, boosts/acca boost/insurance, campaign builder, abuse detection rules, featured/homepage, templates, partial/auto cash-out, settlement monitoring, scheduled exports, mobile BO, platform cross-operator views, PDP tools.
- **P2:** ML risk scoring, early payout, multi-match builder, jackpot/pools, retail/SSBT, A/B, affiliate, competitor-following pricing.

---

## 5. საქართველოს რეგულაცია

> ⚖️ **DISCLAIMER:** არ არის იურიდიული კონსულტაცია. კანონი — „ლატარიების, აზარტული და სხვა მომგებიანი თამაშობების მოწყობის შესახებ" (Law of Georgia on Organising Lotteries, Games of Chance and Other Prize Games). ცვლილებები: დეკემბერი 2021 (ძალაში ძირითადად 2022-03-01), 2024 (გადასახადები, ძალაში 2024-07-01), 2024-12-01 (ონლაინ ჩარჩო, ორი დომენი), 2025 (Revenue Service-ის ზედამხედველობის როლის გაძლიერება), 2026 (საერთაშორისო ლიცენზია, ჯარიმები). ზუსტი მუხლები — `matsne.gov.ge`-ზე იურისტთან ერთად.
> ⚠ **შეუსაბამობა 01-თან:** [01 §7.2](01-roadmap-and-access.md) ასაკ 25+-ს და რეკლამის აკრძალვას 2024 რეფორმად აღწერს; მედია-წყაროების მიხედვით ეს **2021-12-ში მიღებული და 2022-03-01-დან მოქმედი** ცვლილებებია, 2024-ში კი გადასახადები გაიზარდა. 01 უნდა გასწორდეს.

### 5.1. ძირითადი ფაქტები (წყაროებით)

| თემა | რას ამბობს წყარო | სანდოობა |
|---|---|---|
| **მინიმალური ასაკი** | 25 წელი ყველა აზარტულ თამაშზე (ონლაინ და ხმელეთზე); მანამდე 18 (ონლაინ/სლოტები) / 21 (კაზინო) | მაღალი (მრავალი წყარო) |
| **რეკლამის აკრძალვა** | 2022-03-01-დან ფაქტობრივად სრული აკრძალვა: TV, ქართული ვებსაიტები, outdoor, ონლაინ; გამონაკლისი — sponsorship ბანერები სპორტულ ღონისძიებებზე და სპორტსმენების ფორმაზე. ჯარიმა ~$3,200 (განმეორებისას ორმაგი) ⚠ | მაღალი (ჯარიმის თანხა ⚠) |
| **აკრძალულ პირთა რეესტრი (exclusion registry)** | შემოსავლების სამსახური მართავს; მასში ავტომატურად შედიან: **საჯარო მოხელეები**, **სოციალურად დაუცველები** (სოც. დახმარების მიმღებები), სასამართლოს გადაწყვეტილებით შეზღუდულები (ოჯახის წევრის მოთხოვნით, დამოკიდებულება), **თვითშეზღუდვა** (self-exclusion, rs.ge / videocall.rs.ge-ით); ⚠ ზოგი წყარო ასევე ასახელებს ნასამართლევ პირებს. 2025 ბოლოს ~1.58 მლნ პირი, მათ შორის ~36k ნებაყოფლობითი | მაღალი (კატეგორიები ⚠ ზუსტი სია — კანონში) |
| **უცხოური საიტები** | საქართველოს რეზიდენტებისთვის უცხოურ (არალიცენზირებულ) საიტზე რეგისტრაცია აკრძალულია; ქართული ბანკის ბარათით უცხოურ გამბლინგზე გადახდა აკრძალულია | საშუალო |
| **ორი დომენი (2024-12-01-დან)** | ერთი ლიცენზია — ≤2 დომენი: ერთი ქართველ მოთამაშეებზე (25+, ადგილობრივი გადასახადები), მეორე — **მხოლოდ უცხოელებზე** (შეღავათიანი გადასახადი) | საშუალო ⚠ |
| **გადასახადები (ოპერატორის მხარე)** | GGR გადასახადი 10%→15% (2024-07-01); მოთამაშის თანხის გატანის გადასახადი 2%→6% ⚠ (სხვა წყარო: 5% withholding ⚠); ონლაინ sports betting — ⚠ **7% turnover-იდან** ქართველ მოთამაშეებზე vs 5% GGR უცხოელებზე (ერთი წყარო) | **დაბალი/წინააღმდეგობრივი** ⚠ — იურისტთან/ბუღალტერთან |
| **ზედამხედველობა** | Revenue Service — ნებართვები, ზედამხედველობა, რეესტრი; **ელექტრონული სათამაშო ბიზნესის მონიტორინგის სისტემა** — ოპერატორი Random Systems Georgia (RSG); სერვერების საქართველოში განთავსება და 24/7 ვიდეო-მეთვალყურეობა ⚠ (ერთი წყარო) | საშუალო ⚠ |
| **Bonus/promo** | საჯარო წყაროებში ცალსახა **ბონუსების აკრძალვა ვერ ვიპოვეთ** ⚠; მაგრამ რეკლამის აკრძალვა ართულებს გარე კომუნიკაციას (SMS/email/push-ის სტატუსი რეკლამად — ⚠ იურისტი) | **დაბალი** ⚠ |

### 5.2. რა უნდა უზრუნველყოს Back Office-მა / ძრავამ (requirement-ები)

| # | მოთხოვნა | სად | P | კომენტარი |
|---|---|---|---|---|
| GE-1 | **Bet placement-ზე მოთამაშის eligibility check**: age ≥ 25 (PAM-იდან `birth_date` ან `age_verified_25` flag), `excluded = false` (რეესტრის check) | INT/BET | P0 | PAM-ის კონტრაქტში სავალდებულო ველები. ჩვენ — **defense in depth**: ყოველ ticket-ზე ვამოწმებთ PAM-ის status-ს (cache ≤ N წთ) და ვინახავთ check-ის შედეგს ticket-ზე (`bet.ticket.eligibility_snapshot jsonb`). ⚠ რეესტრთან პირდაპირი ინტეგრაცია ოპერატორის (ლიცენზიატის) პასუხისმგებლობაა — ჩვენ ვიღებთ PAM-იდან. |
| GE-2 | **Exclusion-ის მყისიერი ეფექტი**: PAM-იდან `player.excluded`/`self_excluded` event → open session-ების გაუქმება, pending/referral tickets reject, cash-out-ის დაშვება? ⚠ (ღია ბეთები — როგორ? იურისტი) | INT/CUS | P0 | CUS detail-ზე ჩანს „Regulatory status" (read-only, PAM source). |
| GE-3 | **Operator-level market profile „GE-domestic" vs „GE-international"** — ორი დომენი = ორი brand/site ერთ operator-ზე, განსხვავებული წესებით (ასაკი, ვალუტა, პროდუქტი, ტაქსი) | CFG/ADM | P0 | მოდელი: `operator` → `brand/site` (⚠ brief-ში brand დონე არ არის — **რეკომენდაცია: დავამატოთ `site_id` scope ან operator=brand**). |
| GE-4 | **Regulatory reports**: turnover, payouts, GGR per day/month, per domain (domestic/foreign), per product (prematch/live), voided/resettled; ticket-level ექსპორტი მოთხოვნისას | REP | P0 | ფორმატი ⚠ — Revenue Service/RSG-ის სპეციფიკაცია მოსაპოვებელია ოპერატორისგან. დიზაინი: generic export engine + per-jurisdiction template. |
| GE-5 | **Monitoring-system integration hook** (real-time ან batch ticket/transaction გადაცემა RSG-ის სისტემაში) | INT/REP | P1 ⚠ | ⚠ უცნობია, ბეთების დონეზე სჭირდება თუ მხოლოდ ფინანსური. არქიტექტურა: NATS-ზე `bet.ticket.*` event-ები → per-jurisdiction exporter. |
| GE-6 | **Immutable bet & settlement history** (ticket-ის ცვლილება მხოლოდ append-only ისტორიით; resettlement ახალი ჩანაწერია) + audit log | BET/ADM | P0 | ნებისმიერი რეგულატორის ბაზისი; ⚠ შენახვის ვადა (5+ წელი?) — დაზუსტება. |
| GE-7 | **Data residency** — მონაცემების/სერვერის საქართველოში განთავსება ⚠ | INFRA | P0 ⚠ | თუ დადასტურდა: GE ოპერატორებისთვის ცალკე deployment/region (ჩვენი multi-tenant-ის „cell" მოდელი). დიდი არქიტექტურული გავლენა — **ადრე დავაზუსტოთ**. |
| GE-8 | **Promo compliance switch** — operator/site დონეზე `promo.*` ფიჩერების გამორთვა/შეზღუდვა (მაგ. გარე push/SMS კამპანიები, „welcome bonus" ბანერები) | PROMO/CFG | P0 | default GE-domestic: გარე კომუნიკაცია off ⚠, on-site freebet — on (ოპერატორის გადაწყვეტილებით, audit-ით). |
| GE-9 | **RG ინსტრუმენტები** — player limits (stake/loss/deposit/session) PAM-ში; ჩვენ ვასრულებთ **stake/loss limit-ს bet placement-ზე**, თუ PAM ამას გვაწვდის (`limits` endpoint) | INT/LIM | P1 | ⚠ საქართველოში ზუსტი RG ვალდებულებები (deposit limit სავალდებულო? reality check?) — დაზუსტება. |
| GE-10 | **Tax awareness** — ticket/payout-ზე ტაქსის გამოთვლა **არა** ჩვენი (PAM/ოპერატორი), მაგრამ REP-ში turnover/GGR სწორად გაყოფილი domestic/foreign | REP | P0 | ⚠ 7% turnover vs 15% GGR — ორივე მეტრიკა ანგარიშში. |
| GE-11 | **Georgian language** (`ka`) სრულად — BO UI, rejection messages, terms | I18N/CMS | P0 | ქართული ბაზრისთვის თავისთავად. |
| GE-12 | **B2B supplier obligations** — ⚠ ჩვენთვის შეიძლება საჭირო იყოს RSG-ის სერტიფიცირება/B2B permit (იხ. 01 §7.2) → BO-ს change management, version log, certified build-ები | ADM/INFRA | P0 ⚠ | ეს ზრდის audit/versioning მოთხოვნებს (release notes, ვერსიის hash). |

---

## 6. დიფერენციაციის იდეები

> კონტექსტი: რეგიონში ჭარბობს BetConstruct, Digitain (სომხეთი), GR8 Tech, Altenar, ადგილობრივი in-house სისტემები ⚠. ჩვენი პოზიცია — **„engine + BO, ოპერატორის PAM-ით და ოპერატორის Sportradar კონტრაქტით"** (BYO-PAM, BYO-feed). ეს ნიშნავს: ჩვენ არ ვეჯიბრებით turnkey-ს, ვეჯიბრებით **ხარისხს, სიჩქარეს, გამჭვირვალობას და ლოკალურ compliance-ს**.

1. **„Compliance-native for Georgia" out-of-the-box** — GE-domestic/international site profiles, 25+ & exclusion enforcement ყოველ ticket-ზე snapshot-ით, regulatory export-ები, audit — დემოზე ჩვენება „რეგულატორი მოვა — აი ეკრანი". ადგილობრივი ოპერატორებისთვის ეს ყველაზე ძლიერი გასაყიდი არგუმენტია (უცხოური vendor-ები ამას custom-ად აკეთებენ).
2. **Transparent configuration** — effective-config inspector („ეს margin 6.5% მოდის `tournament=UCL` override-იდან, დააყენა X-მა 2026-10-01-ს, მიზეზი: ..."), diff/preview **ცვლილების წინ** („ეს ცვლილება 1,240 market-ზე იმოქმედებს"), one-click rollback audit-იდან. Vendor-ების BO-ები ცნობილია როგორც „შავი ყუთი".
3. **Relative manual odds by default** (OddsMatrix-ის მიდგომა) + **„stale override" alert** (manual absolute ფასი, რომელიც feed-ს > X%-ით ჩამორჩება) — ტრეიდერის შეცდომების #1 წყაროს პრევენცია.
4. **Bring-your-own-MTS** — ერთი BET pipeline ორი რეჟიმით: შიდა acceptance (delay, limits, stake factor) ან Sportradar MTS (ოპერატორის კონტრაქტით) ან hybrid (შიდა წინასწარი ფილტრი → MTS). რეგიონულ vendor-ებს ეს ჩაკეტილი აქვთ.
5. **Real-time liability ყველა ფენაზე** — outcome/market/event/tournament/operator + multiples-ის „running-up money" + cash-out P&L, PostgreSQL + Valkey-ზე incremental aggregates; **auto-actions** (threshold → suspend market / reduce stake factor / referral).
6. **Platform-level intelligence (opt-in)** — cross-operator signals: „ეს ბეთის პატერნი სხვა ოპერატორებთან sharp-ად დაფიქსირდა" ანონიმიზებული hash-ით ⚠ (PDP/GDPR, ოპერატორების თანხმობა, კონტრაქტი). OpenBet „Global Risk"-ის ანალოგი მცირე ოპერატორებისთვის, რომლებსაც ტრეიდინგ-გუნდი არ ჰყავთ.
7. **Light managed trading** — ჩვენი პატარა 24/7 ⚠ (ან business-hours + alerts) risk-desk ოპერატორის სახელით (BtoBet-ის „სამი მოდელი"); BO-ში „acting-for-operator" რეჟიმი სრული audit-ით. ფასი — revenue share.
8. **Engagement without advertising** — რადგან გარე რეკლამა აკრძალულია, on-site retention ფიჩერები (odds boosts, acca boost, bet builder, early payout, personalised „for you" events) ქართველი ოპერატორისთვის ორმაგად ღირებულია ⚠ (იურიდიული შემოწმებით).
9. **Regional content** — ქართული/სომხური/აზერბაიჯანული/ყაზახური/თურქული ენები და ადგილობრივი ლიგები (Erovnuli Liga, რაგბი — საქართველოში პოპულარული, კალათბურთი, MMA/ჭიდაობა), **ქართული ტრანსლიტერაცია და მოთამაშეთა სახელები** ხარისხიანად (feed-ის ქართული თარგმანები სუსტია ⚠); manual events ადგილობრივ ღონისძიებებზე (manual settlement-ით).
10. **Developer-grade integration** — სუფთა PAM API კონტრაქტი (idempotency, OpenAPI, sandbox, simulator — ჩვენ უკვე გვაქვს feed simulator), webhooks/NATS event-ები ოპერატორის DWH-სთვის, BO-ს ყველა ფუნქცია API-ითაც (`/api/bo/...`) — ოპერატორს შეუძლია საკუთარი ავტომატიზაცია.
11. **Mobile-first BO for owners** — dashboard, alerts, ticker, approve/reject referral ტელეფონიდან (BetConstruct-ის მსგავსად) — მცირე ოპერატორის მფლობელი ხშირად თვითონაა ტრეიდერი.
12. **Fast onboarding** — „operator in a day": templates (league tiers, margin presets), import from CSV, default GE profile. გასაყიდი მეტრიკა: time-to-first-bet.

---

## 7. წყაროები

**Vendors**
- Altenar: [event trading tools](https://altenar.com/en-us/blog/sportsbook-features-guide-event-trading-tools/) · [risk support](https://altenar.com/en-us/blog/sportsbook-guide-risk-management-support/) · [bet boost](https://altenar.com/blog/sportsbook-features-guide-bet-boost-promo-tool/) · [early payout](https://altenar.com/blog/sportsbook-features-guide-early-payouts/) · [bonus system](https://altenar.com/blog/which-bonus-system-to-choose-for-your-sports-betting-businesses/) · [retail](https://altenar.com/en-us/services/sportsbook-retail-solution/)
- BetConstruct: [sportsbook](https://www.betconstruct.com/products/sportsbook) · [mobile BO](https://focusgn.com/betconstruct-optimizes-backoffice-for-mobile-use) · [risk tool (Yogonet 2025)](https://www.yogonet.com/international/news/2025/04/21/102276-betconstruct-highlights-realtime-risk-management-tool-to-enhance-sportsbook-security) · [risk services](https://www.betconstruct.com/services/risk-management)
- Kambi: [turnkey](https://www.kambi.com/what-we-do/turnkey-sportsbook/) · [bet builder](https://www.kambi.com/kambi-what-we-do-b2b-sports-betting-platform/bet-builder/) · [Odds Feed+](https://www.kambi.com/kambi-what-we-do-b2b-sports-betting-platform/odds-feed-plus/)
- Digitain: [sportsbook](https://www.digitain.com/products/sportsbook-software/) · [single BO (SoftGamings)](https://www.softgamings.com/blog/sportsbooks-unique-business-tool-a-single-back-office-for-all-clients/)
- EveryMatrix OddsMatrix: [sportsbook](https://everymatrix.com/oddsmatrix/sportsbook/) · [oddsmatrix.com](https://oddsmatrix.com/)
- Sportingtech: [back office](https://sportingtech.com/platform/back-office/)
- BtoBet: [Neuron](https://archive.btobet.com/en/products/neuron-sports-platform) · [sportsbook](https://archive.btobet.com/en/sportsbook)
- OpenBet: [trading tools](https://www.openbet.com/products/trade/trading-tools) · [managed trading](https://www.openbet.com/products/trade/managed-trading-services)
- Amelco: [risk tools](https://www.amelco.co.uk/solutions/risk-management-tools) · [managed services](https://amelco.co.uk/managed-services/)
- Delasport: [sportsbook](https://www.delasport.com/sportsbook-solution/)
- GiG: [SportX](https://www.gig.com/products/sport-x/)
- Betby: [sportsbook](https://betby.com/en/sportsbook/)
- Playtech: [sports](https://www.playtech.com/products/sports/) · [BetTracker](https://www.gamingintelligence.com/business/43637-playtech-bgt-sports-rolls-out-bettracker-product-with-uk-bookmakers)
- SBTech: [casinnovate review](https://www.casinnovate.com/software-reviews/sbtech/)
- GR8 Tech: [sportsbook](https://gr8.tech/sportsbook/) · [risk playbook](https://gr8.tech/blog/sportsbook-risk-management/)
- Sportradar: [trading & risk](https://sportradar.com/betting-gaming/sportsbook/trading-risk/) · [MTS](https://sportradar.com/betting-gaming/sportsbook/trading-risk/managed-trading-services/) · [Alpha Odds](https://betradar.com/betting-services/alpha-odds/) · [Betradar Ctrl](https://betradar.com/betting-services/pre-match-odds-service/betradar-ctrl/) · [Cashout Engine](https://docs.sportradar.com/mts/features/cashout-engine)

**საქართველო — რეგულაცია**
- [Yogonet 2021-12: bill — age, ads, taxes](https://www.yogonet.com/international/news/2021/12/23/60756-georgia-passes-bill-with-gambling-restrictions--higher-taxes-and-age-limit--advertising-ban)
- [JAMnews: advertising ban](https://jam-news.net/advertising-of-gambling-in-georgia/) · [Civil.ge: restrictions](https://civil.ge/archives/463807/amp) · [GGA: ads & sponsorship](https://www.gga.org.ge/en/news/129-new-restrictions-on-advertising-and-sponsorship-which-the-gambling-business-is-protesting-what-is-happening-in-georgia)
- [SBC News 2023: 1.5m banned](https://sbcnews.co.uk/uncategorized/2023/03/28/georgia-1-5m-citizens-ban/) · [SiGMA: 1.5m blocked](https://sigma.world/news/georgia-bans-million-citizens-gambling/) · [Gambling Insider: self-exclusion registry](https://www.gamblinginsider.com/news/30577/georgias-gambling-self-exclusion-registry-surpasses-30000-registrants)
- [Georgia Today 2024: oversight & taxes](https://georgiatoday.ge/georgia-boosts-gambling-oversight-and-taxes-in-2024/) · [NEXT.io: GGR tax hike](https://next.io/news/regulation/georgia-gambling-tax/) · [Andersen: gambling taxation](https://ge.andersen.com/georgia-gambling-taxation/) · [Andersen: foreign-player tax benefits](https://ge.andersen.com/georgia-online-gambling-tax-benefits/)
- [Legal500: Georgia gambling law](https://www.legal500.com/guides/chapter/georgia-gambling-law/) · [Revera: new licensing rules](https://www.revera.legal/en/news-and-analytical-materials/novye-pravila-licenzirovaniya-igornogo-biznesa-v-gruzii-chto-neobxodimo-znat/) · [4H agency guide 2025](https://4h.agency/everything-else/tpost/cf4ofse1c1-complete-guide-to-gambling-laws-licenses) · [GamblingMaps: operators](https://gamblingmaps.org/map/regulations/georgia/for-operators)
- [Wikipedia: Gambling in Georgia](https://en.wikipedia.org/wiki/Gambling_in_Georgia)
