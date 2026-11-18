You are worker W (quantity offers: price tiers and free gifts, contract C18) on the Solvelt seller portal. This brief is an approved assignment from the repository owner (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend), and keep going until the work is built, tested, green, merged and pushed.

Source: issue Yanky1/Yanky1-test-login-backend#101. Requested by Ronald, the repository owner, on 2026-11-18.

## Repos
- **Backend** (your checkout): https://github.com/Yanky1/Yanky1-test-login-backend.
  - Install the .NET SDK with `apt-get update && apt-get install -y dotnet-sdk-10.0` from Ubuntu's archive; the Microsoft hosts are blocked.
- **Frontend:** https://github.com/Yanky1/test-login-frontend. Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push").
- **Read first:** docs/AGENTS.md, docs/CATALOG.md (the catalog feed), docs/CONTRACT.md and docs/ORDERS.md (order intake, money rules, signed internal API, outbox ordering C14), docs/PAYMENTS.md (fees, refunds), and the O demo-data section.
- **The buyer marketplace is NOT yours.** Its repos are solvelt-marketplace-api and solvelt-marketplace-web. The buyer orchestrator applies the offers there from your contract. Write the contract so it can be implemented without asking you.

## Owner decisions
- **Tiers are a per-unit price by minimum quantity,** counted across all eligible products of ONE seller in one order, mixing designs. Example: "from 20 cards, $0.90 each". So 20 cards cost $18.00 and 25 cards cost 25 × $0.90 = $22.50. There are several steps per offer; the highest step reached applies to every eligible unit.
- **Free gift at a minimum quantity:** from 10 eligible units, one or more gift products of the same seller are added at $0, for example a wallet and a pack. The seller ships them, they appear on the order and packing slip, and they count against the gift product's stock.

## Your defaults (record each in DECISIONS; Ronald may change them later)
- **A gift is once per order** and does not stack. A per-gift `stackEvery` option can come later.
- **A tier never raises a price.** Each line's unit price is min(its current price including any sale price, the tier price). A tier and a sale don't stack beyond that minimum.
- **The gift is out of stock:** the order still goes through without it. The cart and checkout say "free gift unavailable", the order records `giftUnavailable`, and the seller sees it.
- **Money:** integer cents. Discount per line = (price − tier price) × quantity, written into `OrderLine.DiscountCents`. Fees, net, tax base and refunds use the discounted amounts, through the existing paths in `Orders/OrderSupport.cs`.
- **One active offer per scope.** If several of a seller's offers could apply to one line, the most specific scope wins (product, then category, then all), and offers do not stack.

## A. The offer model and the seller API
- **`Offer`:** seller id, brand scope like products, name, scope (`all` | `category` + ids | `products` + ids), `tiers` [{minQuantity, unitPriceCents}], `gifts` [{minQuantity, productId, quantity}], active from/to, status (draft / active / ended).
- **Versioned:** every edit creates a new immutable `OfferVersion`, so orders refer to the exact version they were priced with.
- **Validation:**
  - tiers have strictly increasing minQuantity and decreasing unitPriceCents, and a unit price > 0;
  - gift products belong to the same seller and are active, physical products;
  - at most 10 tiers and 5 gifts.
- **Endpoints** under `/api/v1/seller/offers`: list, get, create, update (creates a new version), end. Owner, Admin and Manager can write; Staff can read. Brand scope and IDOR-safe like products; audit-log every write.
- **Demo:** the O demo fill seeds one offer (tiers, plus a gift product) on demo products, isolated like the other demo data.

## B. Contract C18 with the marketplace (signed, same rules as the other internal calls)
1. **Catalog feed:** expose the active offer versions per seller and product, with the id, version, tiers, gifts and scope, as resolved product ids. The marketplace can then show "20 for $18" and "free wallet from 10" and price the cart.
   - A change to an offer emits an outbox event (`offer.updated` / `offer.ended`, SequenceScope `offer:{offerId}`), or follows whatever pattern the catalog feed already uses for product changes.
2. **Order intake:**
   - **Discounted lines:** an order may carry `offerId` + `offerVersion`, per-line `discountCents`, and gift lines (`isGift: true`, price 0, `giftOfOfferId`), plus `giftUnavailable` when the gift was out of stock.
   - **The portal recomputes from that offer version and REJECTS a mismatch** with 422 `offer_mismatch`, carrying the expected figures. This covers wrong discounts, a gift not earned, other sellers' units counted, and a version that is not active at the order's placed-at time, with a short grace (e.g. 15 minutes) so carts priced just before an edit still go through.
   - **Gift stock** is reserved like normal lines. If it is out of stock at intake, accept the order with `giftUnavailable`, not reject it.
3. **Refunds and fees:** partial refunds and the platform fee use the discounted line amounts.
   - A gift line refunds $0.
   - Refunding units below a tier threshold does NOT re-price the remaining units after the fact: the buyer keeps the price paid. Record this rule clearly in the contract.
4. **Docs:** document C18 with JSON examples in CATALOG.md, CONTRACT.md and ORDERS.md. Add a "Marketplace side" checklist the buyer orchestrator can hand to its workers:
   - pricing the cart;
   - checkout;
   - the listing badge;
   - the cart message;
   - the voice assistant quoting the same totals.

## C. Seller portal UI
- An **"Offers" page** in the seller nav: the list, plus create/edit with a tier table (minimum quantity → price each), a gift picker from the seller's products, scope, dates, and end-offer with a confirm dialog.
- A **live preview:** "10 cards: $10.00 + free Wallet. 20 cards: $18.00. 25 cards: $22.50", computed by a server preview endpoint; no client-side money maths.
- **Orders:** gift lines show a "Free gift" tag on the order detail and the packing slip, and `giftUnavailable` shows a notice. Discounts show per line.
- Design tokens only, WCAG 2.2 AA, axe clean in light and dark, keyboard operable, and a "Demo" pill on demo offers.

## Tests (each guard must fail when its line is reverted; check that and say so in the PR)
- The issue's acceptance list:
  - 20 mixed cards from one seller total $18.00;
  - 25 total $22.50;
  - 10 to 19 cards include a $0 gift line the seller sees;
  - other sellers' cards don't count;
  - partial refunds and the fee use the discounted amounts.
- **Validation:** the tier rules, a gift from another seller is rejected, the limits.
- **Intake:** a correct order is accepted; each mismatch kind gives 422 `offer_mismatch`; the version grace window; gift out of stock gives `giftUnavailable`; gift stock is decremented.
- **A tier never raises a price;** a sale price below the tier wins.
- **Seller API:** the role matrix, IDOR, the audit log, and versioning (an order keeps its old version after an edit).
- **Feed and events:** shapes and ordering.
- **Frontend:**
  - Vitest for the Offers page, the tier editor, the preview and the confirm;
  - Playwright E2E against the real backend: create an offer, see the preview, place a signed test order with a gift and a tier discount, and see it on the order and the packing slip;
  - axe in light and dark.
- Never skip, delete or weaken a test.

## Merging
- Branch `feature/quantity-offers-c18` in both repos. At most ONE backend migration.
- The backend merge queue is one PR in CI at a time: check with `gh api` (REST) first.
- Rebase onto origin/main. Merge only when CI (plus E2E for the frontend) is green on that exact commit.
- Rebase-merge or fast-forward only: no merge commits, no force-push to main, and never rewrite a pushed branch (use a new branch name).
- Don't touch any other repository. Add a comment on issue #101 with the merge commits when done (no secrets, demo data only).
- Docs:
  - DECISIONS W1..Wn;
  - AGENTS.md: a row "W. Quantity offers (C18)";
  - add this brief verbatim as docs/agent-briefs/W-quantity-offers.md;
  - frontend DESIGN.md, plus screenshots (desktop and mobile) in docs/screenshots/offers/.

## Keep yourself running
Schedule send_later check-ins every 2 hours. If a usage limit stops you, resume 3 minutes after the reset.

## Finish
Message the seller-portal orchestrator (session_018hZb8rLz5RZyEUFB3R9JnX) and the buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) with:
- the merge commits;
- the final C18 shapes (the feed, the events, the order intake and the 422 body);
- the test counts and where the screenshots are.
