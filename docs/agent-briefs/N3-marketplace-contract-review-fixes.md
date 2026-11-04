You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

Context: the orchestrator's review of PR #33 (C1/C2 on main) found the issues below. Each one was reproduced on main. Fix them on a NEW branch, `feature/marketplace-contract-review-1`, cut from the current origin/main, with ONE PR. Rebase-merge it once CI is green on that exact commit. If C5/C6 (brief N2) hasn't merged yet, finish and merge it first; then do this brief.

## Bugs
1. **Deleted listings vanish from the feed** (ProductEndpoints.cs:340-344, CatalogTracking.cs:212).
   - Delete removes the row and its tombstone.
   - Repro:
     1. Read the feed, keeping cursor C.
     2. Deactivate the listing.
     3. DELETE it.
     4. Read the feed from C. No tombstone is returned.
   - Fix: persist a tombstone for any listing that was ever listed (a `catalog_tombstones` row with a new version and reason `deleted`, or a soft delete) inside the tracker, and return it in the feed. Add `deleted` to the unlisted reasons and docs.
   - Then restore the counter test's `Equal` assertion, which was loosened to `>=` in #33 because of this bug.
2. **`content.report_decided` is emitted once per concurrent decision** (Moderation.cs:187-191, 33-35).
   - Repro: 6 moderators dismiss the same report in parallel. All get 200, and 6 events are emitted.
   - Fix:
     - Claim the report with `ExecuteUpdate(... WHERE Id=@id AND Status='Open')`, and continue only if 1 row changed; otherwise return 409 `already_decided`. A concurrency token is also acceptable.
     - Mark ContentReports decided with `WHERE Decision IS NULL`, and enqueue events only for the rows that update affected.
   - Add a parallel test.
3. **A report on an already-hidden listing can't be decided as hidden** (Moderation.cs:233).
   - Repro:
     1. Hide the listing.
     2. Report it.
     3. Call `hide-content`. It returns 409 `already_hidden`.
   - Fix: when it is already moderated, decide the report as `hidden` without re-hiding.
4. **Visibility race** (CatalogTracking.cs:193). The visibility check runs before the counter lock and isn't repeated under it, so a child-row edit (variant/image) racing with activation can miss a version bump.
   - Fix: re-evaluate visibility after acquiring the lock, or make child-row edits touch the product row.
   - Add a test where possible; otherwise document it.

## Gaps
5. **searchTerms:** cap the count (e.g. 20) and each term's length (e.g. 50). Strip control characters, trim, dedupe case-insensitively, and reject email-address and phone-number patterns. Check the CSV template example: space-separated keywords become one term, so either document the separator or split on commas and semicolons.
6. **Image endpoint:** remove the disabled-variant filter at CatalogEndpoints.cs:116; currently no test covers it.
7. **Image signing:** image requests reuse the inbound HMAC key. Add a separate key purpose, either a derived subkey or a config key like `Shipping:LinkKey`, keep backward compatibility, and document it.
8. **Retention:** `content_reports.Note`, `ReporterRef` and `Reason` have no retention or redaction. Add them to the existing retention/redaction job, with a test.
9. **Cross-seller id matching:** message reports match `MarketplaceMessageId` across all sellers (Moderation.cs:84), but the index is only unique per (SellerId, id). Require the target to resolve to exactly one row, or include the seller in the report contract (additively), and treat an ambiguous match as 422. Message intake does the same global match, so fix it consistently.
10. **Docs (C4):** ORDERS.md:156 and :161 and CATALOG.md:116 still say to dedupe on `X-Solvelt-Event-Id`. Make them say: the body `id` for events, and `version` for listings.
11. **Counter lock:** removing the row lock survives every test, because SQLite can't show the race. Add an opt-in PostgreSQL category test (it isn't counted as skipped) or a test that asserts the lock SQL is issued.

## Rules
- Make one new EF migration, only if the schema changes.
- Never skip, delete or weaken a test. Revert each fix and confirm its test fails.
- Don't touch any other repository.
- Never rewrite a pushed branch.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commit and any shape change (e.g. the `deleted` reason).
