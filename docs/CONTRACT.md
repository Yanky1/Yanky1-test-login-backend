# Marketplace contract: index and C18 quantity offers

The seller portal and the buyer marketplace talk through the **signed internal API** (`/api/internal/v1`, signing in `docs/ORDERS.md` section 3.1) and the **signed outbox webhook** (`docs/ORDERS.md` section 3.4). Where each part lives:

| Part | Document |
|---|---|
| Orders (C1-C16): intake, buyer events, events, ordering, order read | `docs/ORDERS.md` |
| Catalog feed, questions, content reports | `docs/CATALOG.md` |
| Services, bookings, quotes (C11-C13) | `docs/SERVICES.md` |
| Payments, refunds, disputes | `docs/PAYMENTS.md` |
| Reviews (C17) | `docs/REVIEWS.md` |
| **Quantity offers (C18)** | **this file**, plus `docs/CATALOG.md` section 11 and `docs/ORDERS.md` section 3.2 |

---

## C18. Quantity offers: price tiers and free gifts

A seller (Blitz Cards in issue #101) sells plastic cards at $1.00 each and offers:
* **Price tiers:** "from 20 cards, $0.90 each". The **highest step reached** applies to **every eligible unit** of the order, counted across all the eligible products of that seller's brand, mixing designs. 20 cards cost $18.00, 25 cards cost 25 x $0.90 = $22.50.
* **Free gifts:** "from 10 cards, a wallet and a pack free". Gift products of the same brand are added at $0, once per order. The seller ships them; they count against the gift's stock.

All money is integer cents. Nothing here changes orders that carry no offer.

### C18.1 Rules (normative)

1. **Eligibility.** An offer belongs to one seller and **one brand**. A line is eligible when its product is a physical product of that brand inside the offer's scope: `products` (the listed ids), `category` (a listing whose category is one of `scope.categoryIds` or a descendant of one), `all` (every product of the brand). Gift lines never count as eligible units. Lines of other sellers or brands never count.
2. **One offer per line, no stacking.** When several active offers of the brand could price a line, the most specific scope wins: `products`, then `category`, then `all` (ties: the older offer, by the feed's `createdAt`; on equal `createdAt`, the lower `offerId` compared as its lowercase hyphenated 36-character string, ordinal). A line is priced by at most one offer.
3. **Eligible units** of an offer = the sum of the quantities of the lines it prices.
4. **Tier:** the step with the highest `minQuantity <= eligible units`; none below the first step.
5. **A tier never raises a price.** For each line priced by the offer: `discountCents = max(0, unitPriceCents - tier.unitPriceCents) x quantity` (`0` without a tier). `unitPriceCents` is the price you charge for the line before the offer, sale price included; a sale price below the tier stays, the tier does not stack on top of it.
6. **Gifts:** each gift whose `minQuantity <= eligible units` is earned **once per order** (never multiplied), as `quantity` units of `productId`, at `unitPriceCents: 0`, `discountCents: 0`, `taxCents: 0`.
7. **Gift out of stock:** the order still goes through without it. Tell the buyer ("free gift unavailable") and send `offers[].giftUnavailable: true` (or send the gift line: if the portal cannot reserve it, it leaves the gift out and answers `giftUnavailable: true` for that seller order). Never reject a checkout because of a gift.
8. **Version grace.** Price with the offer version you have. The portal accepts a version that applied at any instant of `[placedAt - 15 minutes, placedAt]` (`Offers:VersionGraceMinutes`). It **requires** an offer (answers `offer_missing` when you ignore it) only when that offer applied during the whole window; a cart priced just before an offer was created or ended still goes through.
9. **Refunds and fees use the discounted amounts.** The platform fee is computed on `items - discounts`; a partial refund returns the discounted price of the refunded units; **a gift line refunds $0**. **No re-pricing after a refund:** when units are refunded and the remaining units fall below a tier threshold, the remaining units are NOT re-priced; the buyer keeps the price paid. (Example: 20 cards at $0.90, 5 refunded: the refund is $4.50; the 15 kept cards stay at $0.90; a later refund of the 15 returns $13.50, $18.00 in total.)
10. **Tax.** Compute tax on the discounted line amount (the portal never adds tax; it stores the `taxCents` you send). Gift lines carry no tax.

### C18.2 Catalog feed of offers (signed)

`GET /api/internal/v1/catalog/offers?since=<cursor>&limit=<1..200>` and `GET /api/internal/v1/catalog/offers/{offerId}` (rate limit `RateLimit:InternalCatalogPerMinute`, signed exactly like the listing feed). Same cursor rules as listings: `feedVersion` comes from the catalog counter, strictly increasing in commit order; keep the highest `feedVersion` per `offerId`. An offer that was never active never appears; an ended or paused offer stays as a `listed: false` entry.

```json
GET /api/internal/v1/catalog/offers?since=0&limit=2
200
{
  "items": [
    {
      "offerId": "6f1c0e52-3b4a-4f0e-9a51-0c8d2b7e9d10", "version": 2, "feedVersion": 1902, "listed": true, "status": "active",
      "sellerId": "5d2a...", "brandId": "a71e...", "name": "Blitz cards bundle", "currency": "USD",
      "scope": { "kind": "products", "categoryIds": [], "productIds": ["0a5d...", "1b6e..."] },
      "productIds": ["0a5d...", "1b6e..."],
      "tiers": [ { "minQuantity": 20, "unitPriceCents": 90 }, { "minQuantity": 50, "unitPriceCents": 80 } ],
      "gifts": [ { "minQuantity": 10, "productId": "c3f0...", "quantity": 1, "title": "Card wallet", "available": true },
                 { "minQuantity": 10, "productId": "d401...", "quantity": 1, "title": "Card pack", "available": true } ],
      "activeFrom": null, "activeTo": null, "activeNow": true, "demo": false, "createdAt": "2026-11-18T21:05:12.4310000Z", "updatedAt": "2026-11-18T21:30:00Z"
    },
    { "offerId": "93be...", "version": 4, "feedVersion": 1907, "listed": false, "status": "ended", "reason": "ended", "sellerId": "5d2a...", "brandId": "a71e...", "createdAt": "2026-11-10T09:00:00Z" }
  ],
  "nextCursor": "1907",
  "hasMore": false
}
```
* `productIds` = the **visible** listings inside the scope when the document was built. For `category` and `all` scopes a listing that becomes visible (or changes category) later does not create a new offer version: apply rule C18.1-1 to the listing document (`brand.brandId`, `category.path`) or re-read the offer.
* `activeFrom` / `activeTo` are sent raw; `activeNow` is evaluated when the document is built (like a listing's sale window): evaluate the dates yourself if you cache the document. Their passing does not create a feed entry.
* `gifts[].available` is a snapshot (active, and stock minus reservations covers `quantity`, or backorders allowed); the gift's own listing availability in the listing feed is authoritative.
* `reason` on `listed: false` entries: `ended` (the seller ended it), `paused` (set back to draft; it can come back as a new version), `deleted` (demo data removed: the offer is gone for good; this tombstone carries `version: null` and `brandId: null`, like a deleted listing's tombstone).
* `createdAt` (ISO-8601 UTC, full stored precision) is when the offer was first created; it never changes (an edit makes a new `version`). It is the first tie-break of C18.1-2: compare it exactly as sent. Demo tombstones (`reason: deleted`) do not carry it.
* `demo: true` marks the test tool's demo offer (filter it like demo listings).
* Unknown or never-active id: `404 not_found`. Unsigned/forged: the usual `401` codes.

### C18.3 Events (outbox)

| Event | `data` |
|---|---|
| `offer.updated` | `{offerId, version, feedVersion, sellerId, brandId}`: an active version was written (created, edited, re-activated). Fetch the document. |
| `offer.ended` | `{offerId, version, feedVersion, sellerId, brandId, reason}`: the offer stopped applying (`ended`, `paused`, `deleted`). |

Sequence scope `offer:{offerId without dashes}` (C14 ordering, C17.1 `sequenceScope`/`sequence` in the signed body): events of one offer are delivered in order. Dedupe on the body `id`; keep the highest `feedVersion`. Treat events as hints; the feed is the source of truth. Demo offers send no events (they are in the feed).

```json
{ "id": "8a31...", "type": "offer.updated", "createdAt": "2026-11-18T21:30:00Z",
  "data": { "offerId": "6f1c0e52-3b4a-4f0e-9a51-0c8d2b7e9d10", "version": 2, "feedVersion": 1902, "sellerId": "5d2a...", "brandId": "a71e..." },
  "sequenceScope": "offer:6f1c0e523b4a4f0e9a510c8d2b7e9d10", "sequence": 2 }
```

### C18.4 Order intake (`POST /api/internal/v1/orders`, additive)

New optional fields (orders without them behave exactly as before, unless an offer is required by C18.1-8):

| Field | Meaning |
|---|---|
| `offers: [{offerId, offerVersion, giftUnavailable?}]` | Every offer you applied, with the version you priced with (at most 20). `giftUnavailable: true` = an earned gift was out of stock at checkout. |
| `lines[].offerId` | The offer that prices this line (required on **every** eligible line of an applied offer, even below the first step: those units count). |
| `lines[].discountCents` | The tier discount (C18.1-5). |
| `lines[].isGift`, `lines[].giftOfOfferId` | A free gift line: `isGift: true`, `giftOfOfferId`, price/discount/tax 0, no `variantId`. Same `sellerId`/`brandId` as the offer's lines. |

Example: 12 blue + 8 gold cards at $1.00 (20 eligible units, step "from 20: $0.90"), with the wallet and the pack.
```json
{
  "marketplaceOrderId": "MP-10101", "placedAt": "2026-11-18T21:40:00Z", "currency": "USD",
  "buyer": { "displayName": "Jane B.", "relayEmail": "relay-8f3a@relay.solvelt.example" },
  "shippingAddress": { "name": "Jane Buyer", "line1": "1 Main St", "city": "Brooklyn", "region": "NY", "postalCode": "11201", "country": "US" },
  "shippingMethod": "Standard", "shippingSpeed": "standard",
  "lines": [
    { "marketplaceLineId": "L-1", "sellerId": "5d2a...", "brandId": "a71e...", "productId": "0a5d...", "quantity": 12, "unitPriceCents": 100, "discountCents": 120, "taxCents": 0, "offerId": "6f1c0e52-..." },
    { "marketplaceLineId": "L-2", "sellerId": "5d2a...", "brandId": "a71e...", "productId": "1b6e...", "quantity": 8,  "unitPriceCents": 100, "discountCents": 80,  "taxCents": 0, "offerId": "6f1c0e52-..." },
    { "marketplaceLineId": "L-3", "sellerId": "5d2a...", "brandId": "a71e...", "productId": "c3f0...", "quantity": 1, "unitPriceCents": 0, "discountCents": 0, "taxCents": 0, "isGift": true, "giftOfOfferId": "6f1c0e52-..." },
    { "marketplaceLineId": "L-4", "sellerId": "5d2a...", "brandId": "a71e...", "productId": "d401...", "quantity": 1, "unitPriceCents": 0, "discountCents": 0, "taxCents": 0, "isGift": true, "giftOfOfferId": "6f1c0e52-..." }
  ],
  "shipping": [ { "sellerId": "5d2a...", "brandId": "a71e...", "amountCents": 0 } ],
  "offers": [ { "offerId": "6f1c0e52-...", "offerVersion": 2 } ]
}
```
`201 {created: true, orders: [{sellerOrderId, number, sellerId, brandId, status, oversold, giftUnavailable, totalCents: 1800, feeCents: 180, netCents: 1620}]}`. `giftUnavailable` is new in every intake response (false when nothing was left out).

**Mismatch.** The portal recomputes every applied (or required) offer from the exact version and rejects the whole order, creating and reserving nothing:
```json
422
{
  "type": "about:blank", "title": "The offer pricing does not match; nothing was created.", "status": 422, "code": "offer_mismatch",
  "errors": [
    { "index": 0, "kind": "discount", "offerId": "6f1c0e52-...", "message": "The discount of this line must be 0 cents (19 eligible units).",
      "expected": { "discountCents": 0 }, "actual": { "discountCents": 190 } }
  ],
  "expected": { "offers": [ {
    "offerId": "6f1c0e52-...", "offerVersion": 2, "eligibleUnits": 19, "tier": null,
    "lines": [ { "index": 0, "marketplaceLineId": "L-1", "discountCents": 0 } ],
    "gifts": [ { "productId": "c3f0...", "quantity": 1, "minQuantity": 10 }, { "productId": "d401...", "quantity": 1, "minQuantity": 10 } ]
  } ] }
}
```
`index` is the line index (`-1` for an order-level problem). `expected`/`actual` per error depend on the kind:

| `kind` | When |
|---|---|
| `discount` | A line's `discountCents` differs from C18.1-5 (also when other sellers' units were counted: they are not eligible). |
| `not_eligible` | A line names an offer it cannot use: another seller or brand, outside the scope, not a product, or a more specific offer applies. `expected.offerId` is the right one (or null). |
| `offer_missing` | An eligible line has no `offerId` although the offer applies (or was required by the grace rule and not named). |
| `unknown_offer` | A line or gift names an offer missing from `offers[]`, or `offers[]` names an unknown offer. |
| `duplicate_offer` | The same offer twice in `offers[]`. |
| `version_not_active` | `offerVersion` did not apply within the grace window; `expected.offerVersions` lists the versions that did. |
| `gift_not_earned` | A gift line for a gift not earned, or more units than earned, or without `giftOfOfferId`, or with a variant. |
| `gift_missing` | An earned gift is absent (or short) and `giftUnavailable` is not set. |
| `gift_price` | A gift line with a non-zero price, discount or tax. |

Validation errors that are not about offers keep the old `422 invalid_order` shape. A replay of the same `marketplaceOrderId` returns the first result (`200 {created:false}`) as before.

### C18.5 After the order

* The seller sees the discount per line, the gift lines tagged "Free gift" on the order and the packing slip, and a "free gift unavailable" notice when `giftUnavailable` is true. Gift lines ship like any line; their stock is reserved at intake and taken off when shipped.
* Refunds (`refund.issued`), cancellations and fees work unchanged on the discounted amounts (C18.1-9). Each order line keeps `OfferId` + `OfferVersion`, so a later edit of the offer never changes a placed order.

### C18.6 Marketplace side: checklist for the buyer orchestrator

Hand these to the marketplace workers (repos `solvelt-marketplace-api`, `solvelt-marketplace-web`):

- [ ] **Sync offers.** Read `GET /catalog/offers` from a stored cursor (and on `offer.updated` / `offer.ended`); keep the highest `feedVersion` per offer; drop `listed: false` and `demo: true` where demo data is hidden; re-evaluate `activeFrom`/`activeTo` against the clock.
- [ ] **Pricing the cart (server side, one function used everywhere).** Group the cart lines by seller + brand; for each line pick the most specific applicable active offer (C18.1-1/2); count eligible units per offer; take the tier; `discountCents = max(0, price - tierPrice) x qty` per line, where `price` is the line's current price with any active sale; add each earned gift once at $0 (C18.1-6), checking the gift listing's availability. Compute tax on the discounted amounts. Integer cents only.
- [ ] **Checkout.** Re-price with the same function right before placing the order; send `offers[{offerId, offerVersion}]`, `offerId` on every eligible line, the gift lines, and `giftUnavailable: true` when an earned gift was out of stock. Before payment, on `422 offer_mismatch` (or when the pre-charge re-price differs from the quote): refresh the offer from the feed, re-price with `expected`, show the buyer the new total and ask them to confirm; never retry blindly. After payment (orders sent from the paid-order outbox), a `422 offer_mismatch` should not happen because of the pre-charge re-price; if it does, treat that seller's order as failed, refund the buyer in full for it, and log it at error level for a person. Never resend it with changed figures without the buyer's confirmation. Free-shipping thresholds compare the items subtotal after offer discounts (SHIPPING.md section 4). Read `giftUnavailable` in the response and tell the buyer if the portal left a gift out.
- [ ] **Listing badge.** On an eligible listing show the steps and gifts, e.g. "20+ for $0.90 each" or "20 for $18", and "Free wallet from 10"; only while the offer is active now.
- [ ] **Cart message.** Show progress and results per offer: "Add 3 more cards for $0.90 each", "You get a free Card wallet", and "Free gift unavailable" when the gift is out of stock; show the discount per line.
- [ ] **Voice / phone assistant.** Quote totals with the same pricing function as the website (never its own maths), including the gifts, so the phone total equals the web total for the same cart.
- [ ] **Refund display.** Show refunds with the amounts the portal reports; never re-price kept units after a partial refund (C18.1-9).
- [ ] **Tests to mirror the portal's:** 20 mixed cards = $18.00; 25 = $22.50; 10-19 cards include the $0 gift lines; other sellers' cards don't count; a sale price below the tier stays; an edit within 15 minutes still accepts the old version.
