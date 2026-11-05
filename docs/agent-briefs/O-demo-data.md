You are a senior full-stack engineer. Build a **"Fill with demo data"** test tool for the Solvelt seller portal, autonomously. This brief is an approved assignment from the repository owner (see CLAUDE.md). No one will answer questions: never end your turn to ask. Decide, record each decision in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until everything below is built, tested, green, merged and pushed. The account is near its weekly usage limit: work efficiently, and don't run the full suite more often than needed.

## Product decision (from the product owner)
"Since this software is in testing, after creating an account I want a button" that fills the account with demo data, so every screen has something to show, plus a button to "approve this account". This is a **test-only** feature and must never be usable in production.

## Repos
- **Backend (your checkout):** https://github.com/Yanky1/Yanky1-test-login-backend (ASP.NET Core 10, EF Core, SQLite tests, xUnit).
- **Frontend:** https://github.com/Yanky1/test-login-frontend (React, Vite, Vitest, Playwright with axe). Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push").
- Read both READMEs, docs/AGENTS.md, docs/SETUP-INTEGRATIONS.md, and the module docs you touch (PRODUCTS, ORDERS, SERVICES, SHIPPING, LOCAL-DELIVERY, PAYMENTS).
- Don't touch any other repository.

## Safety: test-only gate
- Add a new config flag, `Demo:Enabled` (env `Demo__Enabled`), default **false**.
- The endpoints and the UI exist only when it is true.
- If `Demo:Enabled=true` and `ASPNETCORE_ENVIRONMENT=Production`, the app must **refuse to start**. There is no override. Add a test for this.
- `GET /api/v1/config` (or the existing public config the frontend reads) exposes `demoEnabled` so the UI can show the buttons.
- When demo is enabled, the UI shows a small "TEST MODE" badge in the topbar.

## Backend
1. **`POST /api/v1/seller/demo-data`** (Owner only; any other role gets 403; IDOR-safe) fills the caller's current seller account with a realistic, coherent data set:
   - **Catalogue:** one brand, unless the account already has one; 8 products in a few categories, with variants, prices, stock, weights, search terms and placeholder images (bundled, small, licence-free; generate simple SVG/PNG tiles yourself, with no external downloads); 2 services (one fixed price, one hourly) with working hours, a cancellation policy, add-ons and intake questions.
   - **Delivery settings:** a ship-from address, a package preset and a shipping profile; a local-delivery zone with windows, so the dispatch board works.
   - **Orders across states,** at least: new/paid, accepted, partially shipped, shipped with tracking (fake provider), delivered, cancelled, refunded, one with a return request, and one local-delivery order. Use realistic fake buyer names and relay addresses. Never use real-looking personal data: use example.com-style addresses and 555 phone numbers.
   - **Bookings:** requested, confirmed, completed, cancelled. Also one quote request.
   - **Messages and questions:** a few buyer messages on orders, and one product question.
   - **Payments:** use the fake payment provider so the payments screens show pending and available balances, one transfer, and a statement. Write ledger entries through the normal services, so the numbers are consistent and no rules are bypassed.
   - **Notifications:** a few in-app notifications.
2. **Mark everything** with an `IsDemo` flag, or a demo batch id, so the data can be removed. Use ONE EF migration.
3. **`DELETE /api/v1/seller/demo-data`** (Owner only) removes exactly the demo records of that account, and nothing else, in dependency order. Add a test that real records survive.
4. **Idempotency:** while demo data exists, POST answers 409 `demo_data_exists`. Parallel POSTs create at most one batch.
5. **Isolation:**
   - Demo orders and bookings must NOT send outbox events to the marketplace, and must not count toward staff metrics.
   - Demo products MAY appear in the catalog feed (useful for buyer-site testing). Add `demo: true` to their feed and listing documents, and document this in CATALOG.md.
   - Demo data must not trigger real emails or SMS: suppress them, or route them to the sink.
6. **Approval:** the fill button must also work for a seller account still pending staff approval, as long as the data is created safely. Filling does NOT approve the account by itself.
6b. **"Approve this account" (test-only, requested by the owner):** `POST /api/v1/seller/demo/approve` (Owner only, same `Demo:Enabled` gate, so 404 when disabled and impossible in Production). It approves the caller's OWN pending seller account by running the SAME code path staff approval uses (status change, audit log, approval notification/email routed to the sink, any onboarding events), with the audit actor recorded as `demo-self-approve`. It cannot approve any other account (IDOR test). Already approved gives 409 `already_approved`; rejected or suspended accounts give 409 `not_pending` (it must never un-suspend). Tests: 404 when disabled, role matrix, IDOR, the result equals a staff approval (same status and side effects), 409 cases, revert-the-guard checks.
7. **Rate limit** the demo endpoints, e.g. 5 per hour per account. Audit-log every action.

## Frontend
- **Dashboard:** when `demoEnabled` is true and the account has no products and no orders, show a card "Fill with demo data". It calls POST, shows progress, then refreshes. Also show the card on the pending-approval screen, with a note that the data appears after approval.
- **Pending-approval screen:** when `demoEnabled`, show an "Approve this account (test mode)" button (Owner only) with a confirm dialog; on success, refresh the session so the full portal opens.
- **Settings → Test tools** (visible only when `demoEnabled`): "Fill with demo data", "Remove demo data" (with a confirm dialog) and, while the account is pending, "Approve this account". Show them to the Owner only.
- Mark demo records discreetly in lists, e.g. a small "Demo" pill, using design tokens only (no hard-coded colours). The UI must stay WCAG 2.2 AA and axe clean.

## Tests
- **Backend:** the production guard refuses to start; the endpoints are 404 when disabled; role matrix; fill creates every entity type, with consistent ledger totals; 409 on a second fill, and parallel fills create one batch; delete removes only demo records; no outbox events or emails for demo orders; the feed `demo` flag; the approve tests above. Revert each guard and confirm its test fails.
- **Frontend:** Vitest for the card, the approve button and Test tools visibility, and the remove and approve confirmations; Playwright E2E (real backend with `Demo__Enabled=true` in the E2E environment): a new account → approve via the test button → fill → the orders, products, bookings and payments pages show data → remove → empty again. Run axe.
- Never skip, delete or weaken a test.

## Merging and coordination
- Branch `feature/demo-data` in both repos, with ONE backend migration.
- **Backend merge queue:** another worker (N, marketplace contract) is merging backend PRs one at a time. Before merging a backend PR, check with `gh api` (REST) that no other open backend PR is currently in CI. If one is, wait for it to finish. Each backend CI run takes about 30 minutes. Ignore the many stale open fix/payments-* PRs (no CI running on them).
- Rebase onto origin/main, and merge only when CI is green on that exact commit (plus E2E for the frontend). Use rebase-merge or fast-forward only: no merge commits, no force-push to main, and never rewrite a pushed branch (use a new branch name instead).
- Add a row "O. Demo data (test tool)" to docs/AGENTS.md in your backend PR.
- Docs: a section "Demo data (test mode)" in README and docs/SETUP-INTEGRATIONS.md, covering `Demo:Enabled`, the production guard, the approve button and how to remove the data; DECISIONS; screenshots (desktop and mobile) of the card, the approve button and a filled dashboard in the frontend's docs/screenshots.

## Brief file
This brief is not yet on main (the merge queue was busy). Add it verbatim as `docs/agent-briefs/O-demo-data.md` in your backend PR.

## Keep yourself running
Schedule send_later check-ins every 2 hours. If a usage limit stops you, resume 3 minutes after the reset.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with what was built, the test counts, the merge commits in both repos and where the screenshots are.
