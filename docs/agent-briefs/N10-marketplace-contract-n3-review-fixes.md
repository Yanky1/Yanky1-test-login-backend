You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

**Context.** The orchestrator verified PR #61 (N3, main 575b271): 208 tests pass, all 11 items are mutation-pinned, and all migrations apply on PG16. It found the issues below. You may fold this brief into whichever of your PRs is still open. Otherwise use ONE new branch, `feature/marketplace-contract-n3-review`, from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.

## A (medium): a concurrent dismiss and hide-content on a LISTING report both succeed
- **Where:** Moderation.cs:271-306 (`HideListingAsync`, reached from :310). `DecideContentAsync` checks `Status == Open` without a claim. `HideListingAsync` hides the listing even when its `ClaimAsync` for that report loses, because `change == true` skips the check at :296.
- **Repro** (fails 3 out of 3): one listing report, then 6 moderators in parallel (3 dismiss, 3 hide-content). Result: dismiss 200 and hide-content 200, the listing is hidden, the seller is emailed "listing hidden", and the single `content.report_decided` says `dismissed`.
- **Fix:** when `reportId` is set, `ClaimAsync(reportId…)` FIRST inside the transaction. If the claim fails, return `AlreadyResolved()` (rolled back) before touching the product.
- **Test:** a mixed-decision parallel test on a listing report. Exactly one decision wins, and the listing state, the email and the event all agree with the winner.

## B (low, idempotency regression): a retried product question returns 404 after the product is deactivated
- **Where:** Messaging.cs:105. The dedupe now runs after seller resolution, which requires an Active product.
- **Repro:** product message → 201; deactivate the product; same `messageId` again → 404 (it was 200 duplicate before #61).
- **Fix:** before resolving, look the `messageId` up across sellers. If exactly one row exists, return it as duplicate. Then resolve as now.
- **Test:** the repro.
- Also add a test for the message-receipt ambiguity check at Messaging.cs:160; it has none today.

## C (low): phone-regex false positives in searchTerms (SearchTerms.cs:16)
- **Wrongly rejected:** "fits 2019-2023 models", "2019 2020 2021", "100 200 300 ml set", "1.5 2.5 3.5 4.5 mm". Long UPC-like numbers ("UPC 036000291452") are rejected by documented intent; keep that, or allow numbers of 12–14 digits labelled UPC/EAN/GTIN, your choice (record it).
- **Fix:** only treat as a phone a run that starts with `+`, or that contains a phone-style separator pattern with at least 10 digits in total. Exclude year ranges (`(19|20)\d\d\s*[-–]\s*(19|20)\d\d`) and runs of space-separated numbers of 4 digits or fewer, and decimals.
- **Tests:** each false positive above is accepted. Real phones are still rejected: "+1 555 123 4567", "(555) 123-4567", "555-123-4567", "call 5551234567". Emails are still rejected.

## Minor
- A report redelivered after its free text was redacted gets 409 `report_id_conflict`, because Moderation.cs:70 compares `ReporterRef`. After redaction, treat a matching `reportId` as a duplicate (200, same result), not a conflict. Add a test.

## Rules
- Add a migration only if the schema changes.
- Never skip, delete or weaken a test. Revert each fix and confirm its test fails.
- Never rewrite a pushed branch.
- Don't touch other repositories.

**Finish:** message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with the merge commit.
