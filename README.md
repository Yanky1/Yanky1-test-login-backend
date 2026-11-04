# Solvelt seller portal – backend

ASP.NET Core 10 API for seller sign-up / sign-in: signup wizard drafts, email verification, JWT + rotating refresh tokens,
lockout, passkeys (built-in Identity passkey support), Google/Apple sign-in, TOTP 2FA, seller approval status.
Optional **phone (IVR) and SMS channels with an AI assistant** (SignalWire/Twilio, Claude): `docs/PHONE-SMS.md`.
Seller **orders** (signed marketplace API, outbox, buyer messaging, simulator): `docs/ORDERS.md`.
Seller **services, calendars and bookings** (service listings, per-provider calendars, slot engine, bookings and quotes, ICS feeds): `docs/SERVICES.md`.
Seller **AI assistant** (Claude, server-side agent loop, read tools + confirm-to-act cards, SSE, budgets, kill switch): `docs/ASSISTANT.md`, evals `docs/ASSISTANT-EVALS.md`.
Seller **shipping & tracking** (Shippo labels behind `IShippingProvider`, rates, address validation, customs, void/refund into the ledger, live tracking webhooks + reconciler, prepaid return labels, staff tools): `docs/SHIPPING.md`.
Seller **payments & payouts** (Stripe Connect Express, double-entry ledger, release + transfer jobs, Stripe webhooks, staff tools): `docs/PAYMENTS.md`.
Seller **local delivery** with own drivers (zones, windows + capacity, dispatch board, Driver role, driver app API, proof of delivery, tracking, live ETA with consent): `docs/LOCAL-DELIVERY.md`.
**Staff admin panel** (staff roles, 2FA staff sessions, application review, seller/user tools, moderation, support lookups, integration health, audit, platform settings): `docs/ADMIN.md`.
Frontend: <https://github.com/Yanky1/test-login-frontend>.

## Prerequisites
.NET 10 SDK. Optional: PostgreSQL 15+ (production), a local SMTP catcher such as Mailpit (dev).

## Run locally
```bash
cd src/Solvelt.Api
dotnet user-secrets init
dotnet user-secrets set "Email:Provider" "Smtp"        # or "Sink" to keep mails in memory
dotnet run --urls https://localhost:5001               # SQLite at App_Data/solvelt.db, created automatically
```
Dev defaults: Turnstile Cloudflare **test** keys, ephemeral JWT key, frontend at `http://localhost:5173` (Vite proxies `/api` to the backend).
OpenAPI document: `/openapi/v1.json` (Development only). Health: `/health`.
Create the first admin by setting `AdminSeed:Email` / `AdminSeed:Password` (user-secrets / env) – the admin is created at startup if missing.

## Tests
```bash
dotnet test        # xUnit unit + WebApplicationFactory integration tests (SQLite, fake TimeProvider, scripted fake assistant model)
ANTHROPIC_API_KEY=… dotnet test tests/Solvelt.Api.Evals   # optional real-model assistant evals (not part of CI)
STRIPE_SECRET_KEY=sk_test_… dotnet test tests/Solvelt.Api.StripeTests   # optional Stripe TEST-mode tests (not part of CI)
```

## Configuration keys
See `appsettings.json` (defaults) and `appsettings.Example.json` / `.env.example` (placeholders). Sections: `Database`, `App`, `Jwt`, `Session`
(server idle limit), `Lockout`, `RateLimit` (incl. `TeamPerMinute`, `CatalogPerMinute`, `OrdersPerMinute`, `InternalPerMinute`, `BookingsPerMinute`, `AvailabilityPerMinute`), `Turnstile`, `Email`, `Passkeys`, `Storage`, `ExternalAuth`, `AdminSeed`, `Admin` (staff session/idle/step-up minutes, IP allowlist, admin rate limits: `docs/ADMIN.md`), `Passwords`, `DataProtection`, `Orders` (secrets, webhook, fee, retention: `docs/ORDERS.md`), `Shipping` (Shippo key, webhook secret, spend limit, label format, tracking intervals: `docs/SHIPPING.md`; also `RateLimit:ShippingPerMinute`), `Payments` + `Stripe` (release days, reserve, minimum transfer, keys: `docs/PAYMENTS.md`; also `RateLimit:PaymentsPerMinute`), `Bookings` (hold minutes, retention, default time zone: `docs/SERVICES.md`), `LocalDelivery` (proof URL key + minutes, location retention, ETA event interval: `docs/LOCAL-DELIVERY.md`; `LocalDelivery:ProofUrlKey` is required in production), `Phone`, `Ai`, `Assistant` (flag, kill switch, provider/model, budgets, retention: `docs/ASSISTANT.md`; also `RateLimit:AssistantPerMinute`).
The frontend idle timeout is separate (frontend `config.json`); keep `Session:IdleTimeoutMinutes` slightly larger than it.

## API (all under `/api/v1`)
`auth/*` signup (202, no session), verify-email (creates the session), resend-code, login, refresh, logout, logout-all, forgot/reset-password, passkey, external (Google/Apple) ·
`account/*` me, change-password, email change, 2FA, sessions, passkeys, linked logins · `seller/*` wizard + certificate ·
`account/phone*` number + PIN (step-up) · `channels/voice|sms|links` provider webhooks and texted links (signed; see `docs/PHONE-SMS.md`) · `session`, `session/account` (account switching) · `team/*`, `invitations/*`, `account/invitations`, `brands/*` (team members, roles, brands: see `docs/TEAMS.md`) · `seller/products*`, `seller/questions*`, `categories*` (catalog, images, variations, CSV, customer questions: see `docs/PRODUCTS.md`) · `admin/sellers/{id}/status` (legacy Admin role) · `admin/*` staff back-office (staff sessions only; see `docs/ADMIN.md`) · `public-config`. Testing/Development only: `/api/test/emails` (Testing), `/api/dev/questions`.
Login contract details and the failure `hint` field: `docs/DECISIONS.md`.

Orders: `seller/orders*`, `seller/messages*`, `seller/message-templates`, `seller/notification-preferences`, `seller/events` (SSE) · marketplace-only `/api/internal/v1/*` (HMAC-signed, never a seller token) ·
Services: `seller/services*`, `seller/providers*`, `seller/calendar`, `seller/bookings*`, `seller/quote-requests*`, public `calendar/feeds/{token}.ics`; marketplace availability/hold/booking/quote endpoints under `/api/internal/v1` (see `docs/SERVICES.md`).
Local delivery: `seller/delivery/*`, `seller/orders/{id}/local-delivery|tracking`, driver app `driver/*`, marketplace `POST /api/internal/v1/local-delivery/eligibility`, signed proof links `/api/public/delivery-proof/*`, staff `admin/sellers/{id}/local-delivery` (see `docs/LOCAL-DELIVERY.md`).
Shipping: `seller/shipping/*` (settings, addresses, packages, profiles, rates, labels, bulk, void, return labels, tracking timeline), provider webhook `POST /api/v1/shipping/webhooks/shippo`, marketplace `GET /api/internal/v1/shipping/settings`, staff `admin/shipping/*` (see `docs/SHIPPING.md`).
Payments: `seller/payments/*` (summary, ledger, transfers, payouts, disputes, statements + CSV, onboarding, dashboard-link), marketplace `POST /api/internal/v1/payment-events`, Stripe `POST /api/webhooks/stripe`, staff `admin/payments/*` (see `docs/PAYMENTS.md`).
Assistant: `assistant/config`, `assistant/conversations*`, `assistant/messages` (+ `/stream` SSE), `assistant/actions/{id}/confirm|cancel` (CSRF header required), `assistant/usage`.
Development/Testing only: `seller/dev/*` order and booking simulator, `/api/dev/outbox` sink (`scripts/post-signed.sh` signs real requests).

## Deploy notes
* PostgreSQL: set `Database:Provider=Postgres`; apply migrations with `dotnet ef database update` (or `Database:AutoMigrate=true` for a single instance).
* Shipping: set `SHIPPO_API_KEY` (live token in Production; Production refuses a test token) `Shipping:WebhookSecret` and `Shipping:LinkKey` (a separate random value, not an Orders secret), register `https://<host>/api/v1/shipping/webhooks/shippo?token=<secret>` for `track_updated`, set `Shipping:PublicBaseUrl`. Go-live checklist: `docs/SHIPPING.md`.
* Payments: set `STRIPE_SECRET_KEY` (live key in Production; Production refuses a test key) and `STRIPE_WEBHOOK_SECRET`, register `https://<host>/api/webhooks/stripe` (platform + Connect endpoints). Go-live checklist: `docs/PAYMENTS.md`.
* Required in Production: `Orders:InboundSecret` + `Orders:OutboundSecret` (different) and `Orders:WebhookUrl`, `App:HmacSecret`, `Jwt:PrivateKeyPem`, Turnstile keys, an email provider, `DataProtection:KeysPath` (reset links must survive restarts), shared file storage if >1 instance.
* Run behind TLS; set `App:TrustedProxies` to your reverse proxy so per-IP rate limits use the real client IP.
* Serve the SPA and API from the same site (e.g. `/` and `/api`) so the SameSite=Strict refresh cookie works. See `docs/SETUP-INTEGRATIONS.md`.
