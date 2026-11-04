You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C9: tax split in refund and cancel events (requested by the buyer-marketplace orchestrator; accepted)
Today the amounts the portal sends to the marketplace include line tax:
- `refund.issued.amountCents` and `lines[].amountCents` use LineNet, which includes taxCents.
- `order.cancelled.refundDueCents` includes tax.

But `refund.processed` (marketplace → portal) must exclude tax, because tax never enters the ledger. The marketplace therefore has to back the tax out itself.

Make these additive changes:
- `refund.issued`: add `taxCents` (the total tax included in amountCents) and `lines[].taxCents`. Compute them with the same proration as the line amount: floor, with the final refund of a line taking the remainder. They must equal exactly what the marketplace has to subtract to get the `refund.processed` items amount.
- `order.cancelled`: add `taxCents` (the tax included in refundDueCents), and per-line `taxCents` when `partial:true`.
- docs/ORDERS.md: add a short "Money in events" paragraph stating which amounts include tax and shipping:
  - refund.issued.amountCents = lines incl. tax + shippingCents;
  - refundDueCents for a full cancel = total − refunded, and for a partial cancel = the cancelled lines incl. tax, without shipping;
  - refund.processed.amountCents = items excluding tax, plus a separate shippingCents.
  Cross-link this from PAYMENTS.md.

**Tests:**
- a multi-unit line refunded in three parts: the taxCents sum equals the line's taxCents, and `amountCents − taxCents − shippingCents` equals what refund.processed accepts, with no 422;
- a full cancel and a partial cancel each carry the correct taxCents;
- revert the remainder rule and confirm a test fails.

## Rules
- Do this after N3 and N4. You may fold it into the N4 PR if that's still open. Otherwise use ONE new branch, `feature/marketplace-contract-c9`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.
- No migration should be needed.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the shapes.
