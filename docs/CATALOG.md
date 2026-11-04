# Marketplace contract: catalog feed, buyer questions and content reports

Code: `src/Solvelt.Api/Catalog/**` (feed, versioning, endpoints), `Admin/Moderation.cs` (content reports). Tests: `CatalogFeedTests.cs`, `ContentReportTests.cs`.
Everything here lives under the signed internal API `/api/internal/v1` and is signed **exactly** like `docs/ORDERS.md` section 3.1 (headers
`X-Solvelt-Timestamp`, `X-Solvelt-Nonce`, `X-Solvelt-Signature`; timestamp window, HMAC over method + path **and query** + body hash, single-use nonce).
`401` codes: `unsigned`, `bad_timestamp`, `stale`, `bad_signature`, `replayed`. Errors are `{type, title, status, code, errors?}`.

| Endpoint | Purpose | Rate limit (per IP, per minute) |
|---|---|---|
| `GET /catalog/listings?since=<cursor>&limit=<1..200>` | resumable feed of visible listings | `RateLimit:InternalCatalogPerMinute` (1200) |
| `GET /catalog/listings/{id}` | one visible listing | same |
| `GET /catalog/categories`, `GET /catalog/brands` | taxonomy and brands | same |
| `GET /catalog/images/{imageId}` | original image bytes of a visible listing | same |
| `POST /products/{id}/questions` | a buyer question for the seller's Q&A inbox | per (caller, listing) + per-caller ceiling, section 8 |
| `POST /content/reports` | a content report for staff moderation | per (caller, target) + per-caller ceiling, section 8 |

## 1. Which listings are visible

A listing (a `Product`: type `product` or `service`) is **visible** only while ALL hold: status `Active`, not hidden by staff (`ModeratedAt` empty), seller account `Approved`
(so not suspended), brand `Active`. Drafts, inactive, archived, hidden, moderated and suspended-seller listings are never served, whatever their content.
When a visible listing stops being visible the contract says why (`reason`, first match wins): `moderated` (staff hid or removed it), `archived`, `seller_suspended`,
`brand_removed` (brand archived), `not_active` (draft/inactive).

## 2. Versions and the cursor

Every listing has a `CatalogVersion` (`version` in the documents): a **globally unique, strictly increasing** integer, bumped in the **same transaction** as every change
the marketplace can see (any edit of the listing, its variants, options, images, attributes, FAQs, service details or published Q&A; a status change; the seller's
status, display name or country; the brand; the category; a change of the *public availability state*). Changes the marketplace cannot see (cost price, quality score,
shipping box dimensions, internal notes, pending or hidden questions) do not move it.

Versions come from one counter row (`catalog_counter`). Taking the next versions locks that row until the surrounding transaction commits, so catalog-changing
transactions are serialized and **versions become visible in version order**: a reader that has seen version N has seen every version below N. That is what makes the
cursor safe while writes are running (no gap can appear behind it). The cost: catalog-changing transactions commit one after another (milliseconds each).

`since` is the cursor (an opaque non-negative integer today; treat it as opaque). The feed returns items with `version > since` in version order. A listing that changes
again simply appears again with a higher version, so the consumer keeps only the highest version per `listingId`.

```
GET /api/internal/v1/catalog/listings?since=1840&limit=2
200
{
  "items": [ <listing>, <tombstone> ],
  "nextCursor": "1853",       // the largest version in this page; with an empty page, the cursor you sent
  "hasMore": false            // true: call again with since=nextCursor
}
```
Start with `since` omitted (or `0`). `limit` defaults to 50; outside 1..200 is `400 invalid_limit`; a bad cursor is `400 invalid_cursor`.

A **tombstone** tells a consumer that missed the `listing.unlisted` event that a listing it knew is gone. Only listings that were visible at least once appear, with no content:
```json
{ "listingId": "7b0f...", "version": 1853, "listed": false, "reason": "moderated" }
```

## 3. The listing document

```json
{
  "listingId": "0a5d2c1e-...", "version": 1840, "listed": true, "type": "product",
  "title": "Speckled stoneware coffee mug", "handle": "speckled-mug", "condition": "new", "conditionNote": null,
  "sku": "MUG-001", "barcode": null,
  "bullets": ["Dishwasher safe"], "searchTerms": ["mug", "coffee cup"], "description": "Markdown source", "descriptionHtml": "<p>Markdown source</p>",
  "price": { "amountMinor": 1999, "currency": "USD", "compareAtMinor": 2499,
             "sale": { "amountMinor": 1799, "startsAt": null, "endsAt": "2026-12-01T00:00:00Z", "active": true } },
  "availability": { "state": "in_stock", "allowBackorder": false },
  "options": [ { "name": "Size", "values": ["S", "M"] } ],
  "variants": [ { "variantId": "...", "sku": "MUG-S", "barcode": null, "values": ["S"],
                  "price": { "amountMinor": 1850, "currency": "USD" },
                  "availability": { "state": "low_stock" }, "imageIds": ["..."] } ],
  "category": { "id": "...", "name": "Kitchen & dining", "slug": "kitchen-dining", "path": [ { "id": "...", "name": "Home", "slug": "home" }, { "id": "...", "name": "Kitchen & dining", "slug": "kitchen-dining" } ] },
  "brand": { "brandId": "...", "name": "Clay & Co", "slug": "clay-co", "description": null, "website": "https://example.com" },
  "seller": { "sellerId": "...", "displayName": "Clay & Co Ltd", "country": "US" },
  "attributes": [ { "key": "material", "label": "Material", "unit": null, "value": "stoneware" } ],
  "faqs": [ { "question": "Dishwasher safe?", "answer": "Yes." } ],
  "questions": [ { "questionId": "Q-123", "question": "Microwave safe?", "answer": "Yes, up to 90 s.", "askedAt": "...", "answeredAt": "..." } ],
  "images": [ { "imageId": "...", "variantId": null, "width": 1200, "height": 1200, "contentType": "image/png", "sizeBytes": 48211, "alt": "Front", "isMain": true, "sortOrder": 0,
                "path": "/api/internal/v1/catalog/images/<imageId>" } ],
  "policies": { "returnsDays": 30, "returnsCondition": null, "warrantyMonths": null, "warrantyNote": null, "handlingDays": 2 },
  "service": null,
  "publishedAt": "...", "updatedAt": "..."
}
```
* **`searchTerms`** is a `string[]` built from the seller's search keywords (one free-text field of the product form): split on commas, semicolons and line breaks, trimmed, blanks and case-insensitive duplicates removed, order kept; an empty array when there are none. Editing the keywords creates a new version.
* **Money** is integer minor units plus `currency` (USD today). `variants[].price` is already resolved (the variant's own price, else the listing's). The sale window is sent
  raw (`startsAt`/`endsAt`); `active` is evaluated when the document is built, so evaluate the dates yourself if you cache the document. Sale start/end passing does not create a new version.
* **Availability** is a *state*, never a quantity: `in_stock`, `low_stock` (at or below the seller's low-stock alert), `out_of_stock`, `backorder` (out of stock but backorders allowed);
  services are `available`. A listing with variants takes the best state of its enabled variants. Selling one unit of many does **not** create a version; crossing a state boundary does.
* **Variants**: enabled variants only; images of disabled variants are not listed. **Questions**: only published answers; the asker's name is never included.
* `service` (services only): `{pricingModel, priceMinor, minMinutes, maxMinutes, incrementMinutes, defaultDurationMinutes, online, atCustomer, atSeller, bookingMode, travelFeeMinor}`
  (no meeting links, no addresses, no intake forms).
* **Never present**: cost prices, quality scores, reserved or exact stock, SKUs' internal notes, weights and box dimensions, seller contact name/phone/e-mail/address, brand contact e-mail,
  buyer names, moderation reasons, draft data, or anything of a hidden, moderated or unlisted listing.

`GET /catalog/listings/{id}` returns the same document, or `404 not_found` for anything not visible (unknown, draft, hidden, moderated, unlisted all look the same).

### Categories and brands
`GET /catalog/categories` → `{items:[{categoryId, parentId, name, slug, sortOrder, attributes:[{key,label,type,options,unit,group}]}]}` (the whole tree, small).
`GET /catalog/brands?since=<brandId>&limit=1..200` → `{items:[{brandId, sellerId, name, slug, description, website}], nextCursor, hasMore}` ordered by `brandId`;
only active brands that currently have a visible listing. The brand contact e-mail is private and never served.

### Images
`GET /catalog/images/{imageId}` streams the stored original bytes (metadata already stripped at upload) with the stored `Content-Type`, `X-Content-Type-Options: nosniff` and
`Content-Disposition: inline`. It is `404` for every image that is not an image of a currently visible listing (unknown id, draft, hidden, suspended, unlisted, or a disabled variant's image).

## 4. Events (outbox, `docs/ORDERS.md` section 3.4)

Written in the **same transaction** as the change, through the existing outbox, signed with the outbound secret. `data`:

| Event | `data` |
|---|---|
| `listing.upserted` | `{listingId, version}`: fetch `GET /catalog/listings/{listingId}` (or take it from the feed). |
| `listing.unlisted` | `{listingId, version, reason}` with `reason` in `not_active`, `archived`, `seller_suspended`, `moderated`, `brand_removed`. |

**Trust the body, not the headers:** the webhook signature covers `<timestamp>.<raw body>` only, so take the event id and type from the signed body fields (`id`, `type`), not from `X-Solvelt-Event-Id` / `X-Solvelt-Event-Type` (those headers are not signed). The same applies to `content.report_decided`.

Ordering: an older version is **never delivered after a newer one** of the same listing; the dispatcher holds a message back while an older message of the same listing is still
pending (retrying or in backoff), and an old event re-queued after a newer one was delivered is dropped as superseded. Events can still be delivered more than once (use `X-Solvelt-Event-Id` / the version for idempotency). Treat events as hints and the feed as the
source of truth: if you missed events, resume the feed from your cursor.

## 5. Buyer questions: `POST /products/{id}/questions`

```
{ "questionId": "Q-123", "text": "Is it microwave safe?", "buyerDisplayName": "Sam" }
```
* `questionId` (required, ≤100) is the idempotency key. First delivery `201 {duplicate:false, questionId, id}`; a repeat `200 {duplicate:true, ...}` with the same `id`; the same id for
  another listing `409 question_id_conflict`. Parallel deliveries store one row.
* `text` 1–1000 chars, `buyerDisplayName` ≤80 (default "A shopper"), else `422 invalid_question`. Only a **visible** listing accepts questions (`404 not_found` otherwise).
* The question lands in the seller's existing Q&A inbox (`/api/v1/seller/questions`) as unanswered. It becomes part of the listing only when the seller publishes the answer
  (that bumps the version; the asker's name is never published). `/api/dev/questions` stays for Development/Testing only.

## 6. Content reports: `POST /content/reports`

```
{ "reportId": "R-9001", "kind": "review", "targetId": "review-4711", "reason": "Contains personal data", "note": "optional", "reporterRef": "buyer-ref-77" }
```
`kind`: `review` | `listing` | `message`. `targetId` is the marketplace's id for a review, the listing id for a listing, the **marketplace message id** for a message
(the `messageId` you sent in `POST /messages`). `reportId` (≤100) is the idempotency key: `201 {reportId, status:"open", decision:null, duplicate:false}`, a repeat `200 ... duplicate:true`
(with the decision once staff decided), the same id for a different report `409 report_id_conflict`. Validation errors `422 invalid_report`, unknown listing/message `404 not_found`.
`reporterRef` is the marketplace's opaque reference for the reporter (stored for staff; never a name or e-mail).

The report joins the staff moderation queue (`docs/ADMIN.md`) as one row per target: listing reports reuse the existing "report listing" row, message reports flag the message through the
existing report-message path, review reports create a `review` row. A repeat report of a decided target reopens the row; each report keeps its own decision.
The portal stores only the report and its decision for reviews (they live on the marketplace).

Staff decide in the admin panel; each decided report gets one outbox event:

| Event | `data` |
|---|---|
| `content.report_decided` | `{reportId, kind, targetId, decision, reasonShownToUser}`; `decision` in `dismissed`, `hidden`, `removed`; `reasonShownToUser` is optional text written by staff for the reporter, `null` when none (the internal staff reason is never sent) |

| Staff action | decision |
|---|---|
| `moderation/{id}/dismiss` | `dismissed` |
| `moderation/{id}/hide-content`, `listings/{id}/hide` | `hidden` (a listing is hidden, reversible) |
| `moderation/{id}/remove-content` | `removed` (a listing is archived and stays unlisted as `moderated`) |
| `moderation/{id}/warn-seller` | `dismissed` (the content stays up; only the seller is warned) |
| `moderation/{id}/escalate-suspend` | `removed` (the seller is suspended, so their listings are unlisted) |

Reviews, messages: the marketplace acts on its own content when it receives the event.

## 7. Rollout and existing data
* One additive EF migration (`MarketplaceContract`). At start-up the portal gives every Active listing that has no version yet its first version (and a `listing.upserted` for visible ones), so a
  marketplace that starts from `since=0` receives the complete catalog; nobody has to edit listings first. Listings that are not visible then (suspended seller, hidden, draft) get their first
  version when they become visible.
* Configuration: the signed API needs `Orders:InboundSecret` / `Orders:OutboundSecret` and a `Orders:WebhookUrl` for the events, as for orders. Rate limits: `RateLimit:InternalCatalogPerMinute`,
  and the write limits of section 8.
* Operations: a stuck `listing.*` event (Dead after `Orders:OutboxMaxAttempts`) does not block newer versions of the same listing; re-queueing it after a newer version went out marks it `superseded by a newer version` instead of sending it (resume the feed instead).

## 8. Rate limits of the marketplace write endpoints
The marketplace is ONE caller for all shoppers, so a per-IP key would be a global cap (it was 120/min for everybody). Questions and reports therefore follow the availability pattern of `docs/SERVICES.md`:

| Who | Bucket (per minute) | Config (default) |
|---|---|---|
| **Signature-verified** question | (caller, **listingId**) | `RateLimit:InternalQuestionsPerListingPerMinute` (60) |
| **Signature-verified** report | (caller, **targetId**) | `RateLimit:InternalReportsPerTargetPerMinute` (60) |
| Every verified question / report | the caller's ceiling | `RateLimit:InternalQuestionsPerCallerPerMinute` / `RateLimit:InternalReportsPerCallerPerMinute` (2000 each) |
| **Unsigned, forged, stale or replayed** requests | per client IP, spent only by failures | `RateLimit:InternalUnsignedPerMinute` (60) |

* The caller is `marketplace`: the holder of `Orders:InboundSecret` is the only verified identity this API has.
* A request that fails verification is answered with its own status (`401 ...`) while the per-IP bucket has room and with `429 rate_limited` after that. Verified requests never touch that bucket, so a forged flood from any address (even the marketplace's own) cannot make signed requests fail.
* `429` bodies are `{code: "rate_limited", ...}` with `Retry-After: 60`. The old keys `RateLimit:InternalQuestionsPerMinute` and `RateLimit:InternalReportsPerMinute` are gone.

