# Integration and secrets setup

This file lists every external integration and every secret the seller-portal API (`src/Solvelt.Api`) reads, for **local dev**, **test-mode staging** and **production**.
It is written from the code (options classes, `Program.cs` and the `Add*` module methods). Each module's design doc (`docs/PAYMENTS.md`, `docs/SHIPPING.md`, ...) explains behaviour; this file explains configuration.

**The repository is public. Never commit a real value.** All hosts below are placeholders (`https://api.example.com` = this API, `https://seller.example.com` = the seller SPA, `https://www.example.com` = the buyer marketplace).

---

## 0. How configuration works

* **JSON path vs env var.** Every key `Section:Key` can be set as the environment variable `Section__Key`. Arrays use an index: `App__CorsOrigins__0`, `App__CorsOrigins__1`. Dictionaries use the key: `Jwt__PreviousPublicKeys__2026-05`.
* **Where to put secrets.** Dev: `dotnet user-secrets set "Section:Key" "value"` in `src/Solvelt.Api`. Staging/production: environment variables or a secret manager (Azure Key Vault, AWS Secrets Manager, ...) mapped to the same names. `appsettings.json` holds non-secret defaults only; `appsettings.Example.json` and `.env.example` hold placeholders only.
* **PEM values** (`Jwt:PrivateKeyPem`, `ExternalAuth:ApplePrivateKeyPem`) need real line breaks. A literal `\n` in an env var does not work. Use a secret manager, a quoted multi-line value, or `export Jwt__PrivateKeyPem="$(cat jwt.pem)"`.
* **Extra env var names.** Four secrets can also be read from plain names, used only when the `Section:Key` form is empty: `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `SHIPPO_API_KEY` and `ANTHROPIC_API_KEY`.

### Environments (`ASPNETCORE_ENVIRONMENT`)

The guards in the code check for three cases: **Development/Testing**, **Production**, and **anything else** (for example `Staging`). Test-mode staging must use a name other than `Development` (for example `Staging`). Then all the "required outside Development/Testing" guards apply, but Stripe and Shippo **test** keys are still accepted.

| Behaviour | Development | Testing (test host only) | Staging (any other name) | Production |
|---|---|---|---|---|
| Missing `App:HmacSecret`, `Orders:*Secret`, `Shipping:LinkKey`, `LocalDelivery:ProofUrlKey` | fixed dev values | fixed dev values | **startup fails** | **startup fails** |
| Missing `Jwt:PrivateKeyPem` | ephemeral key | ephemeral key | **fails when first used** (sign-in / token validation) | same as staging |
| Placeholder or missing `Email:SupportAddress` | allowed | allowed | **startup fails** | **startup fails** |
| No Stripe key | fake provider | fake provider | "unconfigured" provider (payments answer 503) | "unconfigured" provider |
| No Shippo key | fake provider | fake provider | "unconfigured" provider | "unconfigured" provider |
| Stripe / Shippo **test** key | ok | ok | ok | **refused** unless `Payments:AllowTestKeysInProduction` / `Shipping:AllowTestKeyInProduction` |
| Stripe / Shippo **live** key | **refused** unless `Allow…OutsideProduction` | refused | **refused** unless `Allow…OutsideProduction` | ok |
| `Phone:Enabled` with `Provider=Sink`, no signing key or no `PublicBaseUrl` | allowed | allowed | **startup fails** | **startup fails** |
| `Assistant:Provider=Fake` | assistant on | on | assistant **on** (scripted) | assistant **forced off** |
| Simulators, `/api/dev/*`, outbox dev sink, OpenAPI | yes | `/api/dev/*` and `/api/test/*` | no | no |
| HSTS (`App:UseHsts`) | off | off | on | on |

---

## 1. Quick start

### 1.1 Which integrations each environment needs

"Fake" means a built-in offline provider; nothing has to be set up.

| Integration | Local dev | Test-mode staging | Production |
|---|---|---|---|
| Database (§2) | SQLite (automatic) | PostgreSQL recommended | **PostgreSQL, required** |
| `App:HmacSecret` (§3) | dev default | **required** | **required** |
| JWT signing key (§4) | ephemeral | **required** | **required** (+ rotation plan) |
| Data Protection key ring (§5) | in-memory/default | **required** if >1 instance or restarts matter | **required** |
| File storage (§6) | `App_Data/uploads` | persistent volume | persistent, shared volume |
| Frontend URLs and CORS (§7) | defaults (`localhost:5173`) | **required** | **required** |
| Passkeys (§8) | `localhost` | **required** (staging domain) | **required** |
| Email (§9) | Sink or Mailpit (SMTP) | SMTP / Resend (test domain) + **real `SupportAddress`** | SMTP / Resend + SPF/DKIM/DMARC + **real `SupportAddress`** |
| Turnstile (§10) | Cloudflare test keys (already in `appsettings.Development.json`) | real widget or Cloudflare test keys | **real widget required** |
| Google sign-in (§11) | optional | optional | optional (recommended) |
| Apple sign-in (§12) | off | optional | optional (feature flag) |
| Marketplace signed API: `Orders:InboundSecret` (§13) | dev default | **required** | **required** |
| Outbox webhooks: `Orders:OutboundSecret` + `Orders:WebhookUrl` (§14) | dev sink | **required** | **required** |
| Catalog feed and images (§15) | nothing extra | uses §13/§14 | uses §13/§14 |
| Stripe Connect (§16) | fake | Stripe **test** keys + webhook | Stripe **live** keys + webhook |
| Shippo (§17) | fake | Shippo **test** token + webhook | Shippo **live** token + webhook |
| Shipping `LinkKey` (§17.4) | dev default | **required** | **required** |
| Local delivery `ProofUrlKey` (§18) | dev default | **required** | **required** |
| Phone/SMS: SignalWire or Twilio (§19) | off or Sink + simulator | optional | optional (`Phone:Enabled`) |
| Anthropic (§20) | Fake | optional | needed for the assistant (otherwise it is off) |
| HaveIBeenPwned (§21) | off | on | **on** |
| Admin seed and staff panel (§22) | optional | first Super admin | first Super admin, IP allowlist recommended |
| Reverse proxy (§23) | – | `App:TrustedProxies` | **`App:TrustedProxies`** |
| Rate limits (§24) | defaults | defaults | review |
| Background jobs and retention (§25) | defaults | defaults | at least one instance with jobs on |

### 1.2 Secrets that must be distinct

Startup **enforces** these three rules:

1. `Orders:InboundSecret` ≠ `Orders:OutboundSecret` (`"Orders:InboundSecret and Orders:OutboundSecret must differ."`).
2. `Shipping:LinkKey` ≠ `Orders:InboundSecret` and ≠ `Orders:OutboundSecret`.
3. `LocalDelivery:ProofUrlKey` ≠ `Orders:InboundSecret` and ≠ `Orders:OutboundSecret`.

The code does **not** check the rules below, but follow them anyway. Each key gets its own random value of at least 32 bytes (`openssl rand -hex 32`):

4. `Shipping:LinkKey` ≠ `LocalDelivery:ProofUrlKey`. Both are portal-only signing keys, and the marketplace holds neither.
5. `App:HmacSecret` is used for nothing else.
6. `Shipping:WebhookSecret` is used for nothing else. It sits in the Shippo webhook URL, so treat it as less secret than the others.
7. Staging and production never share any secret, key pair or webhook secret.
8. The **marketplace** holds only `Orders:InboundSecret` (it signs requests to us) and `Orders:OutboundSecret` (it verifies our webhooks). It must never hold `LinkKey`, `ProofUrlKey`, `HmacSecret`, the JWT key or the Stripe secret of this service.

Provider-issued secrets (Stripe `whsec_…`, SignalWire signing key, Twilio auth token, Google/Apple credentials) are unique per account or endpoint by design.

### 1.3 Minimal local run

```bash
cd src/Solvelt.Api
dotnet user-secrets init
dotnet user-secrets set "Email:Provider" "Sink"          # or Smtp + Mailpit on localhost:1025
dotnet run --urls https://localhost:5001                 # ASPNETCORE_ENVIRONMENT=Development from launchSettings
```
Everything else falls back to fakes and dev defaults: SQLite, an ephemeral JWT key, fake Stripe, fake Shippo, the outbox dev sink (`GET /api/dev/outbox`), Turnstile test keys and the scripted assistant.

---

## 2. PostgreSQL and migrations

**What it is for:** the production database. SQLite is for dev and tests only, and its schema is created with `EnsureCreated`, not migrations.

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Database:Provider` | `Database__Provider` | opt | `Postgres` | **`Postgres`** | `Sqlite` | `Sqlite` or `Postgres` |
| `Database:ConnectionString` | `Database__ConnectionString` | opt | **req** | **req** | `Data Source=App_Data/solvelt.db` | Npgsql or SQLite connection string (secret: contains the password) |
| `Database:AutoMigrate` | `Database__AutoMigrate` | – | opt | opt | `false` | Postgres only: run `Migrate()` at startup. Use it only with a single instance. |

```bash
export Database__Provider=Postgres Database__ConnectionString="Host=db.example.com;Database=solvelt;Username=solvelt;Password=CHANGE_ME;SSL Mode=Require"
dotnet tool restore
dotnet ef database update --project src/Solvelt.Api
dotnet ef migrations add <Name> --project src/Solvelt.Api --output-dir Migrations   # after model changes
```
At every startup the app also runs idempotent backfills (`Seeder.InitAsync`) for legacy status, the catalog version, catalog delivery and teams.

The money paths (refund/dispute ingest, release, staff retry) and the booking and delivery locks rely on PostgreSQL row locks and serializable transactions (`docs/PAYMENTS.md` §14, `docs/SERVICES.md` §15).

**Verify:** `GET /health` returns 200, because it includes a DbContext check. Opt-in migration test: `SOLVELT_PG_ADMIN="Host=localhost;Username=postgres;Password=…;Database=postgres" dotnet test tests/Solvelt.Api.PostgresTests`. It creates and drops its own database, needs a superuser, is not in `Solvelt.slnx`, and never runs in CI. Before deploying `ServicesReviewFixes`, run the duplicate check in `docs/SERVICES.md` §14.

---

## 3. App: HMAC secret, versions, HTTPS

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `App:HmacSecret` | `App__HmacSecret` | opt | **req** | **req** | (dev value) | HMAC key for one-time codes and throttle keys. Startup fails outside Development/Testing without it. Changing it invalidates outstanding codes. |
| `App:TermsVersion` | `App__TermsVersion` | | | | `2026-01` | Version recorded on acceptance. The staff platform setting overrides it. |
| `App:SellerAgreementVersion` | `App__SellerAgreementVersion` | | | | `2026-01` | Same, for the seller agreement |
| `App:SmsWordingVersion` | `App__SmsWordingVersion` | | | | `2026-01` | Version of the SMS consent wording |
| `App:UseHttpsRedirection` | `App__UseHttpsRedirection` | | | | `true` | `UseHttpsRedirection()` |
| `App:UseHsts` | `App__UseHsts` | | | | `true` (`false` in Development json) | HSTS, never in Development/Testing |
| `App:TrustedProxies` | `App__TrustedProxies__0` | | see §23 | see §23 | `[]` | Reverse-proxy IPs for `X-Forwarded-For/Proto` |
| `App:FrontendBaseUrl`, `App:CorsOrigins`, `App:MarketplaceUrl` | | | | | | see §7 |

Generate the secret with `openssl rand -hex 32`.

---

## 4. JWT signing keys and rotation

Access tokens are RS256 with a `kid` header. The issuer and audience are validated with zero clock skew.

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Jwt:PrivateKeyPem` | `Jwt__PrivateKeyPem` | opt | **req** | **req** | (ephemeral in Dev/Testing) | PKCS#8 PEM RSA private key (secret) |
| `Jwt:KeyId` | `Jwt__KeyId` | | req | req | `k1` | `kid` of the current key, e.g. `2026-11` |
| `Jwt:PreviousPublicKeys:<kid>` | `Jwt__PreviousPublicKeys__<kid>` | | | during rotation | `{}` | Old SPKI public keys still accepted |
| `Jwt:Issuer` | `Jwt__Issuer` | | | set | `solvelt-api` | e.g. `https://api.example.com` |
| `Jwt:Audience` | `Jwt__Audience` | | | | `solvelt-portal` | |
| `Jwt:AccessTokenMinutes` | `Jwt__AccessTokenMinutes` | | | | `60` | Access-token lifetime |
| `Session:IdleTimeoutMinutes` | `Session__IdleTimeoutMinutes` | | | | `20` | Server idle limit (see §8.1) |
| `Session:RefreshTokenDays` | `Session__RefreshTokenDays` | | | | `30` | Refresh-token lifetime |
| `Session:RefreshCookiePath` | `Session__RefreshCookiePath` | | | | `/api/v1/auth` | Path of the refresh cookie |
| `Session:RefreshGraceSeconds` | `Session__RefreshGraceSeconds` | | | | `30` | How long the just-rotated refresh token may be presented again (concurrent tabs) without counting as reuse |

**Generate:** `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt.pem`. Put it in `Jwt:PrivateKeyPem` and pick a new `Jwt:KeyId`.

**Rotate:**
1. Export the old public key: `openssl pkey -in old.pem -pubout`.
2. Add it as `Jwt:PreviousPublicKeys:<old-kid>`.
3. Deploy with the new private key and a new `KeyId`.
4. Remove the old entry after `AccessTokenMinutes` (60 min) have passed.

**Guard:** outside Development/Testing a missing key throws `"Jwt:PrivateKeyPem must be configured outside Development/Testing."`. The `JwtKeys` singleton is created on first use, so the host can still come up and fail on the first sign-in or authenticated request. Check it with a real sign-in after every deploy.

---

## 5. Data Protection key ring

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `DataProtection:KeysPath` | `DataProtection__KeysPath` | – | **req** | **req** | (ASP.NET default location) | Directory where the key ring is persisted. The application name is fixed to `Solvelt`. |

It protects password-reset and email tokens (30-minute lifetime), the passkey/2FA cookies and the external-login cookie (`sl_ext`). If the keys are lost (container restart without a volume, or several instances without a shared path), links already sent stop working. Mount a persistent path that all instances share.

---

## 6. File storage

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Storage:LocalRoot` | `Storage__LocalRoot` | opt | req | req | `App_Data/uploads` | Root of `LocalFileStorage` (relative paths resolve under the content root) |

`IFileStorage` holds more than certificates. It also stores product images, order-line image snapshots, buyer-message attachments, shipping label files, proof-of-delivery photos and signatures, and brand assets. Files are never web-served directly. They are streamed through authorised endpoints or signed links (§15, §17.4, §18).

In production, mount a persistent volume that every instance shares, or implement `IFileStorage` for S3/Azure Blob and register it in `Program.cs` (`AddSingleton<IFileStorage, …>`). Uploads are limited to 12 MB per multipart request.

---

## 7. Frontend URLs and CORS

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `App:FrontendBaseUrl` | `App__FrontendBaseUrl` | | **req** | **req** | `http://localhost:5173` | Base of every link in email, SMS and voice prompts (reset password, invitations, `/l/{token}` phone links, order/booking links), the Google/Apple return, and the Stripe onboarding return/refresh URLs (`{FrontendBaseUrl}/payments?onboarding=return\|refresh`) |
| `App:CorsOrigins` | `App__CorsOrigins__0` … | | **req** | **req** | `["http://localhost:5173"]` | Exact SPA origins. Allowed: credentials, headers `Authorization, Content-Type, X-CSRF-Token`, methods GET/POST/PUT/PATCH/DELETE. |
| `App:MarketplaceUrl` | `App__MarketplaceUrl` | | req | req | `https://example.com/marketplace` | Buyer marketplace URL, published in `/api/v1/public-config` |

Serve the SPA and the API from the same site (for example `/` and `/api`). The refresh cookie is SameSite=Strict, so this is required for it to work. The frontend's runtime `public/config.json` is described in §8.1.

---

## 8. Passkeys

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Passkeys:RelyingPartyId` | `Passkeys__RelyingPartyId` | `localhost` | **req** | **req** | `localhost` | Registrable domain of the frontend, e.g. `example.com` |
| `Passkeys:AllowedOrigins` | `Passkeys__AllowedOrigins__0` … | | **req** | **req** | `["http://localhost:5173"]` | Exact frontend origins (scheme + host + port) |

User verification is required, so a passkey counts as MFA. Changing the RP ID invalidates every existing passkey. Use separate RP IDs (domains) for staging and production.

### 8.1 Inactivity timing

The frontend's `public/config.json` is served at runtime, so it can be edited without a rebuild. It holds `idleTimeoutMinutes` (default 15), `warningSeconds` (60) and `refreshIntervalMinutes`.

The backend's `Session:IdleTimeoutMinutes` (default 20) is the server-side limit. Keep it slightly larger than the frontend timeout. A session idle longer than the server limit is revoked on its next request or refresh.

### 8.2 Login lockout

| JSON path | Default | Meaning |
|---|---|---|
| `Lockout:MaxFailures` | 5 | Failures (per submitted email) before the lock |
| `Lockout:LockMinutes` | 15 | Lock duration |
| `Lockout:FailureWindowMinutes` | 15 | Window in which failures are counted |
| `Lockout:CaptchaAtFailures` | 4 | Turnstile is demanded from this many failures on |
| `Lockout:WarnAtFailures` | 3 | "Last tries" warning |

---

## 9. Email (SMTP via MailKit, or Resend)

**What it is for:** verification codes, password resets, invitations, and order, payment, shipping and booking notifications.

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Email:Provider` | `Email__Provider` | `Sink`/`Smtp` | **req** | **req** | `Sink` in code (`appsettings.json` sets `Smtp`) | `Sink` (in memory), `Smtp` or `Resend` |
| `Email:FromAddress` | `Email__FromAddress` | | req | req | `no-reply@example.com` | Sender address on a verified domain |
| `Email:FromName` | `Email__FromName` | | | | `Solvelt` | Sender name |
| `Email:SupportAddress` | `Email__SupportAddress` | opt | **req** | **req** | empty (falls back to `Phone:HelpContact`, then `support@example.com`) | "Enquiries" address in every footer (`mailto:`) |
| `Email:SmtpHost` / `SmtpPort` | `Email__SmtpHost` / `Email__SmtpPort` | `localhost`/`1025` | Smtp | Smtp | `localhost` / `1025` | SMTP server |
| `Email:SmtpUseSsl` | `Email__SmtpUseSsl` | | | | `false` | `true` forces STARTTLS. `false` uses MailKit `Auto` (implicit TLS on port 465, STARTTLS when offered). |
| `Email:SmtpUser` / `SmtpPassword` | `Email__SmtpUser` / `Email__SmtpPassword` | | Smtp | Smtp | empty | Auth is skipped when the user is empty (secret) |
| `Email:ResendApiKey` | `Email__ResendApiKey` | | Resend | Resend | empty | Resend API key with *Sending access* (secret) |
| `Email:SendInBackground` | `Email__SendInBackground` | | | | `true` | Sends "forgot password" mail off the request path, so timing does not reveal whether the account exists |

**Setup**
* **SMTP:** any provider. Locally, Mailpit or MailHog on `localhost:1025`.
* **Resend:** create an API key with Sending access, add and verify the sending domain, then set `Email:Provider=Resend` and `Email:ResendApiKey`. The code posts to `https://api.resend.com/emails`.
* **DNS:** send from a subdomain (for example `mail.example.com`) and publish:
  * SPF: `v=spf1 include:<provider> -all`
  * DKIM: the CNAME/TXT records the provider gives you
  * DMARC: `_dmarc` TXT `v=DMARC1; p=quarantine; rua=mailto:dmarc@example.com`
* **Support address guard:** outside Development/Testing the host **refuses to start** in two cases:
  * neither `Email:SupportAddress` nor a plain-address `Phone:HelpContact` is set;
  * the result is a placeholder (any `example` label in the domain, or a `.invalid` or `.localhost` TLD).

  Only plain addresses are accepted (letters, digits, `. _ + -`). `?`, `&`, `%`, quotes and spaces are refused.

**No key:** `Sink` keeps mail in memory. `GET /api/test/emails` exists only in Testing. The code does **not** refuse `Sink` (or an unknown provider name, which also falls back to Sink) in Production, so set the provider explicitly.

**Verify:** trigger "forgot password" for a test account and check delivery, SPF/DKIM pass and the footer address.

---

## 10. Cloudflare Turnstile

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Turnstile:SiteKey` | `Turnstile__SiteKey` | test key | req | **req** | empty (Development json: `1x00000000000000000000AA`) | Public, served to the SPA via `/api/v1/public-config` |
| `Turnstile:SecretKey` | `Turnstile__SecretKey` | test key | req | **req** | empty (Development json: `1x0000000000000000000000000000000AA`) | Secret for `siteverify` |
| `Turnstile:UseTestStub` | – | – | – | – | `false` | Offline emulation of the test keys. **Only allowed in Testing** (startup fails otherwise). |

1. Cloudflare dashboard → Turnstile → Add widget (Managed) for the frontend hostname(s). Use separate widgets for staging and production.
2. Set the site key and the secret.

Cloudflare's test keys:
* site key `1x00000000000000000000AA`;
* secret `1x0000000000000000000000000000000AA` always passes;
* secret `2x0000000000000000000000000000000AA` always fails.

The captcha is demanded only from `Lockout:CaptchaAtFailures` (4) failed logins for an email. Verification **fails closed**: with no secret or no network, a login that needs the captcha cannot succeed. The code has no startup guard for a missing Turnstile key. Admin → Settings shows `Turnstile:Configured`.

---

## 11. Google sign-in (OIDC)

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `ExternalAuth:GoogleClientId` | `ExternalAuth__GoogleClientId` | opt | opt | opt | empty (scheme not registered) | OAuth client id |
| `ExternalAuth:GoogleClientSecret` | `ExternalAuth__GoogleClientSecret` | opt | opt | opt | empty | Client secret |

1. Google Cloud Console → *APIs & Services → OAuth consent screen*: External, app name, support email, authorised domain, scopes `openid email profile`. Publish it (or add test users while it is in testing).
2. *Credentials → Create credentials → OAuth client ID → Web application*.
3. Authorised redirect URI: `https://api.example.com/signin-google` (dev: `https://localhost:5001/signin-google`). It is served by the API host, not under `/api`. JavaScript origins: the frontend origin.
4. Set both keys. The flow is the code flow with PKCE, state and nonce. Sign-in starts at `GET /api/v1/auth/external/google/start`.

Use a separate OAuth client per environment. **Verify:** sign in with a Google test user and check the linked login under Account.

---

## 12. Sign in with Apple (feature flag, off by default)

| JSON path | Env var | Default | Meaning |
|---|---|---|---|
| `ExternalAuth:AppleEnabled` | `ExternalAuth__AppleEnabled` | `false` | Registers the scheme |
| `ExternalAuth:AppleClientId` | `ExternalAuth__AppleClientId` | empty | Services ID, e.g. `com.example.seller.web` |
| `ExternalAuth:AppleTeamId` | `ExternalAuth__AppleTeamId` | empty | Team ID (Membership page) |
| `ExternalAuth:AppleKeyId` | `ExternalAuth__AppleKeyId` | empty | Key ID of the .p8 key |
| `ExternalAuth:ApplePrivateKeyPem` | `ExternalAuth__ApplePrivateKeyPem` | empty | Contents of the .p8 file (secret) |

1. Apple Developer → Identifiers: create an **App ID** with *Sign in with Apple*.
2. Create a **Services ID**. Configure Sign in with Apple:
   * primary App ID;
   * **domain** (host the verification file at `/.well-known/apple-developer-domain-association.txt`);
   * **return URL** `https://api.example.com/signin-apple` (response mode `form_post`).
3. Keys → create a key with Sign in with Apple. Download the **.p8** (only possible once) and note the Key ID.
4. Set the keys above, then `AppleEnabled=true`. The client secret JWT (ES256, 5 min) is generated for each token request.

---

## 13. Marketplace signed internal API (marketplace → portal)

**What it is for:** every call from the buyer marketplace: orders, cancel/return requests, delivery confirmations, messages and receipts, attachments, the catalog feed and images, questions, content reports, bookings/availability/holds/quotes, local-delivery eligibility, shipping settings, return-label links and payment events. Base path: `/api/internal/v1`. Full contract: `docs/ORDERS.md` §3.1.

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Orders:InboundSecret` | `Orders__InboundSecret` | dev default | **req** | **req** | (dev value) | HMAC-SHA256 key the marketplace signs with. **Shared with the marketplace.** |
| `Orders:TimestampWindowSeconds` | `Orders__TimestampWindowSeconds` | | | | `300` | Allowed clock skew (±). Nonces are kept for twice this window. |
| `Orders:MaxInboundBodyBytes` | `Orders__MaxInboundBodyBytes` | | | | `262144` | Larger bodies get `413` |

**Signing:** three headers.
* `X-Solvelt-Timestamp`: Unix seconds.
* `X-Solvelt-Nonce`: unique, ≤ 100 characters.
* `X-Solvelt-Signature`: `v1=` + hex `HMAC-SHA256(InboundSecret, canonical)`.

`canonical` = `timestamp \n nonce \n METHOD \n encoded path+query \n sha256hex(body)`. Failures return `401 unsigned | bad_timestamp | stale | bad_signature | replayed`. With no secret, the endpoints answer `503 inbound_not_configured` (unreachable in practice, because startup requires the secret).

**Guards:** both Orders secrets are required outside Development/Testing, and they must differ (§1.2).

**Clock skew:** keep both servers on NTP. More than 300 s of drift shows up as `stale` under Admin → Integrations → inbound.

**Rotation:** the code accepts only **one** inbound secret, so there is no overlap window. Rotate in a maintenance window: deploy the new secret on both sides together, and expect a short burst of `bad_signature` that the marketplace retries.

**Verify:** `ORDERS_INBOUND_SECRET=… scripts/post-signed.sh GET /api/internal/v1/catalog/categories "" https://api.example.com` returns 200. Test vector (`docs/ORDERS.md`): secret `test-secret`, ts `1700000000`, nonce `abc123`, `POST /api/internal/v1/orders`, body `{"a":1}` gives signature `v1=8a1a05a14201d38431d0c9816b6b127ff9193117e81e0530895a25934cbaadea`.

---

## 14. Outbox webhooks (portal → marketplace)

**What it is for:** every seller-side change (order, shipment, refund, booking, listing, report decision and message events) is delivered to the marketplace, at least once.

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Orders:OutboundSecret` | `Orders__OutboundSecret` | dev default | **req** | **req** | (dev value) | HMAC key for our webhooks. **Shared with the marketplace** (it verifies). Must differ from the inbound secret. |
| `Orders:WebhookUrl` | `Orders__WebhookUrl` | empty (dev sink) | **req** | **req** | empty | Marketplace receiver, e.g. `https://www.example.com/hooks/solvelt` |
| `Orders:OutboxMaxAttempts` | `Orders__OutboxMaxAttempts` | | | | `8` | Attempts before a message is Dead |
| `Orders:OutboxBackoffSeconds` | `Orders__OutboxBackoffSeconds` | | | | `30` | `30 × 2^(n-1)` s, capped at 1 h |
| `Orders:RunBackgroundJobs` | `Orders__RunBackgroundJobs` | | | | `true` | Runs `OutboxWorker` (dispatcher plus order maintenance) |

**Request:**
* `POST <WebhookUrl>` with headers `X-Solvelt-Timestamp`, `X-Solvelt-Signature: v1=HMAC-SHA256(OutboundSecret, "<ts>.<raw body>")`, `X-Solvelt-Event-Id` and `X-Solvelt-Event-Type`.
* The event-id and event-type headers are **not signed**. Read `id` and `type` from the body.
* The receiver must answer `2xx` and dedupe on `id`.
* HTTP timeout: 10 s.

**No URL:** Development/Testing deliver to the in-memory dev sink (`GET /api/dev/outbox`). In **any other environment** each attempt fails with `"No webhook URL configured"`, backs off, and the message becomes **Dead** after `OutboxMaxAttempts` (about an hour with the defaults). Re-queue dead messages from Admin → Integrations → outbox (`retry`) once the URL is set. There is no startup guard for a missing `WebhookUrl`.

---

## 15. Catalog feed and catalog images

There is **no separate image-signing key.** The catalog feed (`GET /api/internal/v1/catalog/listings`, `/listings/{id}`, `/categories`, `/brands`) and the image bytes (`GET /api/internal/v1/catalog/images/{imageId}`) are ordinary signed internal requests. They use `Orders:InboundSecret` (§13) and the path + query is part of the signature. The marketplace must fetch images server-side with a signed request and serve or cache them itself; it must not hand the internal path to browsers.

Images are `404` unless they belong to a currently visible listing. Listing change events (`listing.upserted`, `listing.unlisted`) arrive through the outbox (§14).

Rate limits (§24): `RateLimit:InternalCatalogPerMinute` (1200/IP), `InternalQuestionsPerListingPerMinute` (60), `InternalQuestionsPerCallerPerMinute` (2000), `InternalReportsPerTargetPerMinute` (60), `InternalReportsPerCallerPerMinute` (2000) and `InternalUnsignedPerMinute` (60/IP, spent only by failed verifications).

Other links that are signed or tokenised, each with its own key:
* return-label links: `Shipping:LinkKey` (§17.4);
* proof-of-delivery links: `LocalDelivery:ProofUrlKey` (§18);
* ICS calendar feeds: per-provider random tokens stored hashed, no config (`docs/SERVICES.md` §10);
* seller-side product images: served behind seller authentication only.

---

## 16. Stripe Connect (payouts)

**What it is for:** the portal keeps the seller ledger, releases funds, and **transfers** them to each seller's **Stripe Express** connected account. It uses Stripe *separate charges and transfers*: buyers are charged on the **platform** account by the marketplace. It mirrors payouts and disputes from webhooks. Design: `docs/PAYMENTS.md`.

The portal and the marketplace must use the **same Stripe platform account**, because transfers are paid from the platform balance that the marketplace's charges fund.

### 16.1 Keys

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Stripe:SecretKey` | `Stripe__SecretKey` or `STRIPE_SECRET_KEY` | – (fake) | `sk_test_…`/`rk_test_…` | `sk_live_…`/`rk_live_…` | empty | Platform secret or restricted key (secret) |
| `Stripe:WebhookSecret` | `Stripe__WebhookSecret` or `STRIPE_WEBHOOK_SECRET` | – | **req** with a key | **req** with a key | empty | Signing secret `whsec_…` of the **platform** endpoint ("Your account"). Without it the webhook answers `503 webhook_not_configured`. |
| `Stripe:ConnectWebhookSecret` | `Stripe__ConnectWebhookSecret` or `STRIPE_CONNECT_WEBHOOK_SECRET` | – | **req** with two endpoints | **req** with a live key | empty | Signing secret `whsec_…` of the **Connect** endpoint ("Connected accounts"). With both secrets set, an event carrying `account` must be signed with this one, and other events with the platform secret; otherwise `400 wrong_endpoint_secret`. |
| `Payments:AllowSingleWebhookSecret` | `Payments__AllowSingleWebhookSecret` | | | `false` | `false` | Accept a live key with only one webhook secret, on purpose (e.g. one endpoint only). |
| `Stripe:DefaultCountry` | `Stripe__DefaultCountry` | | | | `US` | Country of new Express accounts when the seller account has none |
| `Payments:AllowTestKeysInProduction` | `Payments__AllowTestKeysInProduction` | | | `false` | `false` | Escape hatch: a test key in Production |
| `Payments:AllowLiveKeysOutsideProduction` | `Payments__AllowLiveKeysOutsideProduction` | `false` | `false` | | `false` | Escape hatch: a live key outside Production |
| `Payments:WebhookToleranceSeconds` | `Payments__WebhookToleranceSeconds` | | | | `300` | Stripe signature timestamp tolerance |
| `Payments:WebhooksPerMinute` | `Payments__WebhooksPerMinute` | | | | `1200` | Per-IP limit of the Stripe webhook |
| `Payments:ReleaseDays` | | | | | `7` | Days after delivery/completion before funds become available |
| `Payments:RequireReturnWindowClosed` | | | | | `true` | Also wait for `Orders:ReturnWindowDays` (30) |
| `Payments:NewSellerReservePercent` / `NewSellerDays` / `ReserveReleaseDays` | | | | | `10` / `90` / `30` | Reserve for new sellers |
| `Payments:MinTransferCents` | | | | | `500` | Smaller balances wait |
| `Payments:TransferMaxAttempts` / `TransferBackoffSeconds` | | | | | `5` / `60` | Transfer retries |
| `Payments:DefaultDisputeFeeCents` | | | | | `1500` | Fee debited when an event carries none |
| `Payments:RunBackgroundJobs` / `JobIntervalSeconds` / `LeaseSeconds` / `ReconcileDays` | | | | | `true` / `300` / `300` / `35` | Release, transfer and reconcile job |
| `RateLimit:PaymentsPerMinute` | `RateLimit__PaymentsPerMinute` | | | | `120` | Seller payments API, per user |

**Key guards (startup):**
* The key must start with `sk_test_`, `rk_test_`, `sk_live_` or `rk_live_`; anything else is refused.
* **Production refuses a test key** unless `Payments:AllowTestKeysInProduction=true`.
* Every other environment, staging included, **refuses a live key** unless `AllowLiveKeysOutsideProduction=true`.

**No key:**
* Development/Testing use the deterministic fake provider. Dev helpers: `POST /api/dev/payments/run`, `complete-onboarding/{sellerId}`, `capture/{orderId}`, `refund/{orderId}`, `age-order/{orderId}`.
* Other environments use an *unconfigured* provider: the payments endpoints answer `503`, and the app still starts.

**Restricted key (recommended):** grant write access to *Accounts* (Express accounts, account links, login links) and *Transfers*, and read access to *Balance* and *Payouts*. Use a separate key per environment.

### 16.2 Dashboard setup, test mode first

1. Create or sign in to the Stripe account and switch the dashboard to **Test mode**. Use the **same account the marketplace uses**.
2. **Connect → Get started:** platform/marketplace, **Express** accounts, charges type *separate charges and transfers*. The portal requests only the `transfers` capability.
3. **Connect → Settings → Branding:** name, icon, colours, support email, terms/privacy links shown during onboarding.
4. **Connect → Settings → Payouts:** the portal does **not** set or expect a particular payout schedule. It moves money to the connected account with transfers (at most one per seller, currency and UTC day, once `Available ≥ MinTransferCents`). Stripe then pays out on the connected account's own schedule. The portal reads `settings.payouts.schedule` (`interval`, `delay_days`) from `account.updated` only to display it. Stripe's default (daily, rolling delay) is fine. Choose weekly or monthly if you prefer, and decide whether sellers may change it in the Express dashboard.
5. **Developers → API keys:** create a restricted key (or use the secret key) → `STRIPE_SECRET_KEY`.
6. **Webhooks (Developers → Webhooks / Workbench → Event destinations):** register `https://api.example.com/api/webhooks/stripe` twice:
   * **Platform** endpoint ("Your account"): `transfer.created`, `transfer.updated`, `transfer.reversed`, `transfer.paid`, `charge.dispute.created`, `charge.dispute.updated`, `charge.dispute.closed`. Add `account.updated` too; it is harmless.
   * **Connect** endpoint ("Connected accounts"): `account.updated`, `payout.created`, `payout.updated`, `payout.paid`, `payout.failed`, `payout.canceled`.

   Unknown events are acknowledged and ignored.

   > **Two secrets.** Stripe issues a different `whsec_…` per endpoint: set the platform one as `STRIPE_WEBHOOK_SECRET` and the Connect one as `STRIPE_CONNECT_WEBHOOK_SECRET`. Both endpoints may point at `/api/webhooks/stripe`, or register the Connect endpoint at the optional `/api/webhooks/stripe/connect`, which accepts only the Connect secret. With only one secret configured (or two equal values) both URLs accept it, which is what the Stripe CLI needs. Production with a live key refuses to start unless both are set or `Payments:AllowSingleWebhookSecret=true`.

7. Local or staging forwarding with the CLI:
   ```bash
   stripe login
   stripe listen --forward-to https://localhost:5001/api/webhooks/stripe \
                 --forward-connect-to https://localhost:5001/api/webhooks/stripe
   # prints one whsec_… that signs both kinds of events -> set it as STRIPE_WEBHOOK_SECRET only (single-secret mode)
   stripe trigger payout.paid   # smoke test
   ```

### 16.3 Test data (Stripe test mode)

* **Express onboarding:** use Stripe's documented test values, for example phone `000 000 0000`, SMS code `000000`, SSN last 4 `0000`, test bank routing `110000000` and account `000123456789`. Address `address_full_match` verifies at once.
* **Platform balance for transfers:** the marketplace's test charges fund it. To make the balance available immediately, charge card `4000 0000 0000 0077`. Normal test cards (`4242 4242 4242 4242`) leave the funds pending, and transfers fail with insufficient funds.
* **Disputes:** card `4000 0000 0000 0259` creates a dispute, which arrives as `charge.dispute.created` on the platform endpoint.

### 16.4 Going live

1. Activate the platform account (business verification, bank account), complete Connect activation, and confirm the tax-reporting settings (Connect → Settings → Tax reporting).
2. Create the **live** restricted key and the live webhook endpoints. Set `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET` (platform endpoint) and `STRIPE_CONNECT_WEBHOOK_SECRET` (Connect endpoint) in the production secret store with `ASPNETCORE_ENVIRONMENT=Production`. Leave both `Allow…` flags `false`.
3. The marketplace must send `payment.captured` for every successful charge, with the charge or payment-intent id, plus `refund.processed` and `dispute.*` (`docs/PAYMENTS.md` §6). Optionally it can create charges with a `transfer_group` per order.
4. Keep `Payments:RunBackgroundJobs=true` on at least one instance. Check Admin → Integrations → jobs (`payments-release`, `payments-transfers`, `payments-reconcile`).
5. Make a small live transfer to a staff-owned seller before opening payouts to everyone.

**Verify:**
* Opt-in tests: `STRIPE_SECRET_KEY=sk_test_… dotnet test tests/Solvelt.Api.StripeTests` (trait `StripeTestMode`, not in `Solvelt.slnx`, not in CI). They create an Express test account, an account link, a login-link attempt and a transfer that is expected to fail. They refuse anything but a test key, and without a key they are reported as skipped in that project.
* Manual: as an Owner of an Approved seller, `POST /api/v1/seller/payments/onboarding`, complete the hosted flow with test data, and check `GET /api/v1/seller/payments/onboarding` shows `active` after `account.updated`.

---

## 17. Shippo (labels, rates, tracking)

**What it is for:** address validation, rates, label purchase, void and refund, tracking (webhook plus poller), and prepaid return labels, behind `IShippingProvider`. Design: `docs/SHIPPING.md`.

### 17.1 Keys

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Shipping:ShippoApiKey` | `Shipping__ShippoApiKey` or `SHIPPO_API_KEY` | – (fake) | `shippo_test_…` | `shippo_live_…` | empty | Shippo API token (secret) |
| `Shipping:WebhookSecret` | `Shipping__WebhookSecret` | dev default | **req** with a key | **req with a key (startup guard)** | (dev value in Dev/Testing) | Authenticates tracking webhooks (`?token=` or HMAC header) |
| `Shipping:LinkKey` | `Shipping__LinkKey` | dev default | **req** | **req** | (dev value) | Signs return-label links (§17.4) |
| `Shipping:PublicBaseUrl` | `Shipping__PublicBaseUrl` | | **req** | **req** | `https://localhost` | Public API origin used in signed label links. There is no startup guard, so a wrong value produces broken links. |
| `Shipping:AllowTestKeyInProduction` | | | | `false` | `false` | Escape hatch |
| `Shipping:AllowLiveKeyOutsideProduction` | | `false` | `false` | | `false` | Escape hatch |
| `Shipping:LabelFormat` | | | | | `PDF_4x6` | `PDF_4x6`, `PNG` or `ZPLII` (default, sellers can change it) |
| `Shipping:QuoteMinutes` | | | | | `15` | How long a rate can be bought |
| `Shipping:SpendLimitCentsPerMonth` | | | | set | `0` (unlimited) | Per account and UTC month |
| `Shipping:LabelLinkMinutes` | | | | | `1440` | Lifetime of a signed return-label link |
| `Shipping:TrackingPollAfterHours` / `TrackingStopDays` | | | | | `24` / `60` | Poll a silent parcel after / stop tracking after |
| `Shipping:RefundCheckMinutes` / `PollBatch` / `PollLeaseMinutes` | | | | | `30` / `50` / `5` | Job tuning |
| `Shipping:PurchaseStuckMinutes` / `PurchaseAbandonDays` | | | | | `10` / `3` | Crash recovery of purchases |
| `Shipping:ManualDeliveryGraceHours` | | | | | `48` | Tolerance for a carrier "delivered" stamped before a manually entered shipment |
| `Shipping:JobIntervalSeconds` / `RunBackgroundJobs` | | | | | `300` / `true` | `ShippingWorker` |
| `Shipping:WebhookToleranceSeconds` / `WebhooksPerMinute` | | | | | `300` / `600` | HMAC freshness / per-IP limit |
| `Shipping:MaxBulk` | | | | | `25` | Labels per bulk request |
| `RateLimit:ShippingPerMinute` | | | | | `240` | Seller shipping API, per user |

**Key guards:**
* The token must start with `shippo_test_` or `shippo_live_`.
* Production refuses a test token unless `AllowTestKeyInProduction`.
* Other environments refuse a live token unless `AllowLiveKeyOutsideProduction`.
* Production with a token requires `Shipping:WebhookSecret`.
* `LinkKey` is required outside Development/Testing and must differ from both Orders secrets.

**No key:** Development/Testing use the fake provider. Dev helpers: `POST /api/dev/shipping/tracking {trackingNumber, status}` and `POST /api/dev/shipping/run-maintenance`. Other environments use an *unconfigured* provider.

In staging with a token but no webhook secret, the app starts but every webhook is rejected. Set the secret anyway.

### 17.2 Dashboard setup, test mode first

1. Create the platform Shippo account. The platform pays Shippo, and sellers are debited in the portal ledger.
2. **Settings → API:** generate a **test** token (`shippo_test_…`) → `SHIPPO_API_KEY` in dev or staging.
3. **Settings → Webhooks:** add `https://api.example.com/api/v1/shipping/webhooks/shippo?token=<Shipping:WebhookSecret>` with event **`track_updated`**, in test mode for the test token. Shippo cannot sign webhooks, so the `?token=` form is the one to use. The endpoint also accepts an `X-Shipping-Signature: t=<unix>,v1=<hex hmac("t.body")>` header, which the dev helper uses.
4. Going live:
   * add payment details;
   * connect the carrier accounts (USPS, UPS, FedEx, DHL Express);
   * create a **live** token;
   * register the live webhook with a new production `WebhookSecret`;
   * set `SpendLimitCentsPerMonth`.

### 17.3 Verify

* Opt-in: `SHIPPO_API_KEY=shippo_test_… dotnet test tests/Solvelt.Api.ShippoTests` (trait `ShippoTestMode`, not in `Solvelt.slnx`, not in CI). It covers address validation, rates, a label purchase, a refund request and tracking registration. It refuses non-test tokens.
* Manual: as a seller, set a ship-from address and a package, buy a test label on an order, then check that the timeline updates when Shippo sends `track_updated`.

### 17.4 Shipping `LinkKey` (signed return-label links)

`LabelLinks` signs `label:{id}:{exp}` with HMAC-SHA256 under `SHA256("solvelt-label-link|" + LinkKey)`. The URL has the form `{Shipping:PublicBaseUrl}/api/v1/shipping/public/labels/{id}?exp=…&sig=…` and is valid for `LabelLinkMinutes`. The marketplace receives these links from `POST /api/internal/v1/returns/{id}/label-link` and the `return.label_created` event.

The key is deliberately **not** an Orders secret: the marketplace holds those and must not be able to mint label links. Rotating `LinkKey` invalidates every outstanding link. They are short-lived, so rotate at any time.

---

## 18. Local delivery (own drivers)

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `LocalDelivery:ProofUrlKey` | `LocalDelivery__ProofUrlKey` | dev default | **req** | **req** | (dev value) | HMAC key for proof-of-delivery links `/api/public/delivery-proof/{id}?exp=&sig=` (HMAC over `delivery-proof\|{id}\|{exp}`) |
| `LocalDelivery:ProofUrlMinutes` | | | | | `60` | Link lifetime (also staff proof-access links) |
| `LocalDelivery:LocationRetentionHours` | | | | | `24` | Driver positions are deleted after this |
| `LocalDelivery:EtaEventSeconds` | | | | | `60` | Minimum interval between ETA events |
| `LocalDelivery:MaxProofBytes` | | | | | `8388608` | Max photo or signature size |

**Guard:** `ProofUrlKey` is required outside Development/Testing (startup fails) and must differ from both Orders secrets.

**No maps, geocoding or route-optimiser keys exist.** Routes use the built-in `NearestNeighbourOptimizer`. Coordinates come from the marketplace (`shippingAddress.lat/lng`). The ETA is a straight line at 30 km/h plus 4 minutes per earlier stop. A paid routing provider would be a new `IRouteOptimizer` implementation with its own config.

The maintenance worker runs every minute outside Testing and has no on/off flag.

**Verify:** Development has `POST /api/dev/delivery-maintenance/run`. Manually: enable local delivery for a brand, call the signed `POST /api/internal/v1/local-delivery/eligibility`, then run a route in the driver app (`/driver`) and open the proof link from `shipment.delivered` within the hour.

---

## 19. Phone and SMS (SignalWire or Twilio)

Full design: `docs/PHONE-SMS.md`.

| JSON path | Env var | Default | Meaning |
|---|---|---|---|
| `Phone:Enabled` | `Phone__Enabled` | `false` | Maps the voice and SMS webhooks and the account phone endpoints |
| `Phone:Provider` | `Phone__Provider` | `Sink` | `Sink` (Development/Testing only), `SignalWire` or `Twilio` |
| `Phone:AccountSid` | `Phone__AccountSid` | | SignalWire Project ID / Twilio Account SID |
| `Phone:AuthToken` | `Phone__AuthToken` | | SignalWire API token / Twilio auth token (secret) |
| `Phone:SigningKey` | `Phone__SigningKey` | (falls back to AuthToken) | SignalWire *Signing Key* / Twilio auth token (secret) |
| `Phone:Space` | `Phone__Space` | | SignalWire space host, e.g. `example.signalwire.com` |
| `Phone:ApiBaseUrl` | `Phone__ApiBaseUrl` | | Overrides the REST base URL (tests, proxies) |
| `Phone:FromNumber` | `Phone__FromNumber` | | E.164 sender |
| `Phone:PublicBaseUrl` | `Phone__PublicBaseUrl` | | Public API origin. Part of the signed URL, so it must match the provider configuration exactly. |
| `Phone:Voice` / `Language` / `DefaultRegion` | | `Polly.Joanna` / `en-US` / `US` | TTS voice, speech language, region for national numbers |
| `Phone:SupportNumber` | | empty (option hidden) | Transfer target from the signed-in menu |
| `Phone:HelpContact` | | `support@example.com` | Help text. Also the fallback for `Email:SupportAddress`. |
| `Phone:BrandName` | | `Solvelt` | Name in prompts |
| `Phone:CallStateMinutes` / `PhoneSessionMinutes` / `SmsSessionMinutes` / `SignupLinkMinutes` | | `10` / `10` / `15` / `30` | Expiries |
| `Phone:NumberPerMinute` / `IpPerMinute` | | `30` / `600` | Webhook rate limits |
| `Phone:SendInBackground` | | `true` | Sends SMS off the request path (timing privacy) |

**Guards:** with `Phone:Enabled=true` outside Development/Testing, startup fails on `Provider=Sink`, on a missing `SigningKey` and `AuthToken`, or on a missing `PublicBaseUrl`. In any environment, `Phone:Enabled` plus `Ai:Provider=Anthropic` without a key also fails (§20).

### SignalWire (default)
1. Create a Space (`<name>.signalwire.com`) and a Project. **Phone Numbers → Buy** a number with Voice + SMS: a local 10DLC number or a toll-free number.
2. **API → Credentials:**
   * Project ID → `AccountSid`
   * API token → `AuthToken`
   * space host → `Space`
   * the number → `FromNumber`

   Then **Settings → Signing Key** → `SigningKey`.
3. Number settings:
   * **Voice:** LaML webhooks, `POST https://api.example.com/api/v1/channels/voice/incoming`, status callback `https://api.example.com/api/v1/channels/voice/status`.
   * **Messaging:** LaML webhooks, `POST https://api.example.com/api/v1/channels/sms/inbound`.
   * Outgoing messages set `StatusCallback` to `…/api/v1/channels/sms/status` themselves.
4. `Phone:PublicBaseUrl=https://api.example.com`. Keep call recording off.
5. **US A2P 10DLC** is required to text from a local US number:
   1. Brand registration (EIN/legal entity).
   2. A Campaign: use case "Account notification / 2FA", sample messages from `PHONE-SMS.md`, opt-in via the web card and "Text JOIN/LOGIN", opt-out STOP, help HELP.
   3. Attach the number to the campaign.

   Approval takes days, and unregistered traffic is filtered. The alternative is toll-free verification. Messages include "Msg&data rates may apply", and the server handles STOP/HELP.

### Twilio
Same code, with `Phone:Provider=Twilio`: Account SID → `AccountSid`, Auth Token → `AuthToken` (also the signing key). Webhook URLs are as above: Console → Phone Numbers → Active numbers → Voice/Messaging "Webhook, HTTP POST". Register A2P 10DLC under Messaging → Regulatory Compliance.

Signatures are read from `X-Twilio-Signature` or `X-SignalWire-Signature`.

**Dev without a provider:** `Phone:Enabled=true`, `Phone:Provider=Sink` and the Development simulator (`docs/PHONE-SMS.md` "Dev simulator"). **Verify:** call and text the number, and check Admin → Integrations for `sms.sent`.

---

## 20. Anthropic (phone AI and the seller assistant)

| JSON path | Env var | Default | Meaning |
|---|---|---|---|
| `Ai:ApiKey` | `Ai__ApiKey` or `ANTHROPIC_API_KEY` | empty | Anthropic API key, used by **both** the phone AI and the seller assistant (secret) |
| `Ai:Provider` | `Ai__Provider` | `Fake` | Phone/SMS conversations: `Fake` (keyword rules) or `Anthropic` |
| `Ai:Model` / `Ai:VoiceModel` | | `claude-opus-5-5` / empty (= Model) | SMS model / voice model (`claude-haiku-5-5` is the documented faster option) |
| `Ai:TimeoutSeconds` / `VoiceTimeoutSeconds` / `MaxTokens` | | `8` / `4` / `400` | Phone AI limits |
| `Assistant:Enabled` | `Assistant__Enabled` | `true` | Feature flag. Forced off in **Production** while `Assistant:Provider=Fake`. |
| `Assistant:KillSwitch` | `Assistant__KillSwitch` | `false` | Emergency stop (read through `IOptionsMonitor`; reload config or restart) |
| `Assistant:Provider` | `Assistant__Provider` | `Fake` | Seller assistant: `Fake` (scripted) or `Anthropic`. **Separate from `Ai:Provider`.** |
| `Assistant:Model` / `Effort` | | `claude-opus-5-5` / `medium` | Model / thinking effort `low\|medium\|high` |
| `Assistant:MaxTokens` / `MaxIterations` | | `16000` / `8` | Per call / per user message |
| `Assistant:CallTimeoutSeconds` / `ToolTimeoutSeconds` | | `60` / `20` | |
| `Assistant:MaxMessageChars` / `MaxToolResultChars` / `HistoryMessages` | | `4000` / `12000` / `24` | Input size and replayed history |
| `Assistant:DailyMessagesPerUser` / `PerAccount` | | `150` / `1500` | Budgets |
| `Assistant:DailyTokensPerUser` / `PerAccount` | | `600000` / `6000000` | Budgets |
| `Assistant:ActionExpiryMinutes` / `RetentionDays` | | `10` / `90` | Confirm-card expiry / history retention |
| `Assistant:InputUsdPerMTok` / `OutputUsdPerMTok` | | `4` / `20` | Cost estimate only |
| `Assistant:RunBackgroundJobs` | | `true` | `AssistantWorker` (hourly retention) |
| `RateLimit:AssistantPerMinute` | | `30` | Per user |

1. Anthropic Console → API keys → create a key per environment.
2. Set `Ai:ApiKey` (or `ANTHROPIC_API_KEY`).
3. Set `Assistant:Provider=Anthropic` for the seller assistant and/or `Ai:Provider=Anthropic` for phone and SMS.
4. Configure your data-retention terms (`docs/ASSISTANT.md` §7).

**Guards:**
* With `Phone:Enabled`, `Ai:Provider=Anthropic` without a key fails at startup.
* `Assistant:Provider=Anthropic` without a key has **no** startup guard; the first model call fails.
* Staging with `Assistant:Provider=Fake` keeps the scripted assistant **on**; only Production forces it off.

**Verify:** `ANTHROPIC_API_KEY=… dotnet test tests/Solvelt.Api.Evals` (not in `Solvelt.slnx` and not in CI; spends real money; `Assistant__Model=claude-haiku-5-5` is cheaper). See `docs/ASSISTANT-EVALS.md`.

---

## 21. Passwords (HaveIBeenPwned)

| JSON path | Env var | Dev | Staging | Prod | Default | Meaning |
|---|---|---|---|---|---|---|
| `Passwords:CheckPwned` | `Passwords__CheckPwned` | `false` | `true` | **`true`** | `false` | Checks the k-anonymity range API (needs outbound HTTPS to `api.pwnedpasswords.com`). No key is needed. |

---

## 22. Admin seed and staff panel

| JSON path | Env var | Default | Meaning |
|---|---|---|---|
| `AdminSeed:Email` / `AdminSeed:Password` | `AdminSeed__Email` / `AdminSeed__Password` | empty | Creates the first Super admin at startup if missing (secret). Remove it after first sign-in. |
| `Admin:SessionMinutes` / `IdleMinutes` / `StepUpMinutes` / `InviteDays` | | `60` / `15` / `5` / `3` | Staff sessions |
| `Admin:IpAllowlist` | `Admin__IpAllowlist__0` … | `[]` | IPs/CIDRs allowed for staff sign-in and `/api/v1/admin` (`403 ip_not_allowed`). Recommended in production. |
| `Admin:ReadsPerMinute` / `WritesPerMinute` / `RevealsPerMinute` | | `240` / `60` / `20` | Staff rate limits |
| `Admin:MaxExportRows` | | `50000` | |

The first admin enrols 2FA (`auth/2fa/setup`) and invites the others. The allowlist needs correct client IPs, so set `App:TrustedProxies` (§23).

---

## 23. Reverse proxy and TLS

Run behind TLS. Set `App:TrustedProxies` (`App__TrustedProxies__0=10.0.0.5`, single IP addresses only, no CIDR) to the proxy's address. Only then does the app honour `X-Forwarded-For/Proto`, which per-IP rate limits, the admin IP allowlist and HTTPS redirection need.

The Phone and Shipping webhooks use their own `PublicBaseUrl` settings instead of the request host.

---

## 24. Rate limits

All are fixed one-minute windows.

| Key | Default | Partition |
|---|---|---|
| `RateLimit:AuthPerMinute` | 20 | IP (auth endpoints) |
| `RateLimit:SensitivePerMinute` | 10 | IP (sensitive account actions) |
| `RateLimit:TeamPerMinute` | 60 | team endpoints |
| `RateLimit:CatalogPerMinute` | 240 | seller products |
| `RateLimit:OrdersPerMinute` | 300 | user |
| `RateLimit:InternalPerMinute` | 1200 | IP (marketplace internal API) |
| `RateLimit:InternalCatalogPerMinute` | 1200 | IP (catalog feed) |
| `RateLimit:InternalQuestionsPerListingPerMinute` / `InternalQuestionsPerCallerPerMinute` | 60 / 2000 | verified caller + listing / caller |
| `RateLimit:InternalReportsPerTargetPerMinute` / `InternalReportsPerCallerPerMinute` | 60 / 2000 | verified caller + target / caller |
| `RateLimit:InternalUnsignedPerMinute` | 60 | IP, failed verifications only |
| `RateLimit:BookingsPerMinute` | 300 | user |
| `RateLimit:AvailabilityPerMinute` / `AvailabilityFailedPerMinute` | 600 / 60 | service (verified) / IP (failed) |
| `RateLimit:PaymentsPerMinute` | 120 | user |
| `RateLimit:ShippingPerMinute` | 240 | user |
| `RateLimit:AssistantPerMinute` | 30 | user |
| `Payments:WebhooksPerMinute` | 1200 | IP (Stripe webhook) |
| `Shipping:WebhooksPerMinute` | 600 | IP (Shippo webhook) |
| `Phone:NumberPerMinute` / `IpPerMinute` | 30 / 600 | caller number / IP |
| `Admin:ReadsPerMinute` / `WritesPerMinute` / `RevealsPerMinute` | 240 / 60 / 20 | staff |

The marketplace is one caller for all shoppers. Size `InternalPerMinute` and `InternalCatalogPerMinute` for its peak traffic.

---

## 25. Background jobs, retention and other business settings

Every worker is off in the Testing environment. With several instances, leases keep the jobs apart. Keep the jobs on for **at least one** instance.

| Key | Default | What |
|---|---|---|
| `Orders:RunBackgroundJobs` | `true` | `OutboxWorker` every 10 s: outbox dispatcher, order maintenance (auto-accept, redaction, nonce and draft cleanup) **and the catalog refresh** (listing versions and `listing.*` events). Turning it off also stops the catalog feed from updating. |
| `Payments:RunBackgroundJobs` | `true` | Release, transfers, reconciliation |
| `Shipping:RunBackgroundJobs` | `true` | Tracking poller, refund checks, stuck purchases |
| `Bookings:RunBackgroundJobs` | `true` | Holds, auto-decline, quotes, reminders, redaction (every 30 s) |
| `Assistant:RunBackgroundJobs` | `true` | Assistant retention and stale-action sweep (hourly) |
| (no flag) | – | Local-delivery maintenance (every minute) |

**Retention**

| Key | Default | Effect |
|---|---|---|
| `Orders:RedactAfterDays` | 90 | Buyer PII of finished orders and conversations redacted, attachments deleted |
| `Bookings:RedactAfterDays` | 30 | Customer data of ended bookings wiped |
| `Assistant:RetentionDays` | 90 | Conversations deleted |
| `LocalDelivery:LocationRetentionHours` | 24 | Driver positions deleted |
| `Orders:TimestampWindowSeconds` × 2 | 600 s | Replay nonces kept |

**Other `Orders` keys:**

| Key | Default |
|---|---|
| `FeeBasisPoints` (the staff platform setting overrides it) | 1000 |
| `AutoAcceptMinutes` | 0 (off) |
| `DefaultHandlingDays` | 2 |
| `ReturnWindowDays` | 30 |
| `ManagerRefundLimitCents` | 0 |
| `ReplyDueHours` | 24 |
| `BlockContactDetails` | false |
| `MaxAttachmentBytes` | 5 MB |
| `MaxAttachmentsPerMessage` | 5 |
| `StreamRecheckSeconds` | 15 |

**Other `Bookings` keys:** `HoldMinutes` 10, `DefaultTimeZone` `UTC`, `MaxAvailabilityDays` 62, `IcsIncludeBuyerName` false.

---

## 26. Production go-live checklist

Work through these in order. Rehearse each step in test-mode staging first.

1. **Environment:** `ASPNETCORE_ENVIRONMENT=Production`. Have a separate secret store with no values shared with staging (§1.2).
2. **Database:** PostgreSQL with TLS. Set `Database:Provider=Postgres` and the connection string, run `dotnet ef database update` (or `AutoMigrate` on a single instance), and run the pre-migration checks in `docs/SERVICES.md` §14.
3. **Core secrets:** `App:HmacSecret`, `Jwt:PrivateKeyPem` + `Jwt:KeyId` + `Jwt:Issuer`, `DataProtection:KeysPath` on a persistent shared volume, and `Storage:LocalRoot` on a persistent shared volume (or a blob `IFileStorage`).
4. **URLs:**
   * `App:FrontendBaseUrl`, `App:CorsOrigins`, `App:MarketplaceUrl`;
   * `Passkeys:RelyingPartyId` + `AllowedOrigins`;
   * SPA and API on the same site;
   * `App:TrustedProxies` set, with TLS at the proxy.
5. **Email:** provider (`Smtp`/`Resend`) and credentials, a verified sender domain with SPF/DKIM/DMARC, and a real monitored `Email:SupportAddress`.
6. **Turnstile:** a production widget with both keys. `UseTestStub` stays off.
7. **Passwords:** `Passwords:CheckPwned=true`.
8. **Marketplace contract:**
   * generate `Orders:InboundSecret` and `Orders:OutboundSecret` (different) and share both with the marketplace over a secure channel;
   * set `Orders:WebhookUrl`;
   * confirm NTP on both sides;
   * test one signed call each way (`scripts/post-signed.sh`; the marketplace verifies a webhook).
9. **Portal-only signing keys:** `Shipping:LinkKey` and `LocalDelivery:ProofUrlKey`, each a new random value, different from each other and from the Orders secrets. Set `Shipping:PublicBaseUrl=https://api.example.com`.
10. **Stripe Connect:**
    * platform activated, Connect Express, branding, payouts and tax settings;
    * a live restricted key;
    * webhook endpoints and events (§16.2; read the one-secret gap);
    * `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET` and `STRIPE_CONNECT_WEBHOOK_SECRET`, with the `Allow…` flags false;
    * the marketplace sends `payment.captured`, `refund.processed` and `dispute.*`.
11. **Shippo:**
    * live token, payment method, carrier accounts;
    * webhook `track_updated` with `?token=` (new `Shipping:WebhookSecret`);
    * `SpendLimitCentsPerMonth`;
    * the marketplace consumes the shipping events and `GET /api/internal/v1/shipping/settings`.
12. **Local delivery** (if offered): seller settings and drivers (`docs/LOCAL-DELIVERY.md` §11). The marketplace calls eligibility and fetches proof links within `ProofUrlMinutes`.
13. **Optional sign-in providers:** a Google OAuth client (production redirect URI), and Apple (domain file, Services ID, key) with `AppleEnabled=true`.
14. **Optional phone/SMS:**
    * provider credentials and `SigningKey`;
    * `PublicBaseUrl`;
    * webhooks on the number;
    * A2P 10DLC or toll-free verification approved;
    * `Phone:Enabled=true`.
15. **Optional AI:** `Ai:ApiKey`, plus `Assistant:Provider=Anthropic` and/or `Ai:Provider=Anthropic`. Keep the kill switch procedure handy.
16. **Admin:** `AdminSeed` for the first Super admin. Enrol 2FA, invite staff, remove `AdminSeed`, and set `Admin:IpAllowlist`.
17. **Jobs:** `RunBackgroundJobs=true` on at least one instance for Orders, Payments, Shipping, Bookings and Assistant. Review rate limits (§24) and retention (§25).
18. **Smoke test after deploy:**
    * `GET /health`;
    * a sign-in (proves the JWT key);
    * forgot-password email;
    * a signed `GET /api/internal/v1/catalog/categories`;
    * one outbox event delivered;
    * Stripe onboarding for a staff-owned seller and a small live transfer;
    * one live label (void it right away);
    * Admin → Integrations shows no inbound rejections, no dead outbox messages and no failed jobs.
19. **Monitor** during the first weeks:
    * dead outbox messages and inbound rejection spikes (clock drift, rotated secret);
    * failed transfers and payouts, accounts with requirements past due, reconciliation flags;
    * failed label refunds and webhook failures;
    * job runs with `LastSucceeded=false`.
