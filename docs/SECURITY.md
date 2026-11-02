# Security: threat model and controls

## Assets
Seller credentials and sessions, seller PII (business contact, phone, address), kosher certificates, approval state.

## Threats → controls
| Threat | Controls |
|---|---|
| Credential stuffing / brute force | Per-email counter (works for unknown emails), lock at 5, Turnstile at 4, per-IP rate limits, equalised timing for unknown users, lockout email + session revocation |
| Account enumeration | Identical 401 body/hint for real/fake accounts; generic forgot-password, change-email and passkey option responses |
| Token theft (XSS) | Access token in memory only; refresh token HttpOnly cookie; strict CSP on the SPA; short access lifetime |
| CSRF | SameSite=Strict, double-submit header, Origin allow-list, CORS allow-list, JSON-only API |
| Refresh token replay | Rotation + reuse detection revokes the session |
| Session hijack / idle | Server session table, idle limit, revoke-all on password change/reset/lock, per-session revoke |
| Phishing | Passkeys (WebAuthn, RP-bound), optional TOTP |
| Account pre-hijacking | Unverified local account loses password/sessions when the real owner signs in via provider |
| Open redirect | `SafeRedirect` on login-return and external flows |
| Malicious upload | Size/type/magic-byte checks, random storage keys, private storage, attachment + nosniff, owner-only |
| Injection | EF Core parameterised queries; server-side validation; HTML-encoded email templates |
| Info leak | ProblemDetails, no stack traces, security headers (CSP, frame-ancestors, nosniff, Referrer-Policy, HSTS, COOP), `Cache-Control: no-store` on API |
| Secret leakage | Secrets via user-secrets/env only; logs never include passwords, tokens or codes; codes/tokens stored hashed |
| Privilege escalation | Role policies (`Admin`, `Seller`, `VerifiedEmail`); admin accounts only via seed config |

## Residual risks / operator duties
Persist Data Protection keys; run behind TLS and set `App:TrustedProxies`; enable `Passwords:CheckPwned`; rotate JWT keys; monitor `AuditEvents`;
Turnstile/email/Google/Apple require real credentials and were not exercised against live services in CI.
