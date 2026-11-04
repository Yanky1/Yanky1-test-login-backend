You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C14: order event ordering and refund accounting on cancel (requested by the buyer-marketplace orchestrator; accepted)
**Problem.** The OutboxDispatcher (Orders/Integration.cs:135-140) sends due messages in CreatedAt order. Per-aggregate ordering (`orderKey`) is only used for listing events. So when an earlier `refund.issued` fails and goes into backoff, a later `order.cancelled` (full cancel) for the same seller order can be delivered first. Its `refundDueCents` (= TotalCents − RefundedCents) already nets out that refund, and the marketplace can't tell which refunds were counted.

**Change (additive):**
1. **Per-seller-order ordering** for every order outbox event (order.*, refund.issued, shipment.*, return.*, delivery.*, and any other event whose aggregate is a seller order).
   - Pass a monotonic per-seller-order `orderKey`, reusing the existing dispatcher rule: never send a message while an older one of the same aggregate is pending, leased or in backoff.
   - Head-of-line blocking: when an older message becomes Dead, later messages of that aggregate must not be blocked forever. Either skip Dead predecessors and mark the successor with `"predecessorDead": true` in its envelope metadata, or keep blocking and surface it in the admin outbox view. Choose one, record it in DECISIONS, and test it.
   - Bookings: do the same per booking for booking.* events, if they aren't already ordered.
2. **`order.cancelled`** gains:
   - `refundIdsAccounted: [refundId…]`: every refund already recorded on that seller order when the cancel happened;
   - `previouslyRefundedCents`: their sum, including tax and shipping.
   For a partial remainder cancel, list the refunds recorded so far too.
3. **Document** in ORDERS.md that events of one seller order (or one booking) are delivered in order. Name the Dead-predecessor rule, and recommend that the marketplace still dedupe on the body `id`.

**Tests:**
- `refund.issued` fails delivery, then `order.cancelled` is enqueued: the cancel is not sent until the refund is delivered;
- the Dead-predecessor rule;
- `refundIdsAccounted` and `previouslyRefundedCents` after two refunds and a cancel;
- events of different seller orders still flow in parallel, with no global blocking.
- Revert the per-aggregate key and confirm the first test fails.

## C15: confirming a lapsed hold fails (requested by the buyer-marketplace orchestrator; accepted)
**Today.** `POST /bookings/{mid}/confirm` on a hold whose `HoldExpiresAt` has passed (status Hold or Expired) re-confirms it when the slot is still free (BookingService.cs ~347-351; SERVICES.md "re-confirm of an expired hold"). The marketplace's buyer flow tells the buyer the hold expired, so a confirm that races expiry must not create a booking.

**Change:**
- A confirm on a hold that is no longer live (Hold with `HoldExpiresAt <= now`, or Expired) returns **409 `hold_expired`**. The check happens inside the existing provider lock, so the decision and the time are taken under the lock.
- A confirm on a Cancelled, Declined or otherwise final hold returns **409 `hold_not_confirmable`**. Today it returns a generic 409; give it this stable code.
- A confirm on an already-confirmed booking still returns the existing booking (idempotent, unchanged).
- Remove "re-confirm of an expired hold" from SERVICES.md, and document both codes. If the seller-portal UI or another internal path relies on re-confirming expired holds, keep that path working through an explicit internal-only flag that is NOT exposed on the marketplace endpoint. Record the decision in DECISIONS.
- Quote holds from C13 (N8) follow the same rule.

**Tests:**
- an expired hold with the slot still free → 409 hold_expired, and no booking or event;
- a hold expiring between the request and taking the lock → 409. Use the fake clock inside the lock hook.
- cancelled → 409 hold_not_confirmable;
- confirmed → idempotent.
- Revert the check and confirm the first test fails.

## Rules
- Queue: after N8. You may fold it into an open N-PR.
- One new EF migration, only if the schema changes.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit.
