You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

Context: C1/C2 (the catalog feed, questions and content reports; PR #33, docs/CATALOG.md) are on main. The buyer-marketplace orchestrator asked for two additive follow-ups, and both are accepted.

## C6: rate-limit keys for the marketplace endpoints
**Problem:** `POST /api/internal/v1/products/{id}/questions` and `POST /api/internal/v1/content/reports` are limited to 120/min **per IP**. The marketplace is a single caller for all shoppers, so in practice this is a global cap.

**Fix:** follow the SERVICES.md availability pattern.
- **Signed requests** (signature verified) get a partitioned limiter keyed on (verified caller, listingId) for questions and (verified caller, targetId) for reports, plus a generous per-caller ceiling (config, default about 2000/min).
- **Unsigned or invalid-signature requests** get a separate, tight per-IP bucket, evaluated before the signature check, so forged floods can't starve the signed bucket.

**Tests:**
- many questions across different listings from the signed caller are not throttled at 120;
- a single listing is throttled at its own limit;
- unsigned floods are throttled per IP and don't affect signed traffic.

Revert the key change and confirm the first test fails.

## C5: explicit delivery options on listings
Add this to listing items in the feed and in `GET /catalog/listings/{id}`:

```json
"delivery": { "ships": true, "localDelivery": false, "serviceLocations": [] }
```

`serviceLocations` takes values from `"online" | "customer" | "seller"`.

**Derive the values:**
- `ships`: an active shipping profile covers the listing's brand/account (products only).
- `localDelivery`: at least one active local-delivery zone exists for the brand/account (products only).
- `serviceLocations`: the service's enabled location types (services only; empty for products).

**Re-emit `listing.upserted`** with a bumped version for the affected listings whenever shipping profiles, local zones or service locations change. Batch the re-emits through the outbox so a settings change doesn't block the request.

**Tests:** one for each derivation, and one for the re-emit on a settings change. Update docs/CATALOG.md with an example.

## Rules
- Work on ONE new branch, `feature/marketplace-contract-c5c6`, cut from the current origin/main, with ONE PR. Rebase-merge it once CI is green on that exact commit.
- Never rewrite a pushed branch; push a new branch name instead.
- Add one new EF migration, and only if the schema changes.
- Never skip, delete or weaken a test. Revert each fix and confirm its test fails.
- Don't touch any other repository. Keep the changes additive and backwards compatible.
- Other workers have backend PRs open: K (shipping round 4) and I (payments round 4). Rebase onto main when they land.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the final shapes. Schedule your own send_later check-ins until then.
