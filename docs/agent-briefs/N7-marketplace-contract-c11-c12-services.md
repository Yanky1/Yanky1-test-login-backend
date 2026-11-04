You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C11: cancellation-policy snapshot in bookings and services (requested by the buyer-marketplace orchestrator; accepted)
The buyer must see the cancellation fee before confirming or cancelling. SERVICES.md §4 snapshots the policy at booking time (`Booking.PolicyJson`), but the marketplace can't read it.

Additive changes:
- **Booking summary.** Add `policy: { cancelFreeHours, cancelFeePercent, noShowFeePercent, maxReschedules, approvalHours }` to:
  - `GET /api/internal/v1/bookings/{marketplaceBookingId}`;
  - the hold and confirm responses;
  - the data of every `booking.*` outbox event that carries the booking summary.
  The values come from the booking's **snapshot** (PolicyJson), never from the current service, so later service edits never change it. Use `null` for values the policy doesn't set; `approvalHours` is null for instant-booking services.
- **Catalog service document.** Add the same `policy` object, holding the service's **current** policy, under `service` in the catalog feed and in `GET /catalog/listings/{id}`. When the seller changes the policy, bump the listing version and emit `listing.upserted`, the same way service details already bump.
- **Docs.** Update docs/SERVICES.md and docs/CATALOG.md with examples. State that `cancellationFeeCents` in `booking.cancelled` stays authoritative, and that the fee formula is round-half-up(total × cancelFeePercent / 100), applied inside the free window only. Note that the buyer side can show a preview.
- **Redaction.** The policy snapshot is not PII and stays after redaction (SERVICES.md §186). Make sure the new event data doesn't reintroduce PII.

**Tests:**
- the booking summary carries the snapshot;
- editing the service policy after booking leaves the booking's policy unchanged but changes the catalog service policy and bumps its version;
- instant booking gives approvalHours null;
- `booking.*` events carry the policy;
- revert "snapshot, not current" and confirm a test fails.

## C12: booking options endpoint (requested by the buyer-marketplace orchestrator; accepted as a separate endpoint, not part of the feed)
Add a new signed internal endpoint, `GET /api/internal/v1/services/{serviceId}/options`, with the same signing, stale and replay rules as the other internal GETs. It returns:
```
{ serviceId, version,
  addOns: [{ id, name, description?, priceCents, extraMinutes }],
  intakeQuestions: [{ id, label, kind: "text"|"longText"|"choice"|"yesNo"|"number", required, choices?: [string], maxLength? }],
  providers: [{ providerId, displayName, photoImageId? }],
  assignment: "leastBusy"|"roundRobin"|"buyerChoice",
  policy: { same shape as C11 },
  travel: { feeCents?, serviceAreaPostalCodes?: [string], serviceRadiusKm? } }
```
- **What it serves:** only services whose listing is visible under the catalog visibility rule. Anything else returns 404.
- **Providers:** list only the eligible providers, the denormalised `ReadyProviders`. `displayName` is the provider's public display name: first name plus last initial, unless the provider set a public name. Never include email, phone, calendar data or internal ids beyond the opaque `providerId`. `photoImageId` only when the image is public and served by the catalog image endpoint.
- **Assignment:** map the existing assignment rule onto the enum. If the rule isn't buyer choice, `providers` may still be listed for display, but the marketplace must not send a provider choice; say so in the docs.
- **Leave out:** meeting links, online instructions, the seller address and any other personal data. Use integer cents only.
- **Caching:** send an `ETag`. The value is the listing version or a hash of the options; answer `If-None-Match` with 304. Add `Cache-Control: private, max-age=300`. Changing add-ons, intake questions, providers, assignment, policy or travel bumps the listing version and emits listing.upserted.
- **Bookings:** hold and confirm already accept add-on ids, intake answers and an optional provider. Validate that the ids exist in the current options; an unknown id returns 422. Document the request fields in SERVICES.md next to this endpoint.
- **Tests:**
  - visible versus 404;
  - provider PII is never present: assert on the JSON keys;
  - ETag and 304;
  - changing options bumps the version;
  - an unknown add-on in a hold returns 422.
  - Revert each guard and confirm its test fails.
- **Docs:** add a section to CATALOG.md or SERVICES.md with an example.

## Rules
- Queue: do this after N3, N4, N5 and N6. You may fold it into whichever of those PRs is still open. Otherwise use ONE new branch, `feature/marketplace-contract-c11c12`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.
- No migration should be needed; PolicyJson already exists.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the shapes.
