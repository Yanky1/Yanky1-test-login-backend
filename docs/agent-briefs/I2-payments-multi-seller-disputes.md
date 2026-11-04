You are worker I (seller payments and payouts), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C10: disputes on multi-seller orders (requested by the buyer-marketplace orchestrator; accepted as option (a))
**Problem.** One Stripe charge covers the whole marketplace order, so one dispute (`dp_…`) can span several seller orders. Today `POST /api/internal/v1/payment-events` keys disputes on a unique ExternalId = disputeId (PaymentsModel.cs:47, Ingest.cs:241-265). So when the marketplace sends `dispute.*` once per seller order with the same disputeId, only the first seller order is debited, with its share, and the others never are.

**Change (additive, backward compatible):**
1. **The dispute key becomes (disputeId, seller order).**
   - The unique index is (ExternalId, SellerOrderId) for order disputes, and stays (ExternalId, BookingId) for bookings.
   - The ledger idempotency key is `dispute:{dp}:{sellerOrderId}`, plus the existing suffixes for fee, won and similar.
   - `sellerOrderNumber` is REQUIRED on dispute.* events when the marketplace order has more than one seller order: return 422 `seller_order_required`, like refund.processed.
   - Single-seller orders keep working without it.
   - Status updates, won, lost, closed and the never-reopen rule all apply per (dp, seller order).
   - `amountCents` and `feeCents` are that seller order's share. Validate them against that seller order's sale, items plus shipping, and return 422 `exceeds_sale` above it.
2. **Platform-account Stripe `charge.dispute.*` webhook.** Find out how it matches today (paymentRef → seller order) and document it in PAYMENTS.md.
   - When the charge maps to exactly one seller order, keep today's behaviour.
   - When the charge maps to several seller orders, the Stripe webhook must NOT debit any seller on its own.
     - It records or updates the dispute's status and evidence date for every matching (dp, seller order) row that already exists.
     - If no marketplace allocation has arrived yet, it creates a staff-visible "unallocated dispute" item, with no ledger entries, so a dispute is never missed.
     - Debits come only from the marketplace's per-seller-order `dispute.*` events.
   - A marketplace event and a Stripe event for the same (dp, seller order) must never double-debit.
3. **Migration.** ONE new EF migration for the index change. Existing rows already have a single seller order; backfill SellerOrderId from the order the row was booked against.
4. **Docs.** Update PAYMENTS.md with the multi-seller dispute rules, an example of the per-seller-order events, and the Stripe webhook matching rule. Add a row to the CONTRACT notes in ORDERS.md if it references disputes.

**Tests:**
- two seller orders under one dp: each debited exactly its share, and replays are no-ops;
- a missing sellerOrderNumber on a multi-seller order gives 422;
- a share above the sale gives 422;
- won and closed per seller order; an older event never reopens a closed dispute;
- a Stripe webhook on a multi-seller charge: no debit, and an unallocated item when no marketplace event exists, cleared or linked once the marketplace events arrive;
- a Stripe event and a marketplace event for the same (dp, seller order) debit once;
- single-seller behaviour unchanged.
- Revert each guard and confirm its test fails.

## Rules
- Work on ONE new branch, `fix/payments-multiseller-disputes`, cut from the current origin/main, with ONE PR. Rebase-merge it once CI is green on that exact commit.
- Never rewrite a pushed branch.
- Never skip, delete or weaken a test.
- Don't touch other repositories.
- Integer minor units only.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the final event rules.
