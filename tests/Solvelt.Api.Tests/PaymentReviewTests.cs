using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

/// <summary>The product defaults: no overrides, so the return window applies (30 days from delivery).</summary>
public class StrictFactory : PayFactory
{
    public StrictFactory() { Extra.Remove("Payments:RequireReturnWindowClosed"); }
}

/// <summary>Regression tests for the orchestrator's review of the payments backend.</summary>
public class PaymentReviewTests
{
    private static List<PaymentTransfer> Transfers(ApiFactory f, Guid seller) => f.WithDb(db => db.PaymentTransfers.AsNoTracking().Where(t => t.SellerId == seller).OrderBy(t => t.CreatedAt).ToList());

    // 1 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_partial_refund_after_a_received_return_moves_the_order_to_Refunded_and_the_kept_items_still_release()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (mid, id, number, _) = await Orders.Place(s, qty: 3, price: 2000, shipping: 400, lid: "RL1");
        await Orders.Ok(Orders.Act(s.Owner, id, "accept")); await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship())); await Orders.Ok(Orders.Act(s.Owner, id, "deliver"));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } })).StatusCode);
        var rid = (string)(await Orders.Detail(s.Owner, id))["returns"]![0]!["id"]!;
        await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/approve", new { note = "send it back" }));
        await Orders.Ok(Orders.Act(s.Owner, id, $"returns/{rid}/receive", new { restock = true }));
        var line = await Orders.LineId(s.Owner, id);
        var refund = await Orders.Ok(Orders.Act(s.Owner, id, "refunds", new { reason = "returned_item", returnId = rid, lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal("Refunded", Orders.St(refund));                                             // the order is Refunded although only one of three units was refunded
        var refunded = (long)refund["money"]!["refundedCents"]!;
        Assert.True(refunded < (long)refund["money"]!["totalCents"]!);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, mid, number, refunded)).StatusCode);

        var pending = (await Pay.Bal(f, s.Seller)).Pending;
        Assert.True(pending > 0);
        Pay.Age(f, 60);
        Assert.Equal(1, (await Pay.Release(f)).Released);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, pending), (b.Pending, b.Available));
    }

    // 2 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_stripe_inquiry_moves_no_money_and_closing_it_changes_nothing()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "pi_inq");
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", new { id = "dp_inq", payment_intent = "pi_inq", amount = 2499, currency = "usd", status = "warning_needs_response" }, created: now)).StatusCode);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal("inquiry", f.WithDb(db => db.PaymentDisputes.Single().Status));
        Assert.Empty(Pay.Journals(f, s.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s.Seller, "dispute_fee"));
        Assert.DoesNotContain(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("dispute"));
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_inq", payment_intent = "pi_inq", amount = 2499, currency = "usd", status = "warning_under_review" }, created: now + 5);
        await Pay.Hook(f, "charge.dispute.closed", new { id = "dp_inq", payment_intent = "pi_inq", amount = 2499, currency = "usd", status = "warning_closed" }, created: now + 10);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal("warning_closed", f.WithDb(db => db.PaymentDisputes.Single().Status));
        Assert.Equal(0, Pay.Sum(f, s.Seller, LedgerAccount.Platform) + 2499);   // only the sale touched the ledger (platform side -2499)
    }

    [Fact]
    public async Task An_inquiry_that_escalates_is_debited_exactly_once_and_a_marketplace_warning_closed_moves_nothing()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "pi_esc");
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        await Pay.Hook(f, "charge.dispute.created", new { id = "dp_esc", payment_intent = "pi_esc", amount = 2499, currency = "usd", status = "warning_needs_response" }, created: now);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
        await Pay.Hook(f, "charge.dispute.created", new { id = "dp_esc", payment_intent = "pi_esc", amount = 2499, currency = "usd", status = "needs_response" }, created: now + 10);
        Assert.Equal(-2499 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_esc", payment_intent = "pi_esc", amount = 2499, currency = "usd", status = "under_review" }, created: now + 20);
        await Pay.Event(f, "dispute.updated", new { disputeId = "dp_esc", status = "needs_response" });
        Assert.Equal(-2499 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Single(Pay.Journals(f, s.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.Seller, "dispute_fee"));
        Assert.Contains(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("dispute"));

        // the marketplace reports an inquiry and its closing: still no money
        var o2 = await Pay.Paid(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_mk", marketplaceOrderId = o2.Mid, sellerOrderNumber = o2.Number, amountCents = 500L, status = "inquiry" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.closed", new { disputeId = "dp_mk", status = "warning_closed" })).StatusCode);
        Assert.Equal(-2499 - 1500, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal(2, f.WithDb(db => db.PaymentDisputes.Count()));
    }

    // 3 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task<(Shop S, PaymentTransfer T)> Sent(ApiFactory f)
    {
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f); await Pay.Transfers(f);
        return (s, Transfers(f, s.Seller)[0]);
    }

    [Fact]
    public async Task A_partial_transfer_reversal_credits_only_the_reversed_part_and_keeps_the_transfer_open()
    {
        using var f = new PayFactory();
        var (s, t) = await Sent(f);
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        object Obj(long reversed, bool full = false) => new { id = t.StripeTransferId, amount = t.AmountCents, amount_reversed = reversed, reversed = full, metadata = new { transferId = t.Id.ToString() } };
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "transfer.reversed", Obj(100), created: now)).StatusCode);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((100, 2299 - 100), (b.Available, b.Settled));
        Assert.Equal(TransferStatus.Created, Transfers(f, s.Seller)[0].Status);
        // the same total again (another delivery of the state) changes nothing; a bigger total credits only the difference
        await Pay.Hook(f, "transfer.updated", Obj(100), created: now + 1);
        Assert.Equal(100, (await Pay.Bal(f, s.Seller)).Available);
        await Pay.Hook(f, "transfer.reversed", Obj(300), created: now + 2);
        Assert.Equal(300, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal(TransferStatus.Created, Transfers(f, s.Seller)[0].Status);
        // a smaller (older) total can not take money back
        await Pay.Hook(f, "transfer.reversed", Obj(200), created: now + 3);
        Assert.Equal(300, (await Pay.Bal(f, s.Seller)).Available);
        // the final reversal of the rest closes the transfer; the seller got exactly the transfer amount back, once
        await Pay.Hook(f, "transfer.reversed", Obj(2299, full: true), created: now + 4);
        b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, 0), (b.Available, b.Settled));
        Assert.Equal(TransferStatus.Reversed, Transfers(f, s.Seller)[0].Status);
    }

    [Fact]
    public async Task A_full_reversal_without_an_amount_still_returns_the_whole_transfer_and_reconciliation_checks_the_reversed_amount()
    {
        using var f = new PayFactory();
        var (s, t) = await Sent(f);
        await Pay.Hook(f, "transfer.reversed", new { id = t.StripeTransferId, reversed = true });
        Assert.Equal((2299, 0), ((await Pay.Bal(f, s.Seller)).Available, (await Pay.Bal(f, s.Seller)).Settled));
        f.Fake.Transfers[t.StripeTransferId!] = f.Fake.Transfers[t.StripeTransferId!] with { Status = "reversed", ReversedCents = 2299 };
        Assert.Equal(0, (await Pay.Reconcile(f)).Flagged);                              // Stripe and ledger agree
        f.Fake.Transfers[t.StripeTransferId!] = f.Fake.Transfers[t.StripeTransferId!] with { ReversedCents = 50 };
        Assert.Equal(1, (await Pay.Reconcile(f)).Flagged);                              // Stripe says 50 reversed, the ledger returned 2299
    }

    // 4 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Staff_retry_is_atomic_and_serialised_with_the_daily_batch()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 8); await Pay.Release(f);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true;
        await Pay.Transfers(f);
        var t = Transfers(f, s.Seller)[0];
        Assert.Equal(TransferStatus.Failed, t.Status);

        // the batch job holds the lease: a retry must not run next to it
        var token = await Pay.Lease(f);
        Assert.NotNull(token);
        Assert.Equal((false, "busy"), await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id)));
        Assert.Equal(TransferStatus.Failed, Transfers(f, s.Seller)[0].Status);
        await Pay.Unlease(f, token!);

        // the debit journal can not be written (its key is taken): the status change must roll back with it, never a Pending transfer without its debit
        f.WithDb(db => { db.LedgerJournals.Add(new LedgerJournal { SellerId = s.Seller, Currency = "USD", Type = "adjustment", IdempotencyKey = $"transfer-retry:{t.Id}:1", Memo = "squatter", CreatedAt = f.Time.GetUtcNow().UtcDateTime,
            Entries = { new LedgerEntry { SellerId = s.Seller, Currency = "USD", Account = LedgerAccount.Platform, AmountCents = 0, Kind = "x", CreatedAt = f.Time.GetUtcNow().UtcDateTime } } }); db.SaveChanges(); return 0; });
        f.Fake.FailPermanently = false;
        var r = await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id));
        Assert.Equal((false, "conflict"), r);
        var after = Transfers(f, s.Seller)[0];
        Assert.Equal((TransferStatus.Failed, 0), (after.Status, after.Retries));
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Available);                      // the money is still where it was
    }

    // 5 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_unique_conflict_that_is_not_this_event_is_a_retryable_409_not_an_acknowledged_duplicate()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        // something else already owns (eventId, ref): saving this capture hits a unique constraint that is NOT the processed-event key
        f.WithDb(db => { db.PaymentReferences.Add(new PaymentReference { EventId = "ev-race", Ref = "ch_race", MarketplaceOrderId = o.Mid, CapturedAt = f.Time.GetUtcNow().UtcDateTime }); db.SaveChanges(); return 0; });
        var r = await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-race", paymentRef: "ch_race");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("concurrent_update", await Orders.Code(r));
        Assert.Empty(Pay.Journals(f, s.Seller));
        Assert.Equal(0, f.WithDb(db => db.ProcessedPaymentEvents.Count()));              // not recorded: the sender's retry is processed normally
        // a genuine second delivery of an applied event is still a duplicate
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-ok")).StatusCode);
        Assert.Equal("duplicate", (string)(await Pay.Json(await Pay.Capture(f, o.Mid, o.Number, eventId: "ev-ok")))["status"]!);
    }

    // 6 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_refund_committing_between_the_release_scan_and_the_release_is_part_of_the_released_amount()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Pay.Age(f, 8);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        hooks.AfterReleaseCandidate = async _ => { hooks.AfterReleaseCandidate = null; Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 1000)).StatusCode); };
        await Pay.Release(f);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal(0, b.Pending);                                                        // never negative
        Assert.Equal(2299 - 900, b.Available);                                             // 1000 refunded minus 100 fee returned
    }

    // spec ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task By_default_funds_also_wait_for_the_return_window()
    {
        Assert.True(new PaymentOptions().RequireReturnWindowClosed);
        using var f = new StrictFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s);
        Pay.Age(f, 10);
        Assert.Equal(0, (await Pay.Release(f)).Released);        // 7 days passed, the 30-day return window has not
        Pay.Age(f, 21);
        Assert.Equal(1, (await Pay.Release(f)).Released);
    }
}
