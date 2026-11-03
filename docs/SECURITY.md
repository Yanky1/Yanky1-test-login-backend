# Security: threat model and controls

## Assets
Seller credentials and sessions, seller PII (business contact, phone, address), kosher certificates, approval state.

## Threats → controls
| Threat | Controls |
|---|---|
| Credential stuffing / brute force | Per-email counter reserved atomically before the password check (works for unknown emails, no parallel overshoot), lock at 5, Turnstile at 4, per-IP rate limits, equalised timing for unknown users, lockout email + session revocation |
| Account enumeration | Identical 401 body/hint for real/fake accounts; sign-up, verify, resend, forgot-password, change-email and passkey option responses do not depend on whether an account exists |
| MFA bypass | Passkeys require user verification; Google/Apple exchange demands TOTP when enabled; passwordless email change needs a code from the current mailbox |
| Multi-tab session loss | Shared activity/logout (BroadcastChannel), Web Locks for logout and refresh, 30 s refresh grace window |
| Token theft (XSS) | Access token in memory only; refresh token HttpOnly cookie; strict CSP on the SPA; short access lifetime |
| CSRF | SameSite=Strict, double-submit header, Origin allow-list, CORS allow-list, JSON-only API |
| Refresh token replay | Rotation + reuse detection revokes the session |
| Session hijack / idle | Server session table, idle limit, revoke-all on password change/reset/lock, per-session revoke |
| Phishing | Passkeys (WebAuthn, RP-bound), optional TOTP |
| Account pre-hijacking | No session until the mailbox is verified; the latest sign-up replaces an unverified account; password reset / provider claim wipes passkeys, logins, TOTP, sessions; passkey/2FA/email-change need a verified email; passkey sign-up binds the email to the attested user |
| Open redirect | `SafeRedirect` on login-return and external flows |
| Malicious upload | Size/type/magic-byte checks, random storage keys, private storage, attachment + nosniff, owner-only |
| Injection | EF Core parameterised queries; server-side validation; HTML-encoded email templates |
| Info leak | ProblemDetails, no stack traces, security headers (CSP, frame-ancestors, nosniff, Referrer-Policy, HSTS, COOP), `Cache-Control: no-store` on API |
| Secret leakage | Secrets via user-secrets/env only; logs never include passwords, tokens or codes; codes/tokens stored hashed |
| Phone/SMS channel abuse | Signed webhooks, replay/seq protection, shared lockout, SMS code + PIN, no enumeration, AI as language layer only with redaction (see `PHONE-SMS.md`) |
| Owner-invariant races (two Owners removing each other) | Per-account lock around every membership change (in-process; `FOR UPDATE` on PostgreSQL). **Production must run PostgreSQL**: SQLite is single-instance dev/test only. Actor role re-read inside the lock |
| Privilege escalation | Role policies (`Admin`, `Seller`, `VerifiedEmail`); admin accounts only via seed config |

## Self-review notes
* Forgot-password mail is sent off the request path (`Email:SendInBackground`) so latency does not reveal account existence.
* Startup refuses to run outside Development/Testing without `App:HmacSecret` and `Jwt:PrivateKeyPem`, and refuses `Turnstile:UseTestStub` outside Testing.
* TOTP codes are accepted once per 30 s window per Identity defaults (no replay cache); failures count toward the login lockout.

* Security review round 2 (items 1-6): see DECISIONS 26-33; every fix has a regression test (xUnit `HardeningTests`, Vitest `multitab`/`security`, Playwright multi-tab, passkey binding and step-up).

## Residual risks / operator duties
Persist Data Protection keys; run behind TLS and set `App:TrustedProxies`; enable `Passwords:CheckPwned`; rotate JWT keys; monitor `AuditEvents`;
Turnstile/email/Google/Apple require real credentials and were not exercised against live services in CI.
| Double booking / slot races | Per-provider calendar lock (atomic UPDATE) + re-validation inside the transaction, unique marketplace booking id, parallel-request test (exactly one wins) |
| Booking IDOR / privilege | Every booking, quote, calendar and provider endpoint resolves account, role and brand scope from the database; foreign or out-of-scope ids are 404; providers act only on their own bookings; viewers are read-only |
| Customer data leaks | Address/phone only for the assigned provider and Owner/Admin from confirmation, retention job, no buyer data in audit, logs, reminder mails or ICS; meeting link only in the confirmed event |
| Calendar feed leaks | 256-bit hashed, revocable ICS tokens, minimal event data, `no-store`, rate limited |
