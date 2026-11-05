# Reviews and seller replies (workstream P, contract C17)

Buyers review listings on the marketplace (a separate system). The marketplace pushes each review to the portal through the **signed internal API**; the seller reads it in the
portal, may answer with ONE public reply, and every reply goes back to the marketplace through the **outbox** (signed webhook, ordered per review). Staff can moderate a
reported reply through the existing content-report queue.

Code: `src/Solvelt.Api/Reviews/**`, one EF migration `Reviews`, moderation changes in `Admin/Moderation.cs`, demo seeding in `Demo/**`. Tests: `tests/Solvelt.Api.Tests/Review*Tests.cs`.
Related: signing, replay, stale and outbox rules `docs/ORDERS.md` section 3; content reports `docs/CATALOG.md` section 6; staff queue `docs/ADMIN.md`; decisions `docs/DECISIONS.md` (P1-P12).

## 1. Model

| Entity | Purpose |
|---|---|
| `Review` (`reviews`) | One buyer review: marketplace `reviewId` (unique, <= 50 chars), seller, product (listing), `rating` 1-5, `text` (<= 5000), `buyerDisplayName` (<= 60), `createdAt`, `editedAt`, `state` (`visible` / `hidden` / `removed`), `stateReason`, claim counter `Version`. **Nothing else about the buyer is stored**: no buyer id, no email, no photos (anything extra in the body is ignored). |
| `ReviewReply` (`review_replies`) | The seller's reply. At most one **active** reply per review (partial unique index); `id` (the `replyId`) is stable across edits. A removed reply keeps its row with `removedAt` / `removedReason` and an empty text. |

A review points at a product, not at a brand: the seller side always joins to the products the caller may see, so brand scope follows the product's brand at the moment of the call.
Reviews of a deleted product are no longer listed (no foreign key, so deleting a product never needs to touch reviews).

## 2. Marketplace -> portal (signed `/api/internal/v1`)

Signed exactly like every internal call (`docs/ORDERS.md` 3.1: `X-Solvelt-Timestamp`, `-Nonce`, `-Signature`; stale, replay, `401` codes). Failed verification only spends the per-IP
bucket; verified calls are limited per caller: `RateLimit:InternalReviewsPerCallerPerMinute` (2000), both endpoints share it (`429 rate_limited`, `Retry-After: 60`).

### `POST /api/internal/v1/reviews`
```json
{ "reviewId": "rev-8841", "listingId": "<product guid>", "sellerId": "<seller guid>", "rating": 4, "text": "Lovely mug.", "buyerDisplayName": "Jane B.",
  "createdAt": "2026-11-05T10:00:00Z", "editedAt": null }
```
* `201 {reviewId, id, duplicate:false, updated:false, state:"visible"}` for a new review (`id` is the portal's id, used in the seller API and in `aggregateId`).
* **Idempotent by `reviewId`.** The same body again: `200 {duplicate:true}`. A **newer** `editedAt` than the stored one updates rating, text and display name: `200 {updated:true}`. An **older or equal** `editedAt` (or none) is ignored: `200 {duplicate:true}`, nothing changes. Parallel deliveries end with the newest edit (one conditional UPDATE). A `removed` review is never changed again.
* **Validation, anything else is `422 invalid_review`** with `errors:{field: message}`: `listingId` must be a product of that seller and `sellerId` must match (no cross-seller injection; an existing `reviewId` cannot move to another listing); `rating` an integer 1-5 (`4.5` and `"4"` are refused); `text` <= 5000 characters (may be empty); `buyerDisplayName` <= 60 characters, stored exactly as sent; `reviewId` 1-50 characters; `createdAt` (and `editedAt` when present) ISO 8601.
* Demo products accept reviews like any other.

### `POST /api/internal/v1/reviews/{reviewId}/state`
```json
{ "state": "hidden", "reason": "Under review" }
```
`state` is `visible`, `hidden` or `removed`; `reason` is optional (<= 300). Answers `200 {reviewId, state, changed}`; the same state again is `200 changed:false`; unknown `reviewId` `404 not_found`; bad state or reason `422 invalid_state`.
* `hidden` and `removed` take the review out of the seller's normal list. Owners and Admins keep a **"Hidden by marketplace"** view (`GET /seller/reviews?view=hidden`, with `stateReason`). `hidden` can go back to `visible`.
* While not visible, replying is refused (`409 review_not_visible`); an existing reply stays but cannot be edited.
* `removed` is **final** (a later `visible` is `409 review_removed`) and **redacts**: text and display name are cleared (the rating stays for the averages), the reply is removed and `review.reply_removed` (reason `review_removed`) is queued in the same transaction.

## 3. Seller API (`/api/v1/seller/reviews`, bearer token, verified email, active account)

| Call | Notes |
|---|---|
| `GET /seller/reviews?rating=1..5&replied=true\|false\|all&productId=&brandId=&view=visible\|hidden&page=&pageSize=` | `pageSize` 1-100 (25), newest first. `view=hidden` is Owner/Admin only (`403` otherwise). Bad filters `422`. |
| `PUT /seller/reviews/{id}/reply` `{text}` | Creates (`201`) or edits (`200`) the reply; text trimmed, 1-2000 characters. The same text again is a no-op (`200`, no event). |
| `DELETE /seller/reviews/{id}/reply` | `204`; `404` when there is no reply. Allowed while the review is hidden. |

List response: `{items, total, page, pageSize, summary:{count, average, counts:{"1".."5"}}, hiddenCount}`. The summary counts **visible** reviews of the chosen product/brand (not narrowed by the rating or reply filter, so the star filter can show its counts); `hiddenCount` is only for Owner/Admin (null otherwise). Item keys are exactly
`id, reviewId, productId, productTitle, rating, text, buyerDisplayName, createdAt, editedAt, state, reply:{id, text, createdAt, editedAt}|null` (+ `stateReason, stateChangedAt` in the hidden view); demo reviews carry `demo: true` (the demo response filter).

* **Roles** (`docs/TEAMS.md`): Owner, Admin and Manager reply (`BrandWorkWrite`, Manager only within their brands); Viewer ("staff", read-only) reads within their brand scope (`BrandWorkRead`) and gets `403` on writes; Drivers have no access. Another account's, an out-of-scope or an unknown review is `404`, never `403` (IDOR-safe).
* **Contact details:** a reply with an email address, a phone number or a link (the `ContactDetector` of the messaging module; the payment-handle rule is not applied) is `422 contact_details_not_allowed`.
* **Rate limit:** `Reviews:RepliesPerHour` (30) creates and edits per seller account (`429 rate_limited`, `Retry-After: 3600`); per instance, counted in memory.
* **Audit** (`audit_events`): `review.reply_posted`, `review.reply_edited`, `review.reply_deleted`; the detail holds ids only, never the text.

## 4. Portal -> marketplace events (outbox)

Same envelope, signing and at-least-once rule as every event (`docs/ORDERS.md` 3.4: dedupe on the signed body `id`). **Ordering (C14):** all events of one review share the sequence scope `review:{reviewId}` (the marketplace's `reviewId`) and get increasing `OrderKey`s (1, 2, 3 ...), assigned in the transaction that writes the change; the dispatcher never sends one while an older event of the same review is pending. A Dead predecessor adds `"predecessorDead": true` to later events, as for orders. The receiver keeps the **latest by sequence**.

| Event | `data` |
|---|---|
| `review.reply_posted` | `{reviewId, replyId, text, repliedAt, editedAt}`: sent on create (`editedAt: null`) and on **every edit** (`editedAt` = the edit time; `repliedAt` stays the creation time). |
| `review.reply_removed` | `{reviewId, replyId, removedAt, reason}`, `reason` in `seller_deleted`, `review_removed`, `staff_removed`. |

```json
{ "id": "5b0e...", "type": "review.reply_posted", "createdAt": "2026-11-05T10:05:00Z",
  "data": { "reviewId": "rev-8841", "replyId": "0f6c...", "text": "Thank you!", "repliedAt": "2026-11-05T10:05:00Z", "editedAt": null } }
{ "id": "9a21...", "type": "review.reply_removed", "createdAt": "2026-11-05T11:00:00Z",
  "data": { "reviewId": "rev-8841", "replyId": "0f6c...", "removedAt": "2026-11-05T11:00:00Z", "reason": "staff_removed" } }
```
The events carry no buyer data (not even the display name). Every writer of one review (seller reply, delete, marketplace removal, staff removal) first **claims** the review with an atomic version bump inside its transaction, so two writers can never take the same sequence number (the loser answers `409 conflict`, retry).

## 5. Moderation (staff, `docs/ADMIN.md`)

The marketplace reports a reply through the existing intake: `POST /api/internal/v1/content/reports {reportId, kind: "review_reply", targetId: <replyId>, reason, note?, reporterRef}` (`docs/CATALOG.md` section 6). `targetId` must be the `replyId` of an active reply (`422` if it is not a guid, `404` if unknown or already removed). It becomes one queue row of kind `review_reply` under the seller; the moderation detail shows the **reply text and the review context** (rating, text, display name, product; text masked for roles that cannot act).

| Staff action | Effect |
|---|---|
| `dismiss` | Reply stays; `content.report_decided` (`dismissed`). Nothing else is sent. |
| `remove-content` | Reply removed (`removedReason staff_removed`), `review.reply_removed` (`staff_removed`) queued, the seller is e-mailed (to the Owners, normal mail path, after the decision is committed), `content.report_decided` (`removed`). |
| `hide-content` | `409 unsupported_decision` (a reply cannot be hidden). |

The decision claims the report row first (N10): of a parallel dismiss and remove **exactly one wins** (`200`), the other gets `409 already_resolved` without any side effect, so the event and the e-mail always match the winner. If the seller deleted the reply a moment earlier, the removal still decides the report but sends no second event.

## 6. Demo data (`docs/DECISIONS.md` O1-O11)

The demo fill (`Demo:Enabled`) seeds three reviews through the same engine: 5 stars with a reply, 3 stars, 1 star, registered as `review` in the demo registry (so `demo: true` shows in the API). The fill runs with outbox events suppressed, and later events about a registered demo review are dropped (`DemoContext.IsDemo` on the review id), so demo replies reach nobody. Removing the demo data deletes the demo reviews with their replies, and any review the marketplace sent for a demo product (the product goes too).

## 7. Retention and privacy
* Stored about the buyer: the display name the marketplace sent, nothing else. A **removed** review loses text and name at once (the rating stays for averages).
* Replies keep their text while active; a removed reply keeps only ids and timestamps (text cleared). Audit entries and e-mails hold no review or reply text.
* There is no age-based deletion of reviews: they are public content of the marketplace. They disappear with their product only from the seller's view (no foreign key); demo removal deletes them for real.

## 8. Configuration
`Reviews:RepliesPerHour` 30; `RateLimit:InternalReviewsPerCallerPerMinute` 2000 (plus the existing `RateLimit:InternalUnsignedPerMinute` for failed requests). Signing secrets as in `docs/ORDERS.md` section 8.
