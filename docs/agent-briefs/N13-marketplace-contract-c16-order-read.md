You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

## C16: signed order read endpoint for reconciliation (requested by the buyer-marketplace orchestrator; accepted)
The marketplace needs to reconcile a seller order when an event arrives with `predecessorDead: true` (C14), and for a manual reconcile. Bookings already have `GET /bookings/{mid}`; orders have no read endpoint.

Add `GET /api/internal/v1/orders/{marketplaceOrderId}`, with the same signing, stale and replay rules as other internal GETs and a per-caller rate limit (config, e.g. 600/min). It returns:
```
{ marketplaceOrderId,
  sellerOrders: [{
    sellerOrderId, sellerOrderNumber, sellerId, brandId, status, updatedAt,
    currency, totalCents, taxCents, shippingCents, shippingTaxCents, refundedCents,
    lines: [{ marketplaceLineId, sku, quantity, shippedQty, cancelledQty, refundedQty, returnedQty }],
    refunds: [{ refundId, amountCents, taxCents, shippingCents, shippingTaxCents, reason, createdAt }],
    shipments: [{ shipmentId, method, carrier, trackingNumber, status, shippedAt, deliveredAt }],
    returns: [{ returnId, status, createdAt, updatedAt }],
    lastEventSequence   // the highest OrderKey enqueued for this seller order's scope (C14), or null
  }] }
```

- **PII:** none beyond what events already carry. No buyer name, address, email or phone. Tracking numbers are already in events, so they are fine.
- **Not found:** an unknown marketplace order returns 404. After redaction (retention rules), return what remains, with no PII.
- **Money:** integer minor units throughout. The amounts must be exactly the ones the events sent: reuse the same calculations, don't recompute differently.
- **Docs:** add it to docs/ORDERS.md (the signed API table, with an example), next to the `predecessorDead` paragraph.

**Tests:**
- signature, stale and replay;
- 404;
- a multi-seller order with refunds, shipments and a return has the right quantities and money;
- `lastEventSequence` matches the outbox;
- no PII keys in the JSON;
- rate limit.
- Revert each guard and confirm its test fails.

## Rules
- Queue: after N8, N10, N11. You may fold it into an open N-PR.
- No migration is expected.
- Respect the backend merge queue (one PR at a time).
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

**Finish:** message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and the shape.
