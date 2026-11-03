# Staff admin panel (workstream G)

The back-office for **Solvelt staff** (not sellers). Backend: `src/Solvelt.Api/Admin/**`, API under `/api/v1/admin/...`.
Frontend: `/admin` area of the portal (see "Frontend" below). Design decisions: `docs/DECISIONS.md` #129-#143.

## Staff roles and the permission matrix

One central table, `StaffPermissions.Matrix` (`Admin/AdminModel.cs`); every admin endpoint declares exactly one permission and the test
`Role_matrix_every_role_against_every_endpoint` runs **every role against every endpoint**
(`Matrix_table_covers_every_admin_route` fails if a route is added without being in the matrix test).

| Permission | Super admin | Support | Moderator | Read-only | Step-up |
|---|:-:|:-:|:-:|:-:|:-:|
| View dashboard | ✔ | ✔ | ✔ | ✔ | |
| View applications / seller accounts / users | ✔ | ✔ | ✔ | ✔ | |
| Approve / needs-info / reject applications, notes | ✔ | | ✔ | | |
| Assign applications to a reviewer | ✔ | | ✔ | | |
| Suspend / reactivate a seller | ✔ | | ✔ | | |
| Close a seller account | ✔ | | | | ✔ |
| Resend owner verification | ✔ | ✔ | | | |
| Emergency ownership transfer | ✔ | | | | ✔ |
| User actions: sign out everywhere, clear lockout, send password reset, remove phone | ✔ | ✔ | | | |
| Disable a user's 2FA (last resort) | ✔ | | | | ✔ |
| View moderation queue / listings | ✔ | ✔ | ✔ | ✔ | |
| Moderation actions: hide/unhide, dismiss, warn, escalate-suspend | ✔ | | ✔ | | |
| Support lookups (orders, bookings, quotes, conversations; masked) | ✔ | ✔ | ✔ | ✔ | |
| **Reveal** personal data (break-glass, reason, audited) | ✔ | ✔ | | | |
| View integration health | ✔ | ✔ | ✔ | ✔ | |
| Retry a dead-lettered event | ✔ | ✔ | | | |
| Discard a dead-lettered event | ✔ | | | | ✔ |
| View audit log | ✔ | ✔ | ✔ | ✔ | |
| Export audit log (CSV) | ✔ | | | | ✔ |
| View platform settings | ✔ | ✔ | ✔ | ✔ | |
| Edit platform settings (fee, legal versions, wizard fields) | ✔ | | | | ✔ |
| View staff list | ✔ | | | | |
| Invite staff, change roles, deactivate/reactivate | ✔ | | | | ✔ |

The legacy Identity role `Admin` maps to **Super admin** (backfilled at start-up). It is only ever *removed* afterwards (demotion/deactivation): new Super admins do **not**
receive it, because it opens the one old API that stays outside the staff gate, `POST /api/v1/admin/sellers/{id}/status` (seller-kind session, no 2FA, no audit).
The older order-admin routes `GET admin/outbox`, `POST admin/outbox/{id}/retry`, `GET admin/messages/reported`, `POST admin/messages/{id}/dismiss-report` are now behind the staff gate
(`IntegrationsView/Act`, `ModerationView/Act`; reasons + audit; text masked unless the role can act): a seller-kind session answers `403 not_staff_session`.

## Access model (ASVS L2)
* **Separate session context.** Staff sign in at `POST /admin/auth/login` (password **and** TOTP/recovery code). That creates a `UserSession` of
  kind `staff` with `MfaAt` set, **no refresh token**, no seller account and no seller roles in its token. The gate (`AdminGate`) accepts only such
  sessions: a seller token (even of the same person, or of an `Admin`-role user signed in the ordinary way) answers `403 not_staff_session`;
  a staff token reaches no seller endpoint (`403`), unless the person also has a seller session of their own.
* **2FA is mandatory.** A staff account without TOTP gets `403 two_factor_setup_required`; it enrols with `POST /admin/auth/2fa/setup` + `/2fa/enable`
  (password + first code; 10 recovery codes returned once). Sessions without `MfaAt` are refused (`two_factor_required`).
  Passkeys: *not yet* a staff second factor (ROADMAP).
* **Short sessions.** Absolute lifetime `Admin:SessionMinutes` (60), idle timeout `Admin:IdleMinutes` (15); expiry is enforced on every request.
* **Step-up.** Permissions marked above need `POST /admin/auth/step-up` (TOTP/recovery code) within `Admin:StepUpMinutes` (5): else `403 step_up_required`.
* **Staff roles are re-read from the database on every request**: deactivation or a role change applies at once (and deactivation revokes sessions).
* **IP allowlist** (optional): `Admin:IpAllowlist` = IPs / CIDR ranges; applies to sign-in and every admin endpoint (`403 ip_not_allowed`).
* **Rate limits** per staff member after authentication: `Admin:ReadsPerMinute` 240, `WritesPerMinute` 60, `RevealsPerMinute` 20 (`429 rate_limited`);
  sign-in shares the `auth` per-IP limiter and the normal login lockout.
* **CSRF:** the admin API uses `Authorization: Bearer` only (no cookies), like the rest of the app, so there is nothing to forge.
* **Staff tokens work on `/admin` only**: a staff JWT (`ctx=staff`) is refused (401) on seller, account, team and session endpoints.
* **Acting on staff accounts** (sign out, clear lockout, password reset, remove phone) is a staff-management action: Super admin with a recent step-up; Support gets `403 staff_target`.
* **Seller-facing text is separate from the internal reason**: the reason stays in the audit log; the seller only sees a message staff wrote for them (needs-info message, optional reject message).
* **Concurrency:** role changes/deactivations that can reduce the Super admin count run under a process lock (+ PostgreSQL advisory lock) that re-reads the acting Super admin and the count.
* **Invariants:** a staff member cannot change their own role / access / 2FA; at least one active Super admin always remains (service rule).

## Reasons and audit
* Every write needs a `reason` (5-500 characters; `422 reason_required`). Every write, every step-up, every file view, every moderation view and every
  PII reveal writes an `AuditEvent` with `Type = admin.<action>`, actor id + role, `TargetType`/`TargetId`, `Reason`, IP and (where useful) JSON
  `before`/`after`. Audit rows never contain passwords, tokens, codes, PINs, HMAC secrets or revealed values.
* **Audit viewer** `GET /admin/audit` filters: `actor` (user id or e-mail fragment), `target`, `targetType`, `action` (exact or prefix, with/without
  the `admin.` prefix), `from`/`to`, `staffOnly`, paging. **Export** `GET /admin/audit/export` (Super admin, step-up) is CSV, every cell quoted and
  **formula-safe** (cells starting `= + - @ TAB CR` get a leading `'`), `nosniff`, `attachment`, itself audited, capped by `Admin:MaxExportRows`.

## PII masking and break-glass reveal
Support lookups (`/admin/lookups/{orders|bookings|quotes|conversations}`) return masked names (`J*** B***`), e-mails (`r***@r***.example`), phones
(`****11`), postal codes (`11***`), hide street lines and mask e-mails/phones inside message text. `POST .../reveal {reason}` (Super admin, Support) returns the
unmasked record; the audit row `admin.pii.revealed` (reason, record, field names, no values) is written before the data leaves; reveals are rate limited.
No impersonation / "log in as" exists (ROADMAP).
Moderators and Super admins (roles that can act on reports) read the reported message and up to 5 preceding messages in full; Support and Read-only see them masked. Each moderation view is audited (`admin.moderation.viewed`).

## Features and endpoints
| Area | Endpoints |
|---|---|
| Staff access | `auth/login`, `auth/2fa/setup|enable`, `auth/accept-invite`, `auth/me`, `auth/step-up`, `auth/logout`; `staff` (list), `staff/invitations` (+ `/{id}/revoke`), `staff/{id}/role`, `/deactivate`, `/reactivate` |
| Dashboard | `dashboard`: applications waiting (+ oldest/average age), new sellers 7/30 days, active listings, orders/bookings today, open reports, dead/retrying events, inbound rejects, SMS/phone volume, 14-day trend |
| Applications | `applications` (status/country/minAgeDays/assigned/q), `applications/{id}` (business, kosher answers, certificate list, legal versions + timestamps, notes, status history), `applications/{id}/files/{fileId}` (certificate viewer: stored sniffed type only, `nosniff`, sandbox CSP), `approve`, `needs-info {message}`, `reject`, `assign`, `notes` |
| Seller accounts | `sellers` (search by name/e-mail/id), `sellers/{id}` (members, brands, counts, status history, activity), `suspend`, `reactivate`, `close`, `resend-verification`, `transfer-ownership` |
| Users | `users?q=` (e-mail, phone, id), `users/{id}` (memberships, sessions, 2FA/passkey status, lockout, recent security events), `sign-out-everywhere`, `clear-lockout`, `send-password-reset`, `remove-phone`, `disable-2fa` |
| Moderation | `moderation` (queue), `moderation/{id}`, `dismiss`, `warn-seller`, `escalate-suspend`; `listings/{id}` (read-only), `listings/{id}/hide|unhide` |
| Support lookups | `lookups/{kind}?q=`, `lookups/{kind}/{id}`, `lookups/{kind}/{id}/reveal` |
| Integration health | `integrations/outbox` (+ `/{id}`, `/retry`, `/discard`), `integrations/inbound`, `integrations/phone`, `integrations/jobs` |
| Audit | `audit`, `audit/export` |
| Settings | `settings`, `settings/fee`, `settings/legal`, `settings/seller-fields` (+ `/preview`) |
| Marketplace (signed, `/api/internal/v1`) | `listings/{id}/reports`, `booking-messages/{id}/reports` |

### Moderation queue sources
Buyer-reported order messages (`OrderMessage.Reported`) and contact-detail warnings (`ContactWarning`) are turned into queue rows on read (idempotent);
the marketplace reports listings and booking messages through the signed internal endpoints above (a repeat report of a resolved item reopens it).
Hiding a listing sets `Product.ModeratedAt/Reason`, moves an Active listing to Inactive, mails the owners with the reason, shows `moderation: {hidden, reason}`
in the seller's product detail and blocks reactivation (`409 hidden_by_staff`) until staff unhide it (then an Active listing is restored if the account is still approved).

### Integration health
`InboundVerifier` records `inbound.rejected` (`bad_signature`, `stale`, `replayed`, `bad_timestamp`, `unsigned`; no payload) so the page can count causes;
`SmsService` records `sms.sent|blocked|failed`; the background workers record each run in `job_runs` through `JobRecorder` (last run, result, failures).
Outbox payload previews are PII-redacted (`PayloadRedactor`: personal field names → `[redacted]`, e-mails/phones masked, ids and numbers kept).

### Platform settings
* **Marketplace fee** (basis points, 0-5000) overrides `Orders:FeeBasisPoints` for orders received afterwards (existing orders keep their fee).
* **Terms / Seller Agreement version bump** changes the version recorded for every *new* acceptance (sign-up, invitation join, application submit) and
  what `/public-config` publishes. There is **no forced re-acceptance flow** for existing sellers yet: the settings page shows how many sellers are
  on an older version (ROADMAP).
* **Seller wizard fields** (`SellerFieldConfig`): full-replace editor with validation (key format, types, select options, branching: the parent exists, comes
  earlier and is yes/no or select; the `kosher_certificate` file field must stay) and a branching **preview** (`preview` returns the fields visible for sample answers).
* **Feature flags** already in configuration are shown read-only (never secrets).

## Operational runbook
**Dead-lettered events.** `integrations/outbox?status=dead`. Open the event (redacted payload, last error). If the marketplace endpoint was down or fixed:
*Retry* (Support or Super admin; reason) - the event returns to Pending with 0 attempts and is delivered by the next dispatcher run. If the event no longer matters
(order cancelled by hand, duplicate): *Discard* (Super admin, step-up, reason) - status `Discarded`, never delivered. Both are audited. Watch
`integrations/inbound` for signature/stale/replay spikes (clock drift, rotated secret) and `integrations/jobs` for a job whose last run is old or failed.

**Suspending a seller.** Moderator/Super admin: `suspend` with a reason (members whose only usable account it is lose their sessions at once; the owners are mailed
by the status service where a template exists). `reactivate` makes the account Approved again (the only active state; `Live` no longer exists). Closing is Super admin + step-up, irreversible
through the panel, owners are mailed. For a stuck account where the owner cannot be reached: `transfer-ownership` (Super admin, step-up; the previous and the new owner are emailed).

**Locked-out / lost-device user.** Verify identity out of band, then Support: `clear-lockout` or `send-password-reset`; for a lost authenticator: Super admin `disable-2fa`
(step-up, reason; the user is signed out everywhere and e-mailed). **Staff lost their device:** another Super admin deactivates and re-invites (or disables 2FA).

**Break-glass reveal.** Only with a ticket reference as the reason; reveals are reviewed through `audit?action=pii.revealed`.

## Frontend
The `/admin` area (own shell, code-split) is built in `Yanky1/test-login-frontend` (`docs/DESIGN.md` there). Status of that part: see `docs/AGENTS.md` row G.

## Configuration
`Admin:SessionMinutes`, `IdleMinutes`, `StepUpMinutes`, `InviteDays`, `IpAllowlist` (array), `ReadsPerMinute`, `WritesPerMinute`, `RevealsPerMinute`, `MaxExportRows`.
Bootstrap: the first Super admin is the `AdminSeed` user (legacy `Admin` role), who enrols 2FA through `auth/2fa/setup` and then invites the others.
