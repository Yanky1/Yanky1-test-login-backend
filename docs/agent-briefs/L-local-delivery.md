You are a senior full-stack engineer specialising in last-mile delivery, mobile web apps and real-time tracking. Build LOCAL DELIVERY with the seller's own drivers, with tracking, for the Solvelt seller portal, autonomously. No one will answer questions: never end your turn to ask. Decide, record it in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until everything below is built, tested, green, merged and pushed. I'm on a plan with usage limits: work efficiently and push regularly.

## Context
Solvelt is a multi-seller marketplace, and this repo pair is the seller portal. Already on main (read the docs first):
- orders with shipments, the signed internal marketplace API and the outbox (docs/ORDERS.md);
- teams, roles and brands (docs/TEAMS.md);
- payments ledger (docs/PAYMENTS.md);
- services and bookings with time zones and slot logic (docs/SERVICES.md), which you can reuse for delivery windows;
- staff admin (docs/ADMIN.md).

Another worker, **K (Shipping & tracking, `feature/shipping`)**, is building carrier labels through Shippo, shipping settings (ship-from addresses, packages, shipping profiles) and normalized tracking statuses: pre_transit, in_transit, out_for_delivery, delivered, available_for_pickup, return_to_sender, failure/exception, unknown. Its brief is docs/agent-briefs/K-shipping-tracking.md; read it. Local delivery is a **second delivery method next to carriers**. Reuse K's normalized statuses, tracking-history model, outbox events and settings pages. If K's pieces aren't on main yet, build the minimal shared pieces in the same shape K's brief describes, coordinate through small additive commits, and rebase onto K's work when it lands (no duplicate models).

- **Backend (your checkout):** https://github.com/Yanky1/Yanky1-test-login-backend
- **Frontend:** https://github.com/Yanky1/test-login-frontend. Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push"). If that is refused, don't work around it: finish the backend and report the refusal.
- Don't touch any other repository. Read both READMEs and every docs/*.md first.

## Product decision (from the product owner)
"Some packages are local packages, so I want the option to deliver them with a local driver, with tracking for that too."
Decisions made on their behalf (document them in DECISIONS; they are easy to change):
- Drivers are the **seller's own people**. There is no third-party courier marketplace in v1; a provider interface for courier APIs (e.g. Uber Direct, DoorDash Drive) goes in the ROADMAP.
- Drivers are team members with a new limited role, **Driver**, invited through the existing invite flow. Driver is an addition to the role model in docs/TEAMS.md:
  - sees only the deliveries assigned to them;
  - sees only the address and phone details needed for those deliveries, and only while they are active;
  - no access to products, money, settings or other orders.
- The driver uses a **mobile web app** inside the portal at `/driver` (installable PWA, works on phones, offline-tolerant). There is no native app.

## Features
1. **Local delivery settings** (Owner/Admin; per brand optional), in Settings → Shipping next to K's profiles:
   - **Delivery zones:** radius from a ship-from address and/or a postcode list, or a drawn polygon (optional).
   - **Fee rules:** flat fee per zone, free over a threshold, minimum order.
   - **Delivery days and time windows,** e.g. 2-hour windows with capacity per window; reuse the services slot and time-zone logic.
   - **Same-day cutoff time.**
   - **Exposed to the marketplace through a signed internal endpoint:** whether an address is eligible, plus available windows and fees, so checkout can offer "Local delivery" and the buyer can pick a window. The marketplace charges the buyer. The order ingest carries the chosen method and window, an additive field on the order contract.
   - **Seller-side conversion:** the seller can also switch a carrier order to local delivery when the address is in a zone (audited, buyer notified via an outbox event).
2. **Dispatch board** (Owner/Admin/Manager for their brands):
   - Local orders by day and window; assign them to a driver.
   - Create a **route**: ordered stops, with an "optimize order" action behind `IRouteOptimizer`. The fake implementation does nearest-neighbour; the real provider is optional and its config goes in the ROADMAP.
   - Print or share the route.
   - Reassign, and unassign a failed stop.
   - Capacity warnings per window.
   - Late and exception highlights.
3. **Driver app** (`/driver`, Driver role, also usable by Owner/Admin/Manager acting as driver):
   - Today's route and the stop list with the window per stop.
   - Navigate opens the phone's maps app with the address.
   - Call or text the buyer goes through the existing relay or masked contact. Never show the real buyer phone or email if the orders module masks them; follow ORDERS.md.
   - Statuses: Start route → per stop "Out for delivery" (auto-set for the next stop) → Delivered, which requires **proof of delivery**: a photo, optionally the recipient's name and an on-screen signature, plus a "left at door" / "handed to" choice. The other outcome is Failed attempt, with a reason (no one home, address issue, refused, unsafe), a note and a photo, plus Reattempt / Return to store.
   - Offline queue: status updates are stored and sent when back online, with client timestamps and server ordering rules.
   - Large touch targets; works one-handed.
4. **Tracking:**
   - Every local delivery is a shipment of method `local` with the same normalized statuses and tracking history as carrier shipments:
     - pre_transit when assigned;
     - in_transit when the route starts;
     - out_for_delivery when it's the next stop;
     - delivered;
     - failure/exception for a failed attempt;
     - return_to_sender for return to store.
   - Delivered moves the order to Delivered through the existing rules, so payments release still works.
   - Outbox events to the marketplace: reuse `order.tracking_updated` (with `method: "local"`, status, ETA window and driver first name only) and `shipment.delivered` (with proof-of-delivery available through a short-lived signed URL), plus `delivery.attempt_failed`. The marketplace shows the buyer's tracking page and sends texts and emails.
   - **Live ETA:** while a route is active, the driver's app sends its location every ~30–60 s, only while a route is started and only after the driver consents in the app.
     - The server computes a rough ETA per remaining stop.
     - The buyer-facing tracking event includes the ETA, and a coarse live position (rounded to ~100 m) **only when the buyer's stop is next**.
     - Positions are kept for at most 24 h, then deleted (retention job). Drivers can pause sharing.
     - Document the privacy rules in docs/LOCAL-DELIVERY.md.
5. **Notifications:** seller in-app and email for a failed attempt, late against the window, and a route finished. The marketplace handles buyer notifications from the outbox events.
6. **Money:**
   - The delivery fee the buyer paid is already on the order through the shipping amount. No ledger changes are needed beyond what exists.
   - Optional driver cost tracking is out of scope (ROADMAP).
7. **Staff tools (backend, read-only):** view a seller's local zones, routes and deliveries, and the proof of delivery (staff permission, reason, audit, as with the PII reveal).

## Security & privacy
- **Permissions matrix:** add Driver everywhere, and test it.
  - Drivers can't see any other order, and can only see their stops' details while a route is assigned or active. After completion, the stop's address and contact are hidden from the driver.
  - IDOR → 404.
- **Media:** proof-of-delivery photos and signatures go through IFileStorage with nosniff, served only through authorised or signed short-lived URLs, and fall under the order retention and redaction rules. Strip EXIF location from photos except for a server-recorded delivery location.
- **Location data:** minimal, consented, retention-limited and never in logs. PII is masked in logs.
- **Concurrency:**
  - Route and stop updates use optimistic concurrency.
  - Offline replays are idempotent: client event ids are unique.
  - Older events can't overwrite newer state.
  - Window capacity can't be overbooked (DB-level, safe on PostgreSQL).

## Out of scope (put in the ROADMAP with short design notes)
- Third-party courier APIs.
- Driver payroll or cost tracking.
- Native apps.
- Real-time map streaming to the seller dashboard beyond periodic refresh.
- Multi-day routes.
- Cash on delivery.

## Tests: write them, run them, fix until green
**Backend xUnit** (fake optimizer, fake clock):
- zone eligibility (radius, postcode);
- window capacity, including parallel bookings;
- the signed eligibility and windows endpoint, and order ingest with a local method and window;
- converting a carrier order to local;
- dispatch, assignment and route creation; optimizer ordering;
- driver permissions:
  - only assigned stops are visible;
  - details are hidden after completion;
  - other drivers' and accounts' data → 404;
- status transitions:
  - proof is required for Delivered;
  - failed attempt, reattempt and return;
- offline replay idempotency and out-of-order handling;
- delivered → order Delivered, and payments release still correct;
- outbox events and the contract;
- live location:
  - consent required;
  - only accepted while the route is active;
  - coarse position only for the next stop;
  - 24 h retention job;
- staff view with audit;
- the existing Orders, Teams, Payments and Shipping tests stay green.

Each regression test must fail when its check is reverted; verify this yourself.

**Frontend Vitest + Playwright E2E** (real backend) with axe:
- configure a zone and windows;
- a local order appears on the dispatch board → assign → driver signs in on a mobile viewport → start route → deliver with a photo (fixture) and signature → order Delivered and the tracking timeline shows local statuses;
- a failed attempt flow;
- a Driver can't open /orders or /products;
- the offline queue (simulate offline, then reconnect).

Never skip, delete or weaken a test. All GitHub workflows must be green on main in both repos after merging.

## Coordination
- **Branches:** work on `feature/local-delivery` in both repos. Rebase on origin/main often; never rewrite a pushed branch (push a new branch name instead).
- **Migrations:** ONE EF migration, regenerated on top of main when main gains migrations; K may land its migration first.
- **Merging:** fast-forward or rebase merges only, no merge commits, no force-push to main. If a direct push to frontend main is refused, open a PR and rebase-merge it once CI + E2E are green.
- **AGENTS.md:** add/update your row "L. Local delivery" in docs/AGENTS.md (docs-only commit).
- **Shared code:** keep changes to Orders/Teams/Shipping minimal, additive, backwards-compatible and tested. Coordinate the shared shipment/tracking model with K: if you both need the same thing, the one who gets there first adds it in the shape K's brief describes, and the other rebases.
- **Other workers** may be active: K (shipping), G (admin fixes), I (payments), F (services PR), J2 (restyle across the frontend; use design tokens and components, no hard-coded colours, don't edit global CSS).
- **Order of work:** backend first, frontend after access.
- **Environment:** install the .NET 10 SDK with `apt-get install -y dotnet-sdk-10.0` if it's missing. Chromium is at /opt/pw-browsers/chromium (PW_CHROMIUM_PATH).

## Docs
- **docs/LOCAL-DELIVERY.md:**
  - Mermaid flows (checkout eligibility → dispatch → route → delivery/failed attempt);
  - data model; the status mapping to K's normalized statuses;
  - the internal endpoint and outbox events with examples;
  - the Driver permissions;
  - the location privacy rules;
  - a going-live checklist (zones, windows, drivers invited, marketplace tracking page, optional optimizer provider).
- **TEAMS.md:** add the Driver role.
- DECISIONS, ROADMAP, both READMEs.
- Screenshots of the dispatch board and driver app (desktop + mobile).

## Keep yourself running
Use send_later or create_trigger to schedule check-ins into this session every 2–3 hours, and 3 minutes after any usage-limit reset.

## Finish
Post a final summary:
- what was built, the flows, statuses and privacy rules;
- test counts per suite;
- workflow run links on main in both repos;
- what's unverified (a real route optimizer, real phones/GPS);
- where the screenshots are.
