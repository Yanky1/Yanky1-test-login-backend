using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the third review round of the payments backend.</summary>
public class PaymentRound3Tests
{
    private static async Task<(string Mid, Guid Id, string Number, string Rid)> ShippedPaidWithReturn(Shop s)
    {
        var f = s.F;
        var (mid, id, number, _) = await Orders.Place(s, qty: 3, price: 2000, shipping: 400, lid: "RL1");
        await Orders.Ok(Orders.Act(s.Owner, id, "accept")); await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship()));   // shipped, never marked delivered
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL1", quantity = 1 } } })).StatusCode);
        var rid = (string)(await Orders.Detail(s.Owner, id))["returns"]![0]!["id"]!;
        return (mid, id, number, rid);
    }

    // P1 ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_order_refunded_after_a_return_without_any_delivery_mark_still_releases_the_kept_items()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await ShippedPaidWithReturn(s);
        await Orders.Ok(Orders.Act(s.Owner, o.Id, $"returns/{o.Rid}/approve", new { note = "send it back" }));
        await Orders.Ok(Orders.Act(s.Owner, o.Id, $"returns/{o.Rid}/receive", new { restock = true }));
        var line = await Orders.LineId(s.Owner, o.Id);
        var refund = await Orders.Ok(Orders.Act(s.Owner, o.Id, "refunds", new { reason = "returned_item", returnId = o.Rid, lines = new[] { new { lineId = line, quantity = 1 } } }));
        Assert.Equal("Refunded", Orders.St(refund));
        Assert.Null((await Orders.Detail(s.Owner, o.Id))["deliveredAt"]?.GetValue<DateTime?>());
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, (long)refund["money"]!["refundedCents"]!)).StatusCode);
        var pending = (await Pay.Bal(f, s.Seller)).Pending;
        Assert.True(pending > 0);
        Pay.Age(f, 365);
        Assert.Equal(1, (await Pay.Release(f)).Released);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, pending), (b.Pending, b.Available));
    }

    [Fact]
    public async Task An_order_whose_return_was_rejected_and_completed_without_delivery_releases_after_the_window()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await ShippedPaidWithReturn(s);
        await Orders.Ok(Orders.Act(s.Owner, o.Id, $"returns/{o.Rid}/reject", new { reason = "item_used", note = "worn" }));
        Pay.Age(f, 40);
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/maintenance/run", null)).StatusCode);   // ReturnRejected -> Completed after the return window
        Assert.Equal("Completed", f.WithDb(db => db.Orders.AsNoTracking().First(x => x.Id == o.Id).Status.ToString()));
        Assert.Null(f.WithDb(db => db.Orders.AsNoTracking().First(x => x.Id == o.Id).DeliveredAt));
        var pending = (await Pay.Bal(f, s.Seller)).Pending;
        Assert.True(pending > 0);
        Pay.Age(f, 40);
        Assert.Equal(1, (await Pay.Release(f)).Released);
        Assert.Equal((0, pending), ((await Pay.Bal(f, s.Seller)).Pending, (await Pay.Bal(f, s.Seller)).Available));
    }

    // P3 ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void PostgreSQL_serialization_failures_and_deadlocks_are_recognised_anywhere_in_the_exception_chain()
    {
        PostgresException Pg(string state) => new("x", "ERROR", "ERROR", state);
        Assert.True(DbErrors.IsSerialization(Pg("40001")));
        Assert.True(DbErrors.IsSerialization(Pg("40P01")));
        Assert.True(DbErrors.IsSerialization(new DbUpdateException("wrapped", Pg("40001"))));
        Assert.False(DbErrors.IsSerialization(Pg("23505")));
        Assert.False(DbErrors.IsSerialization(new InvalidOperationException()));
    }

    [Fact]
    public async Task The_money_paths_open_their_transactions_serializable()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        var o = await Pay.Paid(s);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 100)).StatusCode);                  // refund ingest
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_t", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 100L })).StatusCode);
        Assert.Equal(2, hooks.Transactions.Count);
        Pay.Age(f, 40); await Pay.Release(f);                                                                    // release
        Assert.Equal(3, hooks.Transactions.Count);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true; await Pay.Transfers(f);
        var t = f.WithDb(db => db.PaymentTransfers.AsNoTracking().First());
        f.Fake.FailPermanently = false;
        await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id));                      // staff retry
        Assert.Equal(4, hooks.Transactions.Count);
        Assert.All(hooks.Transactions, l => Assert.Equal(System.Data.IsolationLevel.Serializable, l));
    }

    [Fact]
    public async Task A_conflict_that_is_not_this_stripe_event_is_a_retryable_409_on_the_webhook_too()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var acct = await Pay.Onboard(s);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        hooks.BeforeWebhookSave = () =>
        {   // a concurrent delivery of ANOTHER event for the same payout wins the unique StripePayoutId first
            hooks.BeforeWebhookSave = null;
            f.WithDb(db => { db.PaymentPayouts.Add(new PaymentPayout { SellerId = s.Seller, StripePayoutId = "po_race", AmountCents = 100, Currency = "USD", EventAt = f.Time.GetUtcNow().UtcDateTime, CreatedAt = f.Time.GetUtcNow().UtcDateTime, UpdatedAt = f.Time.GetUtcNow().UtcDateTime }); db.SaveChanges(); return 0; });
            return Task.CompletedTask;
        };
        var before = f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "stripe"));
        var body = Pay.HookBody("payout.created", new { id = "po_race", amount = 100, currency = "usd", status = "pending" }, acct, f: f);
        var r = await Pay.PostHook(f, body);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);                                                      // not acknowledged: Stripe retries
        Assert.Equal(before, f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "stripe")));
        Assert.Equal(HttpStatusCode.OK, (await Pay.PostHook(f, body)).StatusCode);                               // the retry is applied normally
        // and a real second delivery of an applied event is still just a duplicate
        Assert.Equal("duplicate", (string)(await Pay.Json(await Pay.PostHook(f, body)))["status"]!);
    }

    // P4 ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Retry_reads_the_available_balance_inside_its_transaction()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 40); await Pay.Release(f);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true; await Pay.Transfers(f);
        var t = f.WithDb(db => db.PaymentTransfers.AsNoTracking().First());
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Available);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        // a staff debit / refund / dispute commits after the lease is taken but before the retry's transaction: the check must see it
        hooks.BeforeRetryTransaction = async () => await Pay.Run(f, async sp =>
        {
            await sp.GetRequiredService<LedgerService>().PostAsync(s.Seller, "USD", "adjustment", "k-p4", "debit", [new(LedgerAccount.Available, -2000, "adjustment"), new(LedgerAccount.Platform, 2000, "adjustment")]);
            await sp.GetRequiredService<LedgerService>().SaveAsync(); return 0;
        });
        f.Fake.FailPermanently = false;
        var r = await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id));
        Assert.Equal((false, "insufficient_available"), r);
        Assert.Equal(TransferStatus.Failed, f.WithDb(db => db.PaymentTransfers.AsNoTracking().First().Status));
        Assert.Equal(299, (await Pay.Bal(f, s.Seller)).Available);                                                // never negative
    }

    // P5 ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_long_run_keeps_its_lease_so_the_retry_guarantee_does_not_lapse()
    {
        using var f = new PayFactory();    // lease 300 s
        Assert.True(await Pay.Run(f, sp => sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName)));
        f.Time.Advance(TimeSpan.FromSeconds(200));
        await Pay.Reconcile(f);                                                                   // renews (and so does every transfer/release step of a run)
        f.Time.Advance(TimeSpan.FromSeconds(200));                                                // 400 s after the start, 200 s after the renewal
        Assert.False(await Pay.Run(f, sp => sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName)));
        f.Time.Advance(TimeSpan.FromSeconds(200));                                                // 400 s after the last renewal: the lease did expire normally
        Assert.True(await Pay.Run(f, sp => sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName)));
        // an expired lease is never revived by a late renewal (another instance may own it)
        await Pay.Run(f, async sp => { await sp.GetRequiredService<JobLeaseService>().ReleaseAsync(TransferService.JobName); return 0; });
        await Pay.Reconcile(f);
        Assert.True(await Pay.Run(f, sp => sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName)));
    }

    [Fact]
    public async Task Reconciliation_only_rechecks_recent_transfers()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 40); await Pay.Release(f); await Pay.Transfers(f);
        var t = f.WithDb(db => db.PaymentTransfers.AsNoTracking().First());
        f.Fake.Transfers[t.StripeTransferId!] = f.Fake.Transfers[t.StripeTransferId!] with { AmountCents = 1 };
        Assert.Equal(1, (await Pay.Reconcile(f)).Flagged);                                       // recent: checked
        f.WithDb(db => { db.ReconciliationFlags.RemoveRange(db.ReconciliationFlags); db.SaveChanges(); return 0; });
        Pay.Age(f, 40);
        var r = await Pay.Reconcile(f);                                                           // older than ReconcileDays (35): not re-checked
        Assert.Equal((0, 0), (r.Checked, r.Flagged));
    }

    // P6 ---------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_stripe_inquiry_is_shown_as_respondable_with_its_due_date_and_the_owner_is_emailed_without_any_debit()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "pi_inq2");
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds(); var due = now + 5 * 86400;
        await Pay.Hook(f, "charge.dispute.created", new { id = "dp_i2", payment_intent = "pi_inq2", amount = 2499, currency = "usd", status = "warning_needs_response", evidence_details = new { due_by = due } }, created: now);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
        var mails = Pay.Mails(f, Pay.OwnerEmail(f, s.Seller));
        Assert.Contains(mails, m => m.Subject.Contains("asked about a payment"));
        Assert.DoesNotContain(mails, m => m.Subject.Contains("opened a dispute"));
        var list = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes"));
        var d = list["items"]![0]!;
        Assert.Equal(("inquiry", true), ((string)d["status"]!, (bool)d["canRespond"]!));
        Assert.NotNull(d["evidenceDueBy"]);
        Assert.Equal(1, (int)(await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/summary")))["openDisputes"]!);
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_i2", payment_intent = "pi_inq2", amount = 2499, currency = "usd", status = "warning_needs_response" }, created: now + 5);
        Assert.Single(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)), m => m.Subject.Contains("asked about a payment"));   // one email per inquiry
    }

    // labels -----------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_shipping_label_bought_before_the_sale_is_released_is_not_a_negative_balance()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s);                                                                         // 2299 pending
        await Pay.Run(f, async sp =>
        {
            var l = sp.GetRequiredService<LedgerService>();
            await l.PostAsync(s.Seller, "USD", "shipping_label", "k-label", "Label", [new(LedgerAccount.Available, -800, "shipping_label"), new(LedgerAccount.Platform, 800, "shipping_label")]);
            await l.SaveAsync(); return 0;
        });
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, -800), (b.Pending, b.Available));
        Assert.False(b.Negative);                                                                  // the seller is owed 1499 in total
        var sum = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/summary"));
        Assert.False((bool)sum["balances"]![0]!["negative"]!);
        Assert.Empty(Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)).Where(m => m.Subject.Contains("negative")));
        // really owing money is still negative
        await Pay.Run(f, async sp =>
        {
            var l = sp.GetRequiredService<LedgerService>();
            await l.PostAsync(s.Seller, "USD", "adjustment", "k-big", "Debit", [new(LedgerAccount.Available, -2000, "adjustment"), new(LedgerAccount.Platform, 2000, "adjustment")]);
            await l.SaveAsync(); return 0;
        });
        Assert.True((await Pay.Bal(f, s.Seller)).Negative);
    }

    [Fact]
    public async Task The_ledger_reference_filter_matches_order_and_booking_numbers_over_every_row_and_stays_inside_the_account()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f); var other = await Orders.NewShop(f);
        var a = await Pay.Paid(s); var b = await Pay.Paid(s); var c = await Pay.Paid(other);
        var all = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/ledger"));
        Assert.Equal(2, all["items"]!.AsArray().Count);
        var one = await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?reference={a.Number.ToLower()}"));      // case-insensitive
        Assert.Equal(a.Number, (string)Assert.Single(one["items"]!.AsArray())!["orderNumber"]!);
        Assert.Equal(1, (int)one["total"]!);
        Assert.Empty((await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?reference={c.Number}")))["items"]!.AsArray());   // another account's number finds nothing
        Assert.Empty((await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/ledger?reference=NOPE-0")))["items"]!.AsArray());
    }
}
