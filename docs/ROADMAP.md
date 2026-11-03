# Roadmap (out of scope for this iteration)

Each item lists a short design note so it can be added later.

* **Staff admin: impersonation / "log in as"** - not built on purpose. Design note: a short-lived (<=15 min), single-seller *impersonation token* minted only for Super admin with step-up and a ticket reason, carrying an `act` claim (`sub` = seller user, `act.sub` = staff id), read-only by default (writes need a second explicit "allow writes" step-up), never allowed for staff or other Super admins, shown with a permanent banner, every request audited as `admin.impersonation.*`, and revoked on staff logout.
* **Staff admin: SSO for staff** - OIDC (Entra/Google Workspace) with `amr` containing a second factor, mapped by group claim to `StaffRole`; keeps the staff session kind, step-up and IP allowlist, replaces the password+TOTP form.
* **Staff admin: passkeys as the staff second factor** - an `admin/auth/passkey/*` ceremony that mints a `Kind=staff` session with `MfaAt` (user-verified passkeys are already treated as two factors in `IdentityPasskeyOptions`).
* **Staff admin: four-eyes approvals** - `PendingAdminAction(kind, payload, requestedBy, reason)`; close account, ownership transfer, 2FA disable, dead-letter discard and fee changes need a second Super admin to approve (cannot be the requester) before they apply.
* **Forced re-acceptance of new Terms / Seller Agreement** - on bump, compare `SellerAccount.TermsVersion/SellerAgreementVersion` with the platform version at sign-in and route to an acceptance screen; the admin settings page already counts affected sellers.
* **Moderation extras** - reported product Q&A, review moderation, automatic hold of listings with N open reports, appeal flow for hidden listings.
* **Business switcher / multi-brand** – split `SellerAccount` into `Organization` + `Brand`, add membership table; token carries active `org` claim and a `switch` endpoint reissues it.
* **Shop staff invitations and roles** – `Membership(userId, orgId, role)` + signed single-use invitation tokens by email; role claims scoped per org.
* **Guest mode** – anonymous browsing token with read-only scope; the placeholder `/guest` route is the entry point.
* **Account picker** – remember last N accounts per device (non-secret hints in a cookie) and drive `login_hint`/passkey allow-lists.
* **Seller presence / online status** – SignalR hub or heartbeat endpoint updating `LastSeenAt`; expose via cached query.
* **Onboarding checklist** – `OnboardingTask` rows per seller, evaluated by status events, rendered on the dashboard.
* **SMS features (phone verification, new-device code, trusted devices, New sign-in email)** – implement `ISmsSender` (Twilio/etc.), store verified phone, add device fingerprint cookie + `TrustedDevice` table, challenge when unknown, send "New sign-in" email via a new template. Consent timestamps already stored.
* **Magic link / email code login** – reuse `OneTimeCodeService` with purpose `login`, same throttle and captcha rules, no enumeration (generic response).
* **Save-and-resume reminders after 3 days** – background `IHostedService` querying drafts untouched ≥3 days; send reminder email once with deep link to the wizard step (`DraftStep`).
* **Buyer storefront (products)** – public read API over Active products only (`descriptionHtml`, current price, images through a CDN-signed URL), search index fed from `products` (+ `SearchTerms`), question submission replacing `/api/dev/questions` (rate-limited, moderated into `ProductQuestion`).
* **Orders and payments** – workstream E: order lines reference `(ProductId, VariantId?, Sku)` and use `IInventoryService.Reserve/Commit/Release`; payments via a provider-agnostic `IPaymentProvider`.
* **Reviews** – `ProductReview(productId, orderLineId, rating, text, state)` created only from delivered order lines; seller reply like question answers; aggregates denormalised onto `Product`.
* **Advertising / sponsored listings** – `Promotion(productId, budget, bid, window)` + ranking hook in the search index; never mixes into seller catalog tables.
* **External marketplace / store sync** – outbox of product change events (`Version` is the cursor) consumed by connector workers; per-connector field mapping.
* **Image import by URL** – server-side fetch is an SSRF risk; if added: allow-listed schemes/hosts, resolved-IP checks, size/time caps, run in a sandboxed worker, then the same `ImageProcessing` pipeline.
* **Multi-currency, tax classes, bundles, per-variation shipping, bulk edit of attributes, CSV variations** – follow-ups on the existing model.
* **Shipping labels / carrier rate shopping** (orders) – `IShippingProvider` (rates + label purchase + void) behind the ship dialog placeholder; store label URL and cost on `Shipment`; deduct label cost from seller net via a marketplace event.
* **Live carrier tracking polling** – a worker polls carriers (or a tracking aggregator webhook) for open `Shipment`s and drives `order.delivered`/delay events through `BuyerEventService.DeliveryAsync`; keep the manual "mark delivered" as fallback.
* **Buyer storefront, payment processing, payouts, marketplace fee invoicing** – belong to the main website; the portal already exposes amounts (`feeCents`, `netCents`, `refund.issued`) for a payout ledger (`Payout(sellerId, period, orders, fees, refunds)`).
* **Multi-warehouse inventory** – `StockLocation` + per-location `Reserved/Stock`; `IInventoryService` gains a location argument; ship dialog picks the location.
* **Fulfilment by the marketplace** – new `FulfilledBy` on `SellerOrder`; seller actions disabled, inbound shipment events from the marketplace WMS.
* **Multi-node live updates** – replace the in-process `EventHub` with Redis pub/sub or PostgreSQL LISTEN/NOTIFY.
* **Business-day SLA calendars and per-method transit times**, **multiple open returns per order**, **buyer-visible attachment scanning (AV)**.

* **Two-way Google / Outlook calendar sync and importing external calendars** (services) - OAuth app per provider (`CalendarConnection(membershipId, provider, refreshTokenEncrypted)`), push provider bookings as events, and read free/busy (never event bodies) into `CalendarBlock(Kind=Busy, Source=external)`; needs OAuth credentials and server-side fetching of remote content (SSRF review) which is why only the private ICS export exists now.
* **Auto-created video meetings (Zoom / Meet / Teams)** - `IMeetingProvider.CreateAsync(booking)` returning a join URL stored on the booking and revealed in `booking.confirmed`; replaces the manually entered link.
* **Group classes / multi-attendee capacity** - `Service.Capacity` + `Booking.Seats`; the slot engine counts seats instead of blocking the footprint, a `Session` entity groups bookings of one class.
* **Recurring bookings / subscriptions** - `BookingSeries(rrule, until)` that materialises bookings through the same create path (each occurrence takes the calendar lock); marketplace owns the billing.
* **Route-optimised travel time** - replace the fixed travel buffer by a distance-matrix call between consecutive at-customer jobs of a provider/day (`ITravelTimeProvider`), keep the buffer as fallback.
* **Payments and payouts for services** - belong to the marketplace; the portal already emits the amounts (`booking.cancelled`, `booking.no_show`, `booking.additional_charge_requested`, `booking.completed`).
* **Buyer-side booking UI** - part of the main website; consumes the signed availability/hold/confirm API.
* **Booking threads in the Messages inbox** - move `booking_messages` into E's `Conversation` model (`ConversationKind.Booking`) once the order workstream is stable.

* **Assistant: staff tools** (`/admin`) - a second `ToolRegistry`/`ActionDef` set for support staff on the same engine (approve seller applications, add notes, look up orders across accounts with explicit staff permissions); needs a staff `AccessContext`, its own system prompt, and conversations with `Surface=admin`.
* **Assistant: voice** - reuse the phone/IVR layer: speech -> `AssistantEngine` with a short-answer prompt and the read-only tool subset, confirmations by spoken PIN step-up (never "yes" alone).
* **Assistant: proactive suggestions** - a nightly job computes "late orders", "unanswered messages", "listings below 60 quality" per account and surfaces them as dismissible cards in the portal; actions still go through confirm cards.
* **Assistant: refunds** - `propose_refund` limited by `OrderRefund` permission and `Orders:ManagerRefundLimitCents`, high-risk card with the full amount breakdown and a second confirmation for amounts above a threshold.
* **Assistant: model streaming and cost controls** - stream tokens from the Claude API to the SSE channel, `claude-haiku-5-5` routing for simple read-only questions, prompt-cache hit-rate metrics, per-account monthly spend caps and a staff dashboard over `assistant_usage`.
* **Assistant: attachments and bulk work** - "fix all my drafts" as a batch card with per-item diffs; image alt-text suggestions from product photos.
