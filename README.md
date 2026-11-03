# Solvelt seller portal – backend

ASP.NET Core 10 API for seller sign-up / sign-in: signup wizard drafts, email verification, JWT + rotating refresh tokens,
lockout, passkeys (built-in Identity passkey support), Google/Apple sign-in, TOTP 2FA, seller approval status.
Optional **phone (IVR) and SMS channels with an AI assistant** (SignalWire/Twilio, Claude): `docs/PHONE-SMS.md`.
Seller **orders** (signed marketplace API, outbox, buyer messaging, simulator): `docs/ORDERS.md`.
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
dotnet test        # xUnit unit + WebApplicationFactory integration tests (SQLite, fake TimeProvider)
```

## Configuration keys
See `appsettings.json` (defaults) and `appsettings.Example.json` / `.env.example` (placeholders). Sections: `Database`, `App`, `Jwt`, `Session`
(server idle limit), `Lockout`, `RateLimit` (incl. `TeamPerMinute`, `CatalogPerMinute`, `OrdersPerMinute`, `InternalPerMinute`), `Turnstile`, `Email`, `Passkeys`, `Storage`, `ExternalAuth`, `AdminSeed`, `Passwords`, `DataProtection`, `Orders` (secrets, webhook, fee, retention: `docs/ORDERS.md`), `Phone`, `Ai`.
The frontend idle timeout is separate (frontend `config.json`); keep `Session:IdleTimeoutMinutes` slightly larger than it.

## API (all under `/api/v1`)
`auth/*` signup (202, no session), verify-email (creates the session), resend-code, login, refresh, logout, logout-all, forgot/reset-password, passkey, external (Google/Apple) ·
`account/*` me, change-password, email change, 2FA, sessions, passkeys, linked logins · `seller/*` wizard + certificate ·
`account/phone*` number + PIN (step-up) · `channels/voice|sms|links` provider webhooks and texted links (signed; see `docs/PHONE-SMS.md`) · `session`, `session/account` (account switching) · `team/*`, `invitations/*`, `account/invitations`, `brands/*` (team members, roles, brands: see `docs/TEAMS.md`) · `seller/products*`, `seller/questions*`, `categories*` (catalog, images, variations, CSV, customer questions: see `docs/PRODUCTS.md`) · `admin/sellers/{id}/status` (Admin role) · `public-config`. Testing/Development only: `/api/test/emails` (Testing), `/api/dev/questions`.
Login contract details and the failure `hint` field: `docs/DECISIONS.md`.

Orders: `seller/orders*`, `seller/messages*`, `seller/message-templates`, `seller/notification-preferences`, `seller/events` (SSE) · marketplace-only `/api/internal/v1/*` (HMAC-signed, never a seller token) ·
Development/Testing only: `seller/dev/*` order simulator, `/api/dev/outbox` sink (`scripts/post-signed.sh` signs real requests).

## Deploy notes
* PostgreSQL: set `Database:Provider=Postgres`; apply migrations with `dotnet ef database update` (or `Database:AutoMigrate=true` for a single instance).
* Required in Production: `Orders:InboundSecret` + `Orders:OutboundSecret` (different) and `Orders:WebhookUrl`, `App:HmacSecret`, `Jwt:PrivateKeyPem`, Turnstile keys, an email provider, `DataProtection:KeysPath` (reset links must survive restarts), shared file storage if >1 instance.
* Run behind TLS; set `App:TrustedProxies` to your reverse proxy so per-IP rate limits use the real client IP.
* Serve the SPA and API from the same site (e.g. `/` and `/api`) so the SameSite=Strict refresh cookie works. See `docs/SETUP-INTEGRATIONS.md`.
