You are worker Q (admin screens for the newer staff tools) on the Solvelt seller portal. This brief is an approved assignment from the repository owner (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DESIGN.md (frontend) or docs/DECISIONS.md (backend), and keep going until the work is built, tested, green, merged and pushed. The weekly usage limit is close: work efficiently and run filtered tests.

## Goal
The staff admin panel (`/admin` in https://github.com/Yanky1/test-login-frontend, `src/admin/**`; backend docs/ADMIN.md) has no screens yet for staff APIs that already exist in https://github.com/Yanky1/Yanky1-test-login-backend. Build them. This is mostly frontend. Add or change backend code only where a screen needs a missing read field, and then in ONE small backend PR without a migration if at all possible.

Attach the frontend with add_repo (owner "Yanky1", repo "test-login-frontend", access "push"). Install the .NET SDK with `apt-get update && apt-get install -y dotnet-sdk-10.0` from Ubuntu's archive (the Microsoft hosts are blocked); you need it to run the backend for E2E.

## Screens (each behind the staff permission the endpoint already declares; hide the nav item and actions the role lacks; step-up prompts where the API answers `step_up_required`)
1. **Payments** (`/api/v1/admin/payments/...`, docs/PAYMENTS.md):
   - a seller payments view: balances, Stripe status and the ledger with filters for type, dates, order and booking;
   - a payout hold and release, with a reason;
   - a manual adjustment (credit or debit) with a reason and a confirm dialog;
   - lists of transfers (with retry), payouts, accounts past due, reconciliation flags (run, resolve) and unallocated disputes.
2. **Shipping** (docs/SHIPPING.md): a seller's shipments and labels, refunds by state, exceptions, webhook failures, and label refund retry.
3. **Local delivery** (docs/LOCAL-DELIVERY.md):
   - the seller's delivery settings view;
   - proof-of-delivery access as a break-glass action with a reason, audited. Show the proof only after the reveal, never cached.
4. **Moderation additions** (docs/ADMIN.md, CATALOG.md C2):
   - the queue must render every report kind (listing, question, message, review, review_reply when it lands) with its context;
   - the actions hide-content, remove-content, warn-seller and escalate-suspend, with reasons;
   - show the "already resolved" result when another moderator won.
5. **Seller detail page:** link the new payments, shipping and delivery views from the existing seller page as tabs.

## Rules
- Reuse the existing admin components, api.ts, tokens and patterns (Sellers.tsx, Lookups.tsx).
- Mask personal data unless the staff member uses the existing reveal flow.
- Money is integer minor units: format with the existing helper and never compute it client-side.
- WCAG 2.2 AA: axe clean in light and dark, keyboard operable, and confirm dialogs on every write.

## Tests
- Vitest for every page: rendering, the permission-based visibility (run each role from the staff matrix against each new nav item and action), confirm dialogs, step-up handling and error states.
- Playwright E2E against the real backend: a Super admin signs in with 2FA, then:
  - opens a seller's payments tab and places and releases a hold;
  - opens the shipping and delivery tabs;
  - decides a content report;
  - axe runs on every new page.
  Use the demo-data fill (Demo__Enabled=true in the E2E environment) to get data.
- Screenshots (desktop and mobile) of each new page in docs/screenshots/admin-staff-tools/.
- Never skip, delete or weaken a test.

## Merging
- Branch `feature/admin-staff-tools` in each repo you touch.
- Backend merge queue: one PR in CI at a time. Worker P (reviews, C17) has backend PR #84 open: check with `gh api` (REST) and wait for it. Worker P also builds a frontend Reviews page; if both of you change the seller nav or shared admin files, rebase onto whatever lands first.
- Rebase onto origin/main. Merge only when CI (plus E2E) is green on that exact commit. Rebase-merge or fast-forward only: no merge commits, no force-push to main, never rewrite a pushed branch (use a new branch name).
- Update docs/ADMIN.md (the Frontend section) and docs/AGENTS.md (a row "Q. Admin screens for staff tools"), in the backend repo, in your backend PR or as a docs-only commit when no PR is in CI.
- Don't touch any other repository.

## Brief file
This brief isn't on main yet (the merge queue was busy). Add it verbatim as `docs/agent-briefs/Q-admin-staff-screens.md` in your backend PR or your docs-only commit.

## Keep yourself running
Schedule send_later check-ins every 2 hours. If a usage limit stops you, resume 3 minutes after the reset.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with the merge commits, the test counts and where the screenshots are.
