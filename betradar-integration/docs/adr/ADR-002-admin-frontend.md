# ADR-002 — ადმინების frontend: Angular + Angular Material

- **სტატუსი:** მიღებულია · 2026-10-03
- **ცვლის:** docs/04 §4.2-ის რეკომენდაციას (React + Refine + Ant Design)

## კონტექსტი

გუნდს Angular სურს. ადმინი სამი იქნება (Feed Ops → Catalog/Trading → ოპერატორის back-office), ამიტომ საერთო კომპონენტები და ერთი workspace თავიდანვე სჭირდება.

## გადაწყვეტილება

1. **Angular 22** (standalone components, signals, zoneless, Vitest) — Angular CLI multi-project workspace `admin/admin-web`: აპლიკაცია `feed-ops` + ბიბლიოთეკა `@admin/ui` (საერთო კომპონენტები მომავალი ადმინებისთვის). Nx-ს ჯერ არ ვიყენებთ — CLI workspace საკმარისია; საჭიროებისას გადასვლა მარტივია.
2. **UI: Angular Material 3 + CDK (MIT).** PrimeNG განვიხილეთ და პირველ ვერსიაში გამოვიყენეთ, მაგრამ **PrimeNG 22 აღარ არის MIT** — „PrimeUI License“: უფასოა მხოლოდ ორგანიზაციისთვის, რომელსაც აქვს <$1M შემოსავალი, <5 დეველოპერი, <10 თანამშრომელი, <$3M ინვესტიცია; სჭირდება license key; ხოლო კომპონენტების მესამე მხარისთვის (ოპერატორების back-office) გავრცელება ცალკე **OEM ლიცენზიას** მოითხოვს. B2B კომპანიისთვის, რომელიც ზრდას გეგმავს, ეს რისკია → Angular Material (Google, MIT).
3. **ავტორიზაცია:** OpenID Connect (authorization code + PKCE) Keycloak-ით, `angular-auth-oidc-client` (MIT). API ამოწმებს JWT-ს (issuer + audience `admin-api`), როლები Keycloak realm role-ებიდან (`feedops-viewer`, `feedops-operator`). ოპერატორების back-office-ისთვის ცალკე realm იქნება.
4. **Live განახლებები:** Server-Sent Events (`/api/stream`), PostgreSQL `LISTEN/NOTIFY`-დან; ბრაუზერში `fetch`-ით (Authorization header-ისთვის; `EventSource` header-ს ვერ აგზავნის).
5. **Runtime config:** `/config.json` (nginx წერს env-დან) — ერთი build ყველა გარემოსთვის.
6. **ფონტები და აიკონები self-hosted** (`@fontsource/roboto`, Material Symbols) — CDN-ის გარეშე, იზოლირებულ ქსელშიც მუშაობს.

## შედეგები

- docs/04 §4.2 (React/Refine/AntD) ჩანაცვლებულია; არქიტექტურა (admin-api, Keycloak, SSE) უცვლელია.
- ცხრილებისთვის `mat-table`; თუ ოპერატორის back-office-ში ძალიან დიდი grid-ები დაგვჭირდება — AG Grid Community (MIT) ან CDK virtual scroll.
