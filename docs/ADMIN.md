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
| Moderation actions: hide/unhide, dismiss, warn, escalate-suspend, hide-content, remove-content | ✔ | | ✔ | | |
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
| Payments: view seller balances, ledger, Stripe status, failed transfers/payouts, accounts past due, reconciliation flags (`PaymentsView`) | ✔ | ✔ | ✔ | ✔ | |
| Payments: place/release a payout hold (`PaymentsHold`) | ✔ | ✔ | | | |
| Payments: retry a failed transfer, run/resolve reconciliation (`PaymentsRetry`) | ✔ | ✔ | | | |
| Payments: manual balance adjustment, credit/debit, seller notified (`PaymentsAdjust`) | ✔ | | | | ✔ |

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
| Moderation | `moderation` (queue; `kind` = `message`, `booking_message`, `listing`, `contact_warning`, `review`, `review_reply`: a reported seller reply, docs/REVIEWS.md), `moderation/{id}` (+ the marketplace `contentReports`), `dismiss`, `warn-seller`, `escalate-suspend`, `hide-content`, `remove-content`; `listings/{id}` (read-only), `listings/{id}/hide|unhide`. Every decision accepts an optional `reasonShownToUser` (≤500 chars, for the person who reported; never derived from the internal `reason`) |
| Support lookups | `lookups/{kind}?q=`, `lookups/{kind}/{id}`, `lookups/{kind}/{id}/reveal` |
| Local delivery (read-only; `DeliveryView`, proof access needs `LookupsReveal` + reason) | `sellers/{id}/local-delivery`, `local-delivery/deliveries/{id}/proof-access` (see `docs/LOCAL-DELIVERY.md`) |
| Integration health | `integrations/outbox` (+ `/{id}`, `/retry`, `/discard`), `integrations/inbound`, `integrations/phone`, `integrations/jobs` |
| Audit | `audit`, `audit/export` |
| Settings | `settings`, `settings/fee`, `settings/legal`, `settings/seller-fields` (+ `/preview`) |
| Payments (`docs/PAYMENTS.md`) | `payments/sellers/{id}` (+ `/ledger`, `/hold`, `/hold/release`, `/adjustments`), `payments/transfers` (+ `/{id}/retry`), `payments/payouts`, `payments/accounts/requirements-past-due`, `payments/reconciliation` (+ `/run`, `/{id}/resolve`) |
| Marketplace (signed, `/api/internal/v1`) | `listings/{id}/reports`, `booking-messages/{id}/reports`, `content/reports` (`docs/CATALOG.md`) |

### Moderation queue sources
Buyer-reported order messages (`OrderMessage.Reported`) and contact-detail warnings (`ContactWarning`) are turned into queue rows on read (idempotent);
the marketplace reports listings and booking messages through the signed internal endpoints above (a repeat report of a resolved item reopens it).
Hiding a listing sets `Product.ModeratedAt/Reason`, moves an Active listing to Inactive, mails the owners with the reason, shows `moderation: {hidden, reason}`
in the seller's product detail and blocks reactivation (`409 hidden_by_staff`) until staff unhide it (then an Active listing is restored if the account is still approved).

### Marketplace content reports and the decision event
`POST /api/internal/v1/content/reports {reportId, kind: review|listing|message, targetId, reason, note?, reporterRef}` (idempotent on `reportId`) puts the report into this queue: one row per
target (listing reports reuse the "report listing" row, message reports flag the message first, review reports are rows of kind `review`; the portal stores only the report and its decision for
reviews). A repeat report of a decided target reopens the row; every marketplace report keeps its own decision. When staff decide the row, ONE outbox event per undecided marketplace report is
written in the same transaction as the decision and the audit entry:

| Event | `data` |
|---|---|
| `content.report_decided` | `reportId`, `kind`, `targetId`, `decision` (`dismissed` / `hidden` / `removed`), `reasonShownToUser` (staff-written text for the reporter or `null`) |

| Action | decision | Effect |
|---|---|---|
| `dismiss` | `dismissed` | row closed |
| `hide-content` | `hidden` | listing: hidden like `listings/{id}/hide`; review/message: decision only (the marketplace acts on its content) |
| `remove-content` | `removed` | listing: archived and kept unlisted (`moderated`); review/message: decision only |
| `listings/{id}/hide` (existing) | `hidden` | decides every open marketplace report of that listing |
| `warn-seller` | `dismissed` | the content stays; the seller is warned |
| `escalate-suspend` | `removed` | the seller is suspended, so their listings are unlisted |

Every decision is **atomic**: it claims the open report with a conditional update, so when several staff decide the same report at the same moment exactly one wins (`200`) and the others get `409 already_resolved` without any side effect (no event, no audit entry, no seller suspension); each marketplace report is decided, and its event queued, once. `hide-content` on a report about a listing that staff ALREADY hid decides the report as `hidden` without hiding the listing again (`remove-content` still archives it).

`warn-seller` and `escalate-suspend` answer `409 no_seller` for review reports (no seller on this portal). All of these need `ModerationAct`, a reason, and are audited. Hiding, removing
or suspending also unlists the listing from the catalog feed (`listing.unlisted`, `docs/CATALOG.md`).

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
The `/admin` area (own shell, code-split) is built in `Yanky1/test-login-frontend` (`docs/DESIGN.md` there). Status of that part: see `docs/AGENTS.md` rows G and Q.

Screens for the staff tools (worker Q, frontend `src/admin/pages/{Payments,Shipping,Delivery,Moderation,Sellers}.tsx`), each behind the permission its endpoint declares, with the nav item, tab or button hidden for roles that lack it:

| Screen | Where | Permissions |
|---|---|---|
| Seller tabs: Overview, Payments, Shipping, Local delivery | `/admin/sellers/{id}/{tab}` | `PaymentsView`, `ShippingView`, `DeliveryView` (all roles) |
| Seller payments: balances, Stripe status, holds, ledger with filters (type, dates, order, booking, number) | seller tab | view: `PaymentsView`; hold and release: `PaymentsHold`; manual adjustment (integer minor units, confirm dialog, step-up): `PaymentsAdjust` |
| Payments lists: transfers (retry), payouts, accounts past due, reconciliation (run, resolve), unallocated disputes | `/admin/payments/{tab}` | view: `PaymentsView`; retry, run, resolve: `PaymentsRetry` |
| Shipping: seller shipments and labels; refunds by state, exceptions, webhook failures; label refund retry | seller tab, `/admin/shipping/{tab}` | view: `ShippingView`; retry: `ShippingRetry` |
| Local delivery: settings, zones, windows, routes, deliveries; proof of delivery (break-glass: reason, audited, shown only after the reveal and never kept) | seller tab | `DeliveryView`; proof: `LookupsReveal` |
| Moderation: every report kind with its context (including `review`, `review_reply` and the marketplace reports), hide-content, remove-content, warn-seller, escalate-suspend, dismiss; `409 already_resolved` is shown as "another moderator already resolved this report" | `/admin/moderation/{id}` | `ModerationView`; actions: `ModerationAct` |

Every write goes through the reason dialog (the reason is the audit-log reason); `step_up_required` opens the authenticator prompt and repeats the write. No backend change was needed for these screens.

## Configuration
`Admin:SessionMinutes`, `IdleMinutes`, `StepUpMinutes`, `InviteDays`, `IpAllowlist` (array), `ReadsPerMinute`, `WritesPerMinute`, `RevealsPerMinute`, `MaxExportRows`.
Bootstrap: the first Super admin is the `AdminSeed` user (legacy `Admin` role), who enrols 2FA through `auth/2fa/setup` and then invites the others.
