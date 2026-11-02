# Roadmap (out of scope for this iteration)

Each item lists a short design note so it can be added later.

* **Admin UI (approval queue, staff roles, admin login, mandatory admin 2FA, impersonation, audit viewer)** – add `AdminUser` roles + a separate admin SPA/origin; reuse `SellerStatusService`; require TOTP/passkey for the `Admin` role at login; impersonation as a short-lived, audited token with `act` claim; viewer reads `AuditEvents` with paging/filters.
* **Business switcher / multi-brand** – split `SellerAccount` into `Organization` + `Brand`, add membership table; token carries active `org` claim and a `switch` endpoint reissues it.
* **Shop staff invitations and roles** – `Membership(userId, orgId, role)` + signed single-use invitation tokens by email; role claims scoped per org.
* **Guest mode** – anonymous browsing token with read-only scope; the placeholder `/guest` route is the entry point.
* **Account picker** – remember last N accounts per device (non-secret hints in a cookie) and drive `login_hint`/passkey allow-lists.
* **Seller presence / online status** – SignalR hub or heartbeat endpoint updating `LastSeenAt`; expose via cached query.
* **Onboarding checklist** – `OnboardingTask` rows per seller, evaluated by status events, rendered on the dashboard.
* **SMS features (phone verification, new-device code, trusted devices, New sign-in email)** – implement `ISmsSender` (Twilio/etc.), store verified phone, add device fingerprint cookie + `TrustedDevice` table, challenge when unknown, send "New sign-in" email via a new template. Consent timestamps already stored.
* **Magic link / email code login** – reuse `OneTimeCodeService` with purpose `login`, same throttle and captcha rules, no enumeration (generic response).
* **Save-and-resume reminders after 3 days** – background `IHostedService` querying drafts untouched ≥3 days; send reminder email once with deep link to the wizard step (`DraftStep`).
