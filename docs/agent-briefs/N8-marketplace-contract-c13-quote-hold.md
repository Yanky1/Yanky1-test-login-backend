You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C13: accepting a quote creates a hold, not a confirmed booking (requested by the buyer-marketplace orchestrator; option (a) accepted)
Today, accepting a quote through the marketplace (SERVICES.md, quotes) creates a CONFIRMED booking that isn't paid yet. If the buyer then doesn't pay, the marketplace has to cancel it, which can look like a buyer cancellation (fee, provider notification).

**Change:**
- **Accepting a quote** creates a **hold** on the quoted slot, using the same hold model, expiry and slot-blocking rules as normal holds, at the quoted price. The response returns the hold id and its expiry.
- **The marketplace's existing confirm call**, made after the Stripe payment succeeds, turns the hold into a booking. It is then Confirmed or Requested according to the service's booking mode: a seller-sent quote counts as already approved, so it is Confirmed. The usual `booking.confirmed` event is sent at that point, not at acceptance.
- **An expired hold** releases the slot. The quote goes back to its accepted-but-unbooked state, or expires according to the quote's own validity window: pick one and record it in DECISIONS. There is **no fee**, no buyer-cancellation count, and no provider "buyer cancelled" notification; the provider sees "quote not paid in time" at most.
- **Policy snapshot:** taken when the hold is confirmed, the same as normal bookings. The C11 policy fields therefore come from that snapshot.
- **Idempotency:** accepting the same quote twice returns the same live hold. Accepting after the quote's expiry gives 409 `quote_expired`. Accepting while a hold is live gives the same hold.
- **Backward compatibility:** quotes accepted before this change keep their existing confirmed bookings. Record the change in the ORDERS.md and SERVICES.md contract notes, plus a short "migration note for the marketplace" paragraph.

**Tests:**
- accept → hold, no confirmed event;
- confirm → booking Confirmed, with the event and the policy snapshot;
- hold expiry: no fee, no buyer-cancel count, slot freed;
- double accept is idempotent;
- accept after the quote expired → 409;
- a parallel accept and a normal hold on the same slot: only one wins (reuse the existing slot concurrency guard).
- Revert each guard and confirm its test fails.

## Clarification for N4 (C8) and N5 (C9): shipping tax in refund and cancel events. Apply it while doing N4/N5; it takes precedence over their wording.
These are the amounts on events from the portal to the marketplace:
- `refund.issued.amountCents` = the TOTAL to give back to the buyer: the line refunds (including line tax) + `shippingCents` + `shippingTaxCents`.
- `shippingCents` = the shipping refund **net of tax**.
- `shippingTaxCents` (new, int ≥ 0) = the shipping tax refunded. It is prorated from the group's shipping `taxCents`: floor(shippingTaxCents × refundedShipping / shipping), with the final shipping refund of that group taking the remainder.
- `taxCents` (from C9) = the total tax included in amountCents = Σ lines[].taxCents + shippingTaxCents.
- `order.cancelled`: `refundDueCents` includes the shipping tax for a full cancel. Add `shippingTaxCents` there too; it is 0 for a partial remainder cancel, which carries no shipping.
- Worked example (this must be a test). Shipping 500 with shipping tax 45, and a 40 % shipping refund (200) with no lines: amountCents 218, shippingCents 200, shippingTaxCents 18, taxCents 18. A second refund of the remaining 300 gives shippingTaxCents 27, so the remainders sum to exactly 45.
- `refund.processed`, from the marketplace to the portal, is unchanged: amountCents = items excluding tax, and shippingCents = shipping excluding tax. Tax never enters the ledger.
- Document all of this in the ORDERS.md "Money in events" paragraph from N5.

## Rules
- Queue: do this after N7. You may fold it into the N7 PR if that's still open. Otherwise use ONE new branch, `feature/marketplace-contract-c13`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.
- Add one new EF migration, and only if the schema changes.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the final flow.
