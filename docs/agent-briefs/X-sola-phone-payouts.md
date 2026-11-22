You are worker X (seller payouts, refunds and disputes for Sola-paid phone orders, contract C19) on the Solvelt seller portal. This brief is an approved assignment from the repository owner (see CLAUDE.md). Never end your turn to ask: decide, record each decision in docs/DECISIONS.md, and keep going until the work is built, tested, green, merged and pushed.

**Source:** Ronald's decision V-15 (2026-11-18, recorded in solvelt-marketplace-api docs/DECISIONS.md): "Payments: Sola for PHONE orders only. Website checkout and seller payouts stay on Stripe Connect." On the buyer side, real-money phone orders stay refused in production until this portal contract item exists (buyer docs/VOICE.md, "Why real money stays off").

## Repos
- **Backend** (your checkout): https://github.com/Yanky1/Yanky1-test-login-backend. Install the .NET SDK with `apt-get update && apt-get install -y dotnet-sdk-10.0` from Ubuntu's archive; the Microsoft hosts are blocked. Run tests with `dotnet test -p:NuGetAudit=false`.
- **Frontend:** https://github.com/Yanky1/test-login-frontend. Attach it with add_repo (owner "Yanky1", repo "test-login-frontend", access "push") only if you do part D.
- **Read first:**
  - docs/PAYMENTS.md, all of it: the ledger, journals, release, transfers, sections 6–9 on the inbound contract, disputes and reconciliation;
  - docs/CONTRACT.md (signed internal API, C18 as a model of a contract section);
  - docs/ORDERS.md ("Money in events");
  - docs/AGENTS.md.
- **The buyer marketplace is NOT yours** (solvelt-marketplace-api / -web). You may read it: `src/Solvelt.Marketplace.Api/Voice/SolaPayment.cs`, `PhoneOrder.cs`, `Payments/Refunds.cs`, `Outbox/PaymentEventOutbox.cs`, and docs/VOICE.md. Write C19 so the buyer orchestrator can implement its side without asking you.

## The problem
Today every buyer payment is a Stripe charge on the platform account. The portal's ledger counts that money as **Platform** and pays sellers with Stripe Connect transfers from the platform's Stripe balance.

A phone order paid through **Sola** (formerly Cardknox) settles into Solvelt's Sola merchant account, not Stripe. Sellers must still be paid through Stripe Connect, as today. So:
1. The ledger must know which provider holds the buyer's money.
2. Refunds and chargebacks for Sola payments come from Sola, not Stripe.
3. Solvelt must move money from the Sola settlement to the Stripe balance to cover transfers for Sola-paid sales (treasury).
4. Stripe's `charge.dispute.*` webhooks and `paymentRef` matching must never act on a Sola payment.

## Owner decisions (fixed)
- **Sola is for phone orders only.** Web checkout and seller payouts stay on Stripe Connect. No Sola payouts to sellers.
- **No live keys and no real money.** Sandbox or test mode only. The portal never calls Sola: it has no Sola keys and gets none. Everything about Sola reaches the portal as signed marketplace events.

## Your defaults (record each in DECISIONS; Ronald may change them later)
- **Provider on every money event:** `payment.captured`, `refund.processed` and `dispute.*` gain an optional `provider: "stripe" | "sola"`, default `"stripe"` when absent, so today's events are unchanged.
  - The `paymentRef` for Sola is Sola's transaction reference (`xRefNum`).
  - The dispute id for Sola is Sola's chargeback reference.
  - Store `provider` beside every stored payment reference and dispute row. References are unique per (provider, reference), never across providers.
- **Ledger:**
  - Add a counter account **PlatformSola**. Buyer money held at Sola is booked there.
  - A Sola sale debits PlatformSola, not Platform. A Sola refund or dispute credits PlatformSola. A Sola `dispute_won` debits it.
  - Seller-side entries, fees, release rules, reserve, negative-balance policy and transfers are **identical** to Stripe sales. The seller's money and timing don't depend on the provider.
  - Journals stay balanced. Idempotency keys stay unique; prefix new ones with `sola:` where a key could collide.
- **Treasury (funding the Stripe balance):**
  - Transfers to sellers still come from the platform's Stripe balance.
  - Add a staff journal **`provider_funding`** (Platform −x; PlatformSola +x, or the reverse to correct), recorded when Ronald moves money from Sola to Stripe. It takes an amount, a currency, a reference and a note; Admin/Finance permission; audit-logged; idempotent per request id.
  - Add an "unfunded Sola balance" figure to the reconciliation/staff payments view. It shows how much has been released or transferred to sellers from Sola sales but not yet funded, so Ronald knows how much to move.
  - **Default: transfers are NOT blocked by the unfunded figure,** because a seller's payout must not depend on Solvelt's treasury timing. Add a switch `Payments:Sola:HoldTransfersWhenUnfunded` (default false) for the owner. Document both behaviours.
- **Stripe webhooks never touch Sola rows.**
  - `charge.dispute.*` matching uses only Stripe-provider references.
  - A Sola reference can never be matched by a Stripe charge id, even if the strings collide.
  - Reconciliation jobs that compare against Stripe skip Sola rows.
- **Disputes for Sola payments:** only the marketplace `dispute.*` events, with `provider: "sola"`. The statuses and multi-seller rules are the same as section 6/7 (shares per seller order, `seller_order_required`, `exceeds_sale`, `currency_mismatch`). No Stripe evidence flow: the seller sees the dispute and its status, but "respond" is offered only for Stripe disputes. Record this; Sola chargeback evidence is handled by Solvelt staff outside the portal.
- **Bookings:** phone orders are product orders only, since services are not booked by phone (buyer VOICE.md). A `payment.captured` with `provider: "sola"` and a booking kind gets `422 provider_not_supported`.

## Deliverables
**A. Contract C19 in docs/CONTRACT.md,** additive and normative:
- the new fields, defaults and validation:
  - `provider` must be `stripe` or `sola`, else `422 invalid_provider`;
  - `paymentRef` is required, at most 100 characters;
  - examples for each event;
- error codes, and idempotency across providers;
- a **"C19.x Marketplace side: checklist for the buyer orchestrator"** section, listing what the buyer API must send:
  - `payment.captured` with `provider: "sola"` and `paymentRef = xRefNum` after a verified Sola approval;
  - `refund.processed` with `provider: "sola"` after a Sola refund (`cc:refund` / `cc:voidrefund`) succeeds;
  - `dispute.*` with `provider: "sola"` from Sola chargeback notifications or a staff tool;
  - what to do on each 422.
- Also update docs/PAYMENTS.md (ledger table, section 6, reconciliation, configuration, going-live checklist: Sola funding procedure) and docs/ORDERS.md if money-in-events text changes.

**B. Backend:**
- the model, a migration, ingest, ledger, staff funding endpoint, unfunded figure, the `HoldTransfersWhenUnfunded` switch, and Stripe-matching isolation;
- seller payments API: add `provider` to the sale/refund/dispute rows the seller sees, so the statement can say "Phone order (Sola)";
- the demo data (O) gets one Sola-paid demo order if that fits the existing demo seeding.

**C. Tests (all on the existing fakes, never Sola or Stripe live):**
- a Sola sale posts to PlatformSola, and the seller balances are identical to an equal Stripe sale;
- release and transfer are unchanged;
- a Sola refund before and after release;
- Sola disputes: single and multi-seller, won and lost, fee;
- the same event twice is idempotent;
- a `paymentRef` equal to a Stripe id under the other provider does not cross-match;
- a Stripe `charge.dispute.*` with a reference that exists only as Sola does nothing to the Sola row (raises the usual unattributed flag);
- missing provider means stripe, so all existing tests stay unchanged and green;
- invalid provider, and a booking with sola;
- `provider_funding` (permissions, idempotency, correction), the unfunded figure, and the hold switch both ways;
- the reconciliation job skips Sola rows for Stripe checks;
- the journal sum is 0 everywhere (the existing reconciliation).

**D. Frontend (small):**
- the seller payments statement and order money panel show the provider label for Sola rows;
- a staff payments view shows the unfunded Sola balance and a form to record a funding;
- the "respond" button is hidden for Sola disputes;
- E2E as the frontend's rules require (CI + E2E green).

## Done means
- **Merged:** backend (and frontend if D) merged to main per CLAUDE.md: a fast-forward with every workflow green on the exact commit, and no test skipped, deleted or weakened.
- **Reported to the portal orchestrator** (session_018hZb8rLz5RZyEUFB3R9JnX) with send_message:
  - the shas;
  - the C19 section anchor;
  - the migration name;
  - every owner decision left open for Ronald (at least: the treasury procedure, and the hold switch).
- **AGENTS.md row** updated.
