# Integration setup

Secrets: **dev** → `dotnet user-secrets set "Section:Key" "value"` in `src/Solvelt.Api`; **prod** → environment variables (`Section__Key`) or a secret manager
(Azure Key Vault, AWS Secrets Manager, etc. mapped to the same keys). Never commit real values.

## Google OAuth (OIDC)
1. Google Cloud Console → *APIs & Services → OAuth consent screen*: External, app name, support email, authorised domain, scopes `openid email profile`; publish.
2. *Credentials → Create credentials → OAuth client ID → Web application*.
3. Authorised redirect URI: `https://<api-host>/signin-google` (dev: `https://localhost:5001/signin-google`). JavaScript origins: the frontend origin.
4. Set `ExternalAuth:GoogleClientId` and `ExternalAuth:GoogleClientSecret`. The scheme uses the code flow with PKCE, state and nonce.

## Sign in with Apple (feature flag, off by default)
1. Apple Developer → Identifiers: create an **App ID** with *Sign in with Apple*.
2. Create a **Services ID** (e.g. `com.example.seller.web`) → configure Sign in with Apple: primary App ID, **domain** (download and host the verification file at `/.well-known/apple-developer-domain-association.txt`), **return URL** `https://<api-host>/signin-apple`.
3. Keys → create a key with Sign in with Apple; download the **.p8** once; note the **Key ID**; your **Team ID** is in Membership.
4. Set `ExternalAuth:AppleClientId` (Services ID), `AppleTeamId`, `AppleKeyId`, `ApplePrivateKeyPem` (contents of the .p8), then `ExternalAuth:AppleEnabled=true`.
   The client secret JWT (ES256, 5 min) is generated per token request.

## Email
`Email:Provider` = `Smtp` | `Resend` | `Sink` (in-memory; Testing/dev only).
* **SMTP (MailKit)**: `SmtpHost`, `SmtpPort`, `SmtpUseSsl` (STARTTLS), `SmtpUser`, `SmtpPassword`, `FromAddress`.
* **Resend (HTTP API)**: create an API key with *Sending access*, set `Email:ResendApiKey`; verify the sending domain in Resend.
* **Sender domain**: publish **SPF** (`v=spf1 include:<provider> -all`), **DKIM** (CNAME/TXT records from the provider) and **DMARC** (`_dmarc` TXT `v=DMARC1; p=quarantine; rua=mailto:dmarc@yourdomain`). Send from a subdomain such as `mail.example.com`.

## Cloudflare Turnstile
Dashboard → Turnstile → Add widget (managed) for the frontend domain. Put the **site key** in `Turnstile:SiteKey` (served to the SPA via `/api/v1/public-config`) and the **secret** in `Turnstile:SecretKey`.
Cloudflare test keys (dev/tests): site `1x00000000000000000000AA`, secret `1x0000000000000000000000000000000AA` (always passes); secret `2x0000000000000000000000000000000AA` always fails.
Captcha is only demanded after the 4th failed login for an email.

## File storage
Certificates go through `IFileStorage`. `LocalFileStorage` writes to `Storage:LocalRoot` (not web-served; files are streamed only to their owner). In production mount a persistent volume
(or implement `IFileStorage` for S3/Azure Blob and register it in `Program.cs`).

## Phone, SMS and the AI assistant (SignalWire / Twilio / Anthropic)
Full design: `docs/PHONE-SMS.md`. Set `Phone:Enabled=true` and the keys below (env form `Phone__AccountSid`, ...).

### SignalWire (default)
1. Create a Space (`<name>.signalwire.com`) and a Project. **Phone Numbers → Buy** a number with Voice + SMS (US 10DLC: local number; or a toll-free number).
2. **API → Credentials**: Project ID → `Phone:AccountSid`, API token → `Phone:AuthToken`, space host → `Phone:Space`, the number (E.164) → `Phone:FromNumber`. **Settings → Signing Key** → `Phone:SigningKey`.
3. Number settings → **Voice**: "Handle calls using LaML Webhooks", `https://<api-host>/api/v1/channels/voice/incoming` (POST), status callback `https://<api-host>/api/v1/channels/voice/status`.
   **Messaging**: LaML Webhooks, `https://<api-host>/api/v1/channels/sms/inbound` (POST). Our outgoing messages set the status callback `https://<api-host>/api/v1/channels/sms/status`.
4. `Phone:PublicBaseUrl=https://<api-host>` (must match the URL configured at the provider exactly: it is part of the signature). Keep call recording off.
5. **US A2P 10DLC** (required to text from a local US number): Brand registration (EIN/legal entity), then a Campaign (use case "Account notification / 2FA"; sample messages: the code and link texts from `PHONE-SMS.md`; opt-in: web card "Phone number" and "Text JOIN/LOGIN"; opt-out STOP; help HELP), then attach the number to the campaign. Approval takes days; unregistered traffic is filtered.
   **Alternative: toll-free verification** (submit use case, opt-in flow screenshots, sample messages): faster, no brand/campaign.
6. Messages must include "Msg&data rates may apply" and STOP/HELP handling, which the server implements.

### Twilio
Same code, `Phone:Provider=Twilio`: Account SID → `AccountSid`, Auth Token → `AuthToken` (also the signing key). Webhook URLs as above (Console → Phone Numbers → Active numbers → Voice/Messaging "Webhook, HTTP POST"). Register A2P 10DLC under Messaging → Regulatory Compliance.

### Anthropic API key (AI assistant)
Create a key in the Anthropic Console, then `Ai:Provider=Anthropic` and `Ai:ApiKey` (or `ANTHROPIC_API_KEY`). `Ai:Model` (default `claude-opus-5-5`); for lower call latency set `Ai:VoiceModel=claude-haiku-5-5`. Without it the deterministic keyword assistant (`Fake`) is used; the keypad menu and all SMS keywords work either way.
Dev without a provider: `Phone:Provider=Sink` and the simulator (`docs/PHONE-SMS.md`).

## PostgreSQL + migrations
```bash
export Database__Provider=Postgres Database__ConnectionString="Host=...;Database=solvelt;Username=...;Password=..."
dotnet tool restore
dotnet ef database update --project src/Solvelt.Api
dotnet ef migrations add <Name> --project src/Solvelt.Api --output-dir Migrations   # after model changes
```
Migrations target PostgreSQL. SQLite (dev/tests) is created with `EnsureCreated`.

## JWT signing keys and rotation
RS256, `kid` header. Generate: `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt.pem` → `Jwt:PrivateKeyPem` (PKCS#8 PEM), pick a new `Jwt:KeyId`.
Rotate: move the old public key (`openssl pkey -in old.pem -pubout`) into `Jwt:PreviousPublicKeys` (`{ "<old-kid>": "<PEM>" }`), deploy with the new private key; remove the old entry after the access-token lifetime (60 min) has passed.
Without a configured key, non-Production environments generate an ephemeral key (tokens die on restart); Production refuses to start.

## Passkeys
`Passkeys:RelyingPartyId` = registrable domain of the frontend (e.g. `example.com`; `localhost` in dev). `Passkeys:AllowedOrigins` = exact frontend origins (scheme+host+port). Changing the RP ID invalidates existing passkeys.

## Inactivity timing
Frontend `public/config.json` (served at runtime, editable without rebuild): `idleTimeoutMinutes`, `warningSeconds`, `refreshIntervalMinutes`.
Backend `Session:IdleTimeoutMinutes` is the server-side limit (default 20, frontend default 15 + 60 s warning). A session idle longer than the server limit is revoked on the next request/refresh.
