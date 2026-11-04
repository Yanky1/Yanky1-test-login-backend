You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C7: item weight on catalog listings (requested by the buyer-marketplace orchestrator; accepted)
Weight-based shipping (SHIPPING.md: `baseCents + ceil(weightGrams/1000) × perKgCents`) needs the item weight at checkout, but the C1 listing and variant documents don't include it.

- Add `weightGrams` (int or null) to the listing document, on the feed and on `GET /catalog/listings/{id}`, and to each `variants[]` entry. A variant without its own weight falls back to the product's weight; PRODUCTS.md already stores weight in grams. Dimensions are optional: if products store them, add `dimensionsMm {l, w, h}` or null, the same way.
- If per-listing shipping flags exist (e.g. a free-shipping eligibility or an "excluded from free shipping" flag), expose them as `shipping {freeShippingEligible: bool}`. If they don't exist, don't invent them; say so in docs/CATALOG.md.
- When a weight changes, on the product or on a variant, bump the listing version and emit `listing.upserted`.
- **Tests:**
  - the weight is present;
  - the variant falls back to the product's weight;
  - null when no weight is set;
  - a weight change bumps the version and emits the event;
  - revert the version bump and confirm the last test fails.
- Update docs/CATALOG.md with an example.

## C8: per-group shipping speed and tax on shipping in order ingest (requested by the buyer-marketplace orchestrator; accepted)
Additive changes to `POST /api/internal/v1/orders` (docs/ORDERS.md §1):
- **(a) Speed per group.** Each `shipping[]` entry (one per seller/brand group) may carry `speed` (standard|expedited|overnight, the existing enum) and an optional `method` label (string, at most 80 characters, sanitised).
  - When present, they override the order-level `shippingMethod`/`shippingSpeed` for that group's SellerOrder: its DeliverByAt/SLA, the packing slip and the order detail.
  - When absent, the order-level fields apply, as today.
  - Reject an unknown speed with 422.
- **(b) Tax on shipping.** Each `shipping[]` entry may carry an optional `taxCents` (int ≥ 0, in minor units of the order currency).
  - It is included in the SellerOrder Total.
  - It is excluded from the platform-fee base, consistent with the money formula in ORDERS.md §1.
  - It is shown as "Shipping tax" in the order detail, the packing slip and the payments statement.
  - Refunds of shipping refund the proportional shipping tax.
  - Make sure the payments ledger and release amounts still reconcile; payments-module tests must stay green.
- **Idempotency.** A replay of the same order with the same body is still a no-op. A replay with different values for these fields follows the existing conflict rule.
- **Tests:**
  - a per-group speed sets that group's DeliverByAt;
  - an order-level fallback;
  - an unknown speed gives 422;
  - shipping tax is in the Total and not in the fee base;
  - the ledger entries for an order with shipping tax;
  - a refund including shipping tax;
  - revert each fix and confirm its test fails.
- Update docs/ORDERS.md with examples.

## Rules
- Do this after N2 (C5/C6) is merged. If N3 (review fixes) is still open, you may include C7 and C8 in the N3 PR. Otherwise use ONE new branch, `feature/marketplace-contract-c7c8`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.
- Add one new EF migration, and only if the schema changes.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch any other repository.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the shapes.
