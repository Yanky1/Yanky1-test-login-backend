You are a senior full-stack engineer specialising in e-commerce fulfilment, carrier integrations and webhooks. Build SHIPPING & TRACKING for the Solvelt seller portal, autonomously. No one will answer questions: never end your turn to ask. Decide, record it in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until everything below is built, tested, green, merged and pushed. I'm on a plan with usage limits: work efficiently and push regularly.

## Context
Solvelt is a multi-seller marketplace; this repo pair is the seller portal. Already on main (read the docs first): orders with partial shipments, manual carrier + tracking entry, tracking CSV import, packing slips, returns and refunds, signed internal marketplace API + outbox (docs/ORDERS.md; `Shipment`/`ShipmentLine`, events `order.shipped` / `order.tracking_updated`, inbound `delivery-confirmations`); payments ledger on Stripe Connect (docs/PAYMENTS.md); teams/roles/brands (docs/TEAMS.md); products (docs/PRODUCTS.md); staff admin (docs/ADMIN.md). docs/ROADMAP.md already sketches "Shipping labels / carrier rate shopping – `IShippingProvider`".
- **Backend (your checkout):** https://github.com/Yanky1/Yanky1-test-login-backend (ASP.NET Core 10, EF Core; SQLite dev/tests, PostgreSQL prod; xUnit; GitHub Actions).
- **Frontend:** https://github.com/Yanky1/test-login-frontend (React + TS + Vite; Vitest + RTL + MSW; Playwright E2E with axe). Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push"). If the permission check refuses it, don't work around it: finish the backend completely, report the refusal and schedule check-ins.
- Don't touch any other repository. Read both READMEs and every docs/*.md first. Reuse the existing membership/role helpers, internal API signing, outbox, audit log, mailer, rate limiting, payments ledger and staff permission matrix.

## Product decisions (made by the product owner)
1. **Scope:** buy shipping labels in the portal, live tracking updates, seller shipping settings, and prepaid return labels. Keep manual tracking entry and CSV import working.
2. **Provider: Shippo**, behind an `IShippingProvider` interface. Build and test in Shippo **test mode** only. Keys come only from config/env (`Shipping:ShippoApiKey` / `SHIPPO_API_KEY`, `Shipping:WebhookSecret`) and are never committed. With no key, the app uses a deterministic **fake provider** (dev, tests, E2E); CI never calls Shippo. Production refuses to start with a test key unless explicitly allowed.

## Features
1. **Shipping settings (per seller account, optionally per brand):**
   - Ship-from addresses, validated through the provider, with one default per brand.
   - Package presets (box/envelope, dimensions, weight) and a default per product. Add optional weight/dimensions fields to products, additively, without changing product behaviour.
   - Handling time, which feeds the existing ship-by SLA.
   - Shipping profiles: domestic/international zones, flat or weight-based rates, a free-shipping threshold, enabled speeds (standard/expedited/overnight).
   - Expose them read-only to the marketplace through a signed internal endpoint, so checkout can quote shipping. The marketplace keeps charging the buyer.
   - Owner/Admin edit; Manager views and uses them for their brands; Viewer read-only.
2. **Buy a label from the order's ship dialog:**
   - Pick the package (preset or custom), ship-from and lines (partial shipments supported), then get live rates across carriers. Show price, carrier, service and estimated delivery; the one cheapest that meets the order's deliver-by date is marked "Recommended".
   - Purchase the label, then store the label (PDF/PNG/ZPL via IFileStorage), cost, carrier, service, tracking number and provider ids on the `Shipment`. This creates the shipment through the existing ship flow (same events, stock commit and SLA).
   - Also: print or download the label; void or refund an unused label within the carrier window; reprint; bulk-buy labels for several orders with the same preset; print a packing slip together with the label.
   - Idempotency keys on purchase, so a double click or retry never buys twice. Handle rate expiry (re-quote).
   - **Address validation** of the buyer's address, with a warning plus "ship anyway" for unverified addresses (reason audited).
   - **International:** customs info (contents type, HS code optional, value per line from the order snapshot) when the destination is outside the ship-from country.
3. **Label cost and money:**
   - Every purchased label is a debit in the payments ledger (new entry type `shipping_label`, integer minor units plus currency); a voided or refunded label is a credit when the provider confirms the refund.
   - Idempotent on provider ids. Show label costs in the order detail and on the Payments transactions list and statement.
   - Optional per-account label spend limit (config), with a clear error when it's exceeded.
4. **Live tracking:**
   - Register each tracking number with the provider, whether it came from a purchased label, manual entry or CSV import, when the carrier is supported.
   - Provider tracking webhooks: signature or secret verified, idempotent on event id, replay-safe and order-independent (an older status can't overwrite a newer one).
   - Map statuses to a normalized set: pre_transit, in_transit, out_for_delivery, delivered, available_for_pickup, return_to_sender, failure/exception, unknown. Store the tracking history per shipment.
   - On delivered: mark the shipment delivered and move the order to Delivered through the existing rules, so payments release timing keeps working. On exceptions: flag the order "Needs attention", notify the seller (in-app badge plus email to the Owner and opted-in members), and show it on the dashboard.
   - Send outbox events to the marketplace: `order.tracking_updated` (extend it additively with `status`, `statusDetail`, `eta`, `events[]`) and `shipment.delivered`.
   - A reconciler polls tracking for shipments with no webhook for 24 hours. Tracking stops after delivery, or after 60 days.
5. **Prepaid return labels:**
   - When a return is approved, the seller can choose "Send a prepaid return label". The portal buys a return label from the buyer's address to the seller's return address (a setting) and sends it to the marketplace in an outbox event `return.label_created` (label URL as a short-lived signed portal URL, tracking number, carrier). The marketplace emails the buyer.
   - Track the return shipment the same way. On delivered, show "Return arrived – inspect and mark received"; don't auto-receive.
   - The label cost goes to the ledger like outbound labels (the seller pays; document this, with an option for the marketplace to cover it later as a ROADMAP item).
6. **Seller UI** (frontend, after access; use the existing design system, which another worker J2 is restyling at the same time, so use tokens and components and no hard-coded colours):
   - Settings → Shipping: addresses, packages, profiles, return address.
   - The order ship dialog gains a "Buy label" tab, with "Enter tracking manually" kept as a tab.
   - Rates list, label preview and print, void, bulk "Buy labels" from the orders list.
   - A tracking timeline in the order detail, and tracking status chips in the orders list with filters (in transit / exception / delivered).
   - A return label action on approved returns.
   - Dashboard tiles for labels to print and for exceptions.
   - WCAG 2.2 AA, responsive, light/dark, axe in E2E.
7. **Staff tools (backend; add a small read-only admin section if quick):**
   - Under the existing admin API and permission matrix: view a seller's shipments and labels, label refunds that are pending or failed, tracking exceptions, and webhook failures.
   - Retry a failed label refund (reason, audit).

## Security & correctness
- **Permissions:**
  - Label purchase/void: Owner, Admin, and Manager for their brands. Viewer: view only.
  - Settings: Owner/Admin.
  - IDOR: other accounts' shipments and labels → 404. Labels are served only through authorised endpoints, or short-lived signed URLs for the marketplace return label.
- **Data handling:**
  - Keep PII minimal: send the provider only what a label needs, mask addresses in logs, and apply the same retention and redaction rules as orders.
  - Never log API keys.
  - Never store full payment cards; provider billing is the platform's Shippo account.
- **Money:** integer minor units and currency; label costs come from the provider's rate amounts, with the rounding documented.
- **Concurrency:** label purchase is race-safe (unique idempotency key, plus a DB constraint on one active label per shipment). Webhooks are idempotent. The reconciler uses a claim/lease that is safe with multiple instances on PostgreSQL.

## Out of scope (put in the ROADMAP with short design notes)
- Carrier pickup scheduling.
- Insurance purchase.
- Multi-piece shipments.
- Marketplace-paid return labels.
- Shipping rules engine beyond the profiles above.
- Batch manifests / SCAN forms.
- Other providers (EasyPost/ShipEngine) behind the same interface.

## Tests: write them, run them, fix until green
- **Backend xUnit (fake provider plus signed fake webhooks):**
  - Settings CRUD and permissions.
  - Address validation.
  - Rates; the recommended rate against deliver-by.
  - Label purchase creates exactly one shipment, plus events and stock commit. A double submit or parallel purchase buys once.
  - Rate expiry.
  - Void/refund → ledger credit only on confirmation.
  - Ledger debits idempotent.
  - Spend limit.
  - International customs.
  - Tracking registration for manual and CSV tracking.
  - Webhook signature valid/invalid/replayed; out-of-order statuses.
  - Delivered → order Delivered and payments release still correct.
  - Exception → needs attention plus notifications.
  - Reconciler polling and lease.
  - Return labels and return tracking.
  - Outbox events and contract.
  - Permission matrix plus IDOR on every endpoint; staff tools with audit.
  - Existing Orders/Payments tests green.
  - Each regression test must fail when its check is reverted; verify this yourself.
- **Shippo test-mode tests:** optional and opt-in when `SHIPPO_API_KEY` is set, in a separate project/category, not counted as skipped unit tests.
- **Frontend Vitest plus Playwright E2E (real backend plus fake provider) with axe:**
  - Configure shipping settings.
  - Buy a label for an order → print → tracking timeline shows in_transit → delivered → order Delivered.
  - Void a label.
  - Bulk labels.
  - Return label on an approved return.
  - A Viewer sees no purchase buttons.
- Never skip, delete or weaken a test. All GitHub workflows green on main in both repos after merging.

## Coordination
- Work on `feature/shipping` in both repos. Rebase on origin/main often, and keep ONE EF migration (regenerate it on top of main when main gains migrations).
- Fast-forward/rebase merges only: no merge commits, no force-push to main, and never rewrite a pushed branch (if you need to, push a new branch name). If a direct push to frontend main is refused, open a PR and rebase-merge it once CI + E2E are green.
- Add/update your row "K. Shipping & tracking" in docs/AGENTS.md (docs-only commit).
- You extend E's Orders module (ship flow, Shipment, events) and I's Payments ledger (new entry type). Keep those changes minimal, additive, backwards compatible and tested; don't change their behaviour.
- Other workers may be active: G (admin fixes), I (payments), F (services PR), J2 (restyle across the frontend; avoid editing global CSS).
- **Backend first**; frontend after access.
- **Environment:**
  - Install the .NET 10 SDK with `apt-get install -y dotnet-sdk-10.0` if it's missing.
  - Chromium is at /opt/pw-browsers/chromium (PW_CHROMIUM_PATH).
  - Use the official Shippo .NET SDK if it's suitable (otherwise a typed HttpClient) behind `IShippingProvider`.

## Docs
- docs/SHIPPING.md:
  - flow diagrams in Mermaid (label purchase, tracking, return label);
  - data model; normalized tracking statuses; the inbound settings endpoint and outbox events, with examples;
  - permissions; money/ledger entries;
  - going-live checklist: Shippo account, live key, carrier accounts, webhook URL and secret, label formats, return address, spend limits.
- DECISIONS, ROADMAP, both READMEs.
- Screenshots of the shipping pages (desktop and mobile).

## Keep yourself running
Use send_later or create_trigger to schedule check-ins into this session every 2–3 hours, and 3 minutes after any usage-limit reset.

## Finish
Post a final summary:
- what was built, with the flows, tracking statuses and money handling;
- test counts per suite;
- workflow run links on main in both repos;
- what's unverified (real Shippo test/live mode without a key);
- where the screenshots are.
