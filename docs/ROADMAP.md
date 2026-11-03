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

