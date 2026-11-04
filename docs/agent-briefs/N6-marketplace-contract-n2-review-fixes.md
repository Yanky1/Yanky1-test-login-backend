You are worker N (marketplace contract), continuing on the Solvelt seller portal backend: https://github.com/Yanky1/Yanky1-test-login-backend. This brief is an approved assignment (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

Context: the orchestrator's verification of PR #40 (C5/C6, main 75470d6) passed overall: 1031/1031 tests, and the key guards are mutation-pinned. It found one bug and four coverage gaps. You may fold this into whichever of the N3, N4 or N5 PRs is still open. Otherwise use ONE new branch, `feature/marketplace-contract-n2-review`, cut from the current origin/main, with one PR. Rebase-merge it once CI is green on that exact commit.

## Bug (medium): the failed-request bucket doesn't bound any server work
- **Where:** `CatalogEndpoints.cs` ~137 and `Moderation.cs:390` call `VerifyAsync` before the per-IP `Rejected` bucket is charged. `VerifyAsync` (`Integration.cs:52-58`) writes an `AuditEvents` row for every rejected request, and nothing deletes them. C6 removed the pre-handler per-IP limiter, so an unsigned flood now causes one DB insert per request; the bucket only turns the 401 into a 429.
- **Repro:** send 200 unsigned POSTs to `/products/{id}/questions` with the bucket set to 4. You get 196 responses of 429 and 200 audit rows.
- **Fix:**
  - When the caller is already over the failed-request bucket, skip the audit insert; for example, check or charge the bucket before auditing, and pass an `audit` flag into `VerifyAsync`.
  - Rate-limit or sample the `inbound.rejected` audit rows, at most N per IP per minute, and give them a retention period in the existing retention job.
  - Apply the same fix to the availability endpoints that use the same pattern.
  - Keep M18's property: signed requests that share an IP with garbage are never refused by the IP bucket.
- **Test:** count the audit rows during a flood: they stay bounded, and the flood still returns 429. Revert the fix and confirm the test fails.

## Coverage gaps (add tests; each must fail when its guard is reverted)
1. The per-caller ceiling for REPORTS. Removing it today passes every test.
2. localDelivery: a brand's own ENABLED setting with no active zone must win over an enabled account-wide setting with a zone, giving `false`. This is the `SettingFor` order.
3. A service never `ships`, even when the account has a shipping profile covering its brand.
4. Processed `catalog_refresh_requests` rows are deleted after 7 days (`CatalogRefreshJob.cs:116-117`).

Also update DECISIONS M10, which still names the removed `RateLimit:InternalQuestionsPerMinute` key: mark it as superseded by M18.

## Rules
- Make one new EF migration, and only if the schema changes.
- Never skip, delete or weaken a test.
- Never rewrite a pushed branch.
- Don't touch other repositories.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) with the merge commit.
