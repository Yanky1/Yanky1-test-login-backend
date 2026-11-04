You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

**Context.** The orchestrator verified PR #62 (C7–C9, main 3ed6c8b). 500 tests pass, every brief item is mutation-pinned, the ledger has no tax, and the worked example passes. The review found the issues below. Fold this brief into whichever of your PRs is still open (N10 is a good fit). Otherwise use ONE new branch, `feature/marketplace-contract-n4n5-review`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.

## 1 (medium): shipping amount and tax overflow
- **Cause:** each `shipping[]` entry is capped at MaxCents (Ingest.cs:77-78), but the group sum (:92) isn't capped, and the number of entries has no limit. OrderSupport.cs:59 then multiplies `ShippingTaxCents * shipping` in a long.
- **Repro:** 10 entries of amount 1e9 and tax 1e9 gives 201. A refund preview for shipping 5e9 then returns `shippingTaxCents` −534023222.
- **Fix:**
  - return 422 when a group's summed amount or tax exceeds MaxCents;
  - cap the number of `shipping[]` entries, e.g. at the number of lines + groups, or 50;
  - compute the proration with Int128 or checked arithmetic. Check every `x * y / z` proration in OrderSupport and OrderActions.
- **Tests:** the repro gives 422, and a large but valid proration gives no overflow.

## 2 (medium on PostgreSQL): shipping method length mismatch
- **Cause:** CleanMethod allows 80 chars (Ingest.cs:42), but the column is varchar(64) (OrderModel.cs:26). On PostgreSQL a 65–80 char method causes a DbUpdateException, which is rethrown (Ingest.cs:209-217), so the request returns 500. SQLite doesn't enforce the length.
- **Fix:** make the validation and the column agree. Either cap at 64, or widen the column to 80 in a migration, and update ORDERS.md to match.
- **Test:** an 81-char method gives 422; the maximum length is accepted. If possible, run the opt-in PG test project.

## 3 (low): line amount and tax are floored separately (OrderActions.cs:516-522)
- **Repro:** qty 3 at 1 cent, discount 2, tax 2, refunded one unit at a time. Items excluding tax come out 1, 1, −1, so refund.processed returns 200, then 422 `exceeds_sale`, then 422.
- **Fix:** prorate the tax, then the items excluding tax (unit × qty − discount), each with floor plus the last-refund-takes-remainder rule, and add them up for the amount. Neither part may ever go negative.
- **Test:** the repro. Every step's `amountCents − taxCents − shippingCents` must be accepted by refund.processed.

## 4 (low): legacy refund rows report earlier tax again
The OrderShippingTax migration leaves pre-migration refund rows with a tax share of 0. The last post-migration refund of a line then reports the earlier refund's tax as well. For example, qty 2 with tax 100 and one legacy refund: the next event shows taxCents 100 where it should be 50.
- **Fix:** in the migration (or a new data migration), backfill `order_refund_lines.TaxCents = floor(line.TaxCents × qty / line.Quantity)` for existing rows, and give shipping tax 0. Update DECISIONS M33.
- **Test:** a row with a zeroed share, then the next refund → 50.

## Test gaps (each must fail when its line is reverted)
- The packing slip shows the group's speed (OrderEndpoints.cs:395). M24 survived.
- A remainder cancel after an earlier partial refund of the same line uses the remainder rule, not proration (OrderActions.cs:380). M21 survived.

## Rules
- Add at most one migration.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

**Finish:** message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with the merge commit.
