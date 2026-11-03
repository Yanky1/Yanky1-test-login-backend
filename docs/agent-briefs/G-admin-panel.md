You are a senior full-stack engineer specialising in back-office/admin tooling and security. Build the SOLVELT STAFF ADMIN PANEL, autonomously. No one will answer questions: never end your turn to ask. Decide, record it in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until everything below is built, tested, green, merged and pushed. I'm on a plan with usage limits: work efficiently and push regularly.

## Context
Solvelt is an online multi-seller marketplace. This repo pair is the seller portal (sign-up/approval, team members & brands, products, orders + buyer messaging, phone/SMS, services & bookings). It has no back-office yet beyond a single admin-only seller-status API. You build the back-office for Solvelt STAFF (not sellers; sellers already manage their own team/settings in the portal).
- **Backend (your checkout):** https://github.com/Yanky1/Yanky1-test-login-backend (ASP.NET Core 10, Identity, EF Core; SQLite dev/tests, PostgreSQL prod; xUnit; GitHub Actions).
- **Frontend:** https://github.com/Yanky1/test-login-frontend (React + TS + Vite; Vitest + RTL + MSW; Playwright E2E against the real backend). Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push"). If the permission check refuses it, do NOT work around it: finish the backend completely, report the refusal, and schedule check-ins; the user will grant access.
- Don't touch any other repository. Read both READMEs and every docs/*.md first (AGENTS, DECISIONS, SECURITY, TEAMS, PRODUCTS, ORDERS, SERVICES, PHONE-SMS, DESIGN). Reuse existing services (Identity, SessionService, AccountSecurityService, SellerStatusService, AuditLog, Mailer, IFileStorage, rate limiting, outbox, permission helpers). Don't change seller-facing behaviour or weaken any test.

## Product decisions (made by the product owner)
- The admin panel is for Solvelt staff only. Staff roles: **Super admin** (everything, incl. staff management and platform settings), **Support** (look up and help sellers/users, non-destructive account actions), **Moderator** (reported content, listings, messages, seller applications), **Read-only** (view only). Map the existing `Admin` Identity role to Super admin (migration/backfill), and keep the existing admin seller-status API working.

## Features
1. **Staff access:** separate staff area (`/admin` routes in the frontend with its own shell; `/api/v1/admin/...` in the backend). Staff accounts require 2FA (TOTP or passkey) before any admin endpoint works; short session lifetime and idle timeout for staff; step-up re-auth for sensitive actions. Super admins invite staff by email with a role (reuse the invite token pattern: hashed, single-use, expiring), change roles, deactivate staff. A staff member can't change their own role; there must always be at least one Super admin. Optional IP allowlist via config.
2. **Dashboard:** counts and trends: applications waiting review (with age), new sellers, active listings, orders/bookings today, open reports, failed integration events, phone/SMS volume. Accessible charts or stat tiles.
3. **Seller applications queue:** list (filter by status/age/country), detail with everything the wizard collected (business details, kosher answers and certificate file viewer via the API with nosniff, terms/agreement versions and timestamps), timeline and notes; actions Approve, Needs info (message to seller), Reject (reason), through the existing SellerStatusService transitions. Assign to a reviewer.
4. **Seller accounts:** search; detail with status history, members and roles, brands, counts (listings, orders, bookings), recent activity; actions: suspend/reactivate (reason), close account, resend owner verification, transfer ownership in emergencies (Super admin, step-up, both owners emailed). All via existing services.
5. **Users:** lookup by email/phone/user id; detail (memberships, sessions/devices, 2FA/passkey status, lockout state, recent security events from the audit log); actions: sign out everywhere, clear lockout, send password reset, remove phone number, disable 2FA as a last resort (Super admin, step-up, reason, user emailed). Never show password hashes, codes, tokens or PINs.
6. **Moderation:** queue of reported items (buyer-reported messages from orders/bookings, flagged contact-detail warnings, reported listings; add a "report listing" internal endpoint for the marketplace if missing); actions: hide/unhide listing (with reason shown to the seller), dismiss, warn seller (email), escalate to suspend. Listing detail view read-only.
7. **Support lookups (read-only):** orders, bookings, quotes, conversations by number/id/email, with PII masked by default and a "reveal" button that requires a reason and is audit-logged (break-glass). No impersonation/login-as (put a design note in the Roadmap).
8. **Integration health:** outbox events by status (pending/retrying/dead-lettered) with payload preview (PII-redacted), retry or discard dead letters (reason, audit); inbound marketplace request failures (signature/stale/replay counts); phone/SMS delivery failures; background job health (reconcilers, redaction jobs: last run, result).
9. **Audit log viewer:** filter by actor, target, action, date; export CSV (formula-safe); every staff action itself is audited (who, what, why, before/after where reasonable).
10. **Platform settings (Super admin):** marketplace fee %, the seller wizard's kosher/field configuration (SellerFieldConfig editor with ordering and branching preview), Terms/Seller Agreement version bump (forces re-acceptance flow if it exists, otherwise documented), feature flags already in config surfaced read-only.

## Security (OWASP ASVS L2)
- Every admin endpoint requires an authenticated staff user with 2FA satisfied and the right staff role (central permission matrix, documented in docs/ADMIN.md); seller tokens can never reach admin endpoints and staff can't use seller endpoints as a seller unless they also have a seller membership (separate session context).
- Reasons required for every write; audit log for every write and every PII reveal; rate limits on admin writes and lookups; CSRF/same protections as the rest of the app; no secrets in logs; exports formula-safe.
- Never expose secrets, hashes, codes, PINs, tokens, raw HMAC secrets, or unredacted outbox payloads.

## Tests: write them, run them, fix until green
- **Backend xUnit:** staff role matrix on every admin endpoint (each role × each endpoint), 2FA required, seller token rejected, last-Super-admin protection, staff invite flow, step-up on sensitive actions, application approve/needs-info/reject through the state machine, suspend/reactivate/close, user actions (lockout clear, sign out everywhere, 2FA disable), moderation hide/unhide visible to the seller, dead-letter retry/discard, PII reveal audited, audit export formula-safe, existing tests still green. Each regression test must fail when its fix/check is reverted (check this yourself).
- **Frontend Vitest + Playwright E2E (real backend) with axe:** staff login with 2FA → review an application with a certificate → approve → seller sees Approved; Support can't access Super-admin pages; moderator hides a listing; dead-letter retry; audit log filter.
- Never skip, delete or weaken a test. All GitHub workflows green on main in both repos after merging.

## Coordination
- Work on `feature/admin` in both repos; rebase on origin/main often; ONE EF migration (regenerate on top of main when main gains migrations). Fast-forward merge only when every workflow in BOTH repos is green; never force-push main or revert others' commits; no merge commits. Add/update your row "G. Staff admin panel" in docs/AGENTS.md (docs-only commit).
- Other workers may be active: E (orders frontend), F (services frontend), H (AI assistant, `Assistant/**`). Read other modules through their services; add small, additive, tested query methods where needed instead of editing their logic. Keep shared-file changes minimal (`builder.AddAdmin()`).
- **Backend first**; frontend after access is granted, as a separate code-split `/admin` area with its own layout using the design system (docs/DESIGN.md).
- **Environment:** .NET 10 SDK: `apt-get install -y dotnet-sdk-10.0` if missing. Chromium at /opt/pw-browsers/chromium (PW_CHROMIUM_PATH); don't reinstall browsers.

## Docs
docs/ADMIN.md (staff roles and permission matrix, features, audit and PII-reveal rules, operational runbook for dead letters and suspensions), DECISIONS entries, ROADMAP (impersonation, SSO for staff, 4-eyes approvals), both READMEs, desktop + mobile screenshots of the admin pages in the frontend's docs/screenshots/.

## Keep yourself running
Use send_later or create_trigger to schedule check-ins into this session every 2–3 hours and 3 minutes after any usage-limit reset.

## Finish
Post a final summary: what was built, the staff permission matrix, test counts per suite, workflow run links on main in both repos, anything deferred, and where the screenshots are.
