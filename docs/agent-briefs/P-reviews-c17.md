You are worker P (reviews and seller replies, contract C17) on the Solvelt seller portal. This brief is an approved assignment from the repository owner (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until the work is built, tested, green, merged and pushed. The account is near its weekly usage limit: work efficiently, run filtered tests, and run the full suite once before you push.

## Repos
- Backend (your checkout): https://github.com/Yanky1/Yanky1-test-login-backend. Install the .NET SDK with `apt-get update && apt-get install -y dotnet-sdk-10.0` from Ubuntu's archive. The Microsoft hosts are blocked.
- Frontend: https://github.com/Yanky1/test-login-frontend. Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push").
- Read docs/AGENTS.md, docs/ORDERS.md (signed internal API, outbox ordering C14, money/PII rules), docs/CATALOG.md (C1 questions, C2 content reports), docs/ADMIN.md (moderation, staff matrix) and the O demo-data section (demo records must stay isolated).

## C17 (requested by the buyer-marketplace orchestrator; accepted with the adjustments below)

### A. Marketplace → portal (signed; same signing, stale and replay rules and per-caller rate limit as the other internal calls)
1. `POST /api/internal/v1/reviews` with `{reviewId, listingId, sellerId, rating 1-5, text, buyerDisplayName, createdAt, editedAt?}`.
   - Idempotent by reviewId: the same body gives 200 duplicate. A newer `editedAt` updates the review. An older or equal one is ignored (200, unchanged).
   - Validate the following. Anything else gives 422.
     - `listingId` must be one of that seller's products, and `sellerId` must match.
     - `rating` is an integer from 1 to 5.
     - `text` is at most 5000 chars.
     - `buyerDisplayName` is at most 60 chars. Store exactly the display name the marketplace sends and nothing else about the buyer: no photos, no buyer id.
2. `POST /api/internal/v1/reviews/{reviewId}/state` with `{state: visible|hidden|removed, reason?}`.
   - `hidden` and `removed` take the review out of the seller's normal list. Keep a "Hidden by marketplace" view for the Owner and Admin.
   - Replying is blocked (409 `review_not_visible`).
   - `removed` also clears the text and the display name (redaction) and removes the reply, which emits `review.reply_removed`.
   - An unknown reviewId gives 404.
3. Demo: reviews for demo products are accepted normally. Extend the O demo fill to seed 3 reviews (one 5-star with a reply, one 3-star, one 1-star) through the same service. Mark them in the demo registry. Their replies send no outbox events.

### B. Seller side (portal API and UI)
- Endpoints:
  - `GET /api/v1/seller/reviews`, with filters for rating, replied/unreplied and product, and paging;
  - `PUT /api/v1/seller/reviews/{id}/reply` with `{text}` (≤ 2000 chars), which creates or edits the reply;
  - `DELETE /api/v1/seller/reviews/{id}/reply`.
- Roles: Owner, Admin and Manager can reply, and Staff can read, within their brand scope like products. Make it IDOR-safe and rate-limited (e.g. 30 replies per hour per account), and audit-log every reply.
- One reply per review (replyId stable across edits).
- Contact details: reject replies that contain an email address, a phone number or an off-platform URL, using the same detector the messaging module uses, with 422 `contact_details_not_allowed`.
- Frontend:
  - a "Reviews" page in the seller nav, with average rating, counts per star and filters;
  - a review card with stars, the text, the display name, the product and the date;
  - an inline reply editor with a character counter, and delete with a confirm dialog;
  - a "Demo" pill for demo reviews (O's pattern);
  - an empty state.
- Use design tokens only. WCAG 2.2 AA, axe clean, keyboard-operable stars (read-only), and the rating announced as text ("4 out of 5 stars").

### C. Portal → marketplace webhooks (outbox; same signing, dedupe and C14 ordering; SequenceScope `review:{reviewId}`)
- `review.reply_posted {reviewId, replyId, text, repliedAt, editedAt?}`, sent on create and on every edit. The receiver keeps the latest by sequence.
- `review.reply_removed {reviewId, replyId, removedAt, reason: seller_deleted|review_removed|staff_removed}`.

### D. Moderation
- The marketplace can report a reply through the existing C2 content-report intake with `kind: "review_reply"` and `targetId = replyId`. It lands in the admin moderation queue under the existing staff matrix.
- Staff actions: dismiss, or remove the reply (sends `review.reply_removed` with reason `staff_removed` and emails the seller through the normal mail path). Use the per-report claim from N10 so that parallel decisions give exactly one winner.
- No new staff UI is required beyond the existing queue rendering the new kind. Show the reply text and the review context.

## Docs
- REVIEWS.md (new): the model, endpoints, events with examples, moderation and retention.
- ORDERS.md / CATALOG.md: cross-links and the signed API table rows.
- DECISIONS P1..Pn.
- AGENTS.md: a row "P. Reviews and seller replies (C17)".
- Frontend DESIGN.md, plus screenshots (desktop and mobile) in docs/screenshots/reviews/.

## Tests (each guard must fail when its line is reverted; check that and say so in the PR description)
- Signed, stale, replay and rate limit on both internal endpoints; 404 and 422 cases; idempotent upsert; an older `editedAt` is ignored.
- `listingId` or `sellerId` mismatch gives 422 (no cross-seller injection).
- The role matrix and IDOR for the seller endpoints; contact details give 422; the reply rate limit.
- Hidden and removed reviews block replies; removed redacts, removes the reply and sends the event.
- Event order: post, edit, delete on one review share a scope and have increasing OrderKeys. Demo replies send nothing.
- The moderation report of kind review_reply, a parallel dismiss plus remove gives exactly one winner, and the event and the email match the winner.
- No buyer PII beyond the display name anywhere: assert the JSON keys of the seller API and the events.
- Frontend: Vitest for the page, the filters, the reply editor and the delete confirm; Playwright E2E against the real backend: a signed review is ingested → it appears → reply → edit → delete, with axe in light and dark.
- Never skip, delete or weaken a test.

## Merging
- Branch `feature/reviews-c17` in both repos. At most ONE backend migration.
- The backend merge queue is one PR in CI at a time; check with `gh api` (REST) first.
- Rebase onto origin/main. Merge only when CI is green on that exact commit (plus E2E for the frontend). Rebase-merge or fast-forward only: no merge commits, no force-push to main, never rewrite a pushed branch (use a new branch name).
- Don't touch any other repository.

## Keep yourself running
Schedule send_later check-ins every 2 hours. If a usage limit stops you, resume 3 minutes after the reset.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with the merge commits, the final endpoint and event shapes, the test counts and where the screenshots are.
