You are worker N (marketplace contract), working on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). It is URGENT: do it BEFORE any other queued brief. Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the fix is built, tested, green, merged and pushed.

## Bug (serious): an order event is dropped because of a sibling seller order (from the verification of PR #64, main 2622795)
- **Where:** Orders/Integration.cs:209-217. The older listing rule still applies to every event that has an `OrderKey`, now including the scoped order and booking events from C14. That rule is: drop a pending event as "superseded" when a newer event of the same aggregate has been delivered.
- **Why it hits order events:** order events use `AggregateId` = the marketplace order id, and sibling seller orders share it (M35). Sibling orders also block each other through the check at :211.
- **Repro:**
  1. Create one marketplace order with sellers A and B, then dispatch.
  2. B accepts and holds, then dispatch.
  3. A accepts, then dispatch.
  4. A's `order.accepted` (k=1) is marked Dead "superseded by a newer version" and is never sent.
- **Second symptom:** the same rule also kills a re-queued Dead event of a single order. That contradicts ORDERS.md ("never dropped as superseded").
- **Fix:** apply the superseded rule only to unscoped (listing) events. At :209, use `if (candidate.SequenceScope is null && candidate.OrderKey is { } key)`. Make sure blocking and superseding for scoped events use only SequenceScope, never AggregateId.

## Also
- Add a unique index on (SequenceScope, OrderKey) where SequenceScope is not null, so concurrent writers of one scope can't allocate the same number. Make it part of this PR's single migration, if a migration is needed.

## Tests (each must fail when its fix is reverted)
- the sibling repro: both A's and B's events are delivered, in order per seller order;
- a re-queued Dead event of a seller order is delivered, not superseded;
- listing events are still superseded as before;
- the unique index rejects a duplicate (scope, key).

## Rules
- ONE new branch, `fix/outbox-superseded-scope`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.
- This PR goes FIRST, ahead of #66 and your other briefs. Rebase the others afterwards.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with the merge commit.
