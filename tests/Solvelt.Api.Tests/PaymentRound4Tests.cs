using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

public class ShortLeaseFactory : PayFactory
{
    public ShortLeaseFactory() { Extra["Payments:LeaseSeconds"] = "60"; }
}

/// <summary>Regression tests for the fourth review round of the payments backend.</summary>
public class PaymentRound4Tests
{
    private static PostgresException Pg40001() => new("could not serialize access due to concurrent update", "ERROR", "ERROR", "40001");

    // 1 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_serialization_failure_at_commit_is_a_409_even_though_the_transaction_is_already_completed()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        // PostgreSQL: a failed COMMIT leaves the transaction completed; a later RollbackAsync would throw InvalidOperationException and turn the 409 into a 500
        hooks.BeforeCommit = async tx => { hooks.BeforeCommit = null; await tx.RollbackAsync(); throw Pg40001(); };
        var r = await Pay.Refund(f, o.Mid, o.Number, 500);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("concurrent_update", await Orders.Code(r));
        Assert.Equal(0, f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Type == "refund.processed")));
        Assert.Empty(Pay.Journals(f, s.Seller, "refund"));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 500)).StatusCode);                 // the sender's retry is applied
        Assert.Single(Pay.Journals(f, s.Seller, "refund"));
    }

    [Theory]
    [InlineData("refund.processed")]
    [InlineData("dispute.created")]
    [InlineData("payment.captured")]
    public async Task A_serialization_failure_thrown_by_a_handler_is_a_409_not_a_500(string type)
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = type == "payment.captured" ? await Pay.Delivered(s) : await Pay.Paid(s);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        hooks.InHandler = t => { hooks.InHandler = null; throw Pg40001(); };
        var r = type switch
        {
            "refund.processed" => await Pay.Refund(f, o.Mid, o.Number, 100),
            "dispute.created" => await Pay.Event(f, "dispute.created", new { disputeId = "dp_r4", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 100L }),
            _ => await Pay.Capture(f, o.Mid, o.Number)
        };
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("concurrent_update", await Orders.Code(r));
    }

    [Fact]
    public async Task A_failed_commit_in_the_release_job_skips_that_order_and_does_not_abort_the_run()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s); await Pay.Paid(s);
        Pay.Age(f, 40);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        var calls = 0;
        hooks.BeforeCommit = async tx => { if (++calls == 1) { await tx.RollbackAsync(); throw Pg40001(); } };   // the first order loses its race, the second commits
        var run = await Pay.Release(f);                                                                         // must not throw
        Assert.Equal(1, run.Released);
        hooks.BeforeCommit = null;
        Assert.Equal(1, (await Pay.Release(f)).Released);                                                       // the next run picks the skipped one up
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Pending);
    }

    [Fact]
    public async Task A_failed_commit_in_staff_retry_is_a_conflict_and_leaves_the_transfer_failed()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 40); await Pay.Release(f);
        f.Fake.FailNextTransfers = 1; f.Fake.FailPermanently = true; await Pay.Transfers(f);
        f.Fake.FailPermanently = false;
        var t = f.WithDb(db => db.PaymentTransfers.AsNoTracking().First());
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        hooks.BeforeCommit = async tx => { hooks.BeforeCommit = null; await tx.RollbackAsync(); throw Pg40001(); };
        Assert.Equal((false, "conflict"), await Pay.Run(f, sp => sp.GetRequiredService<TransferService>().RetryAsync(t.Id)));
        Assert.Equal(TransferStatus.Failed, f.WithDb(db => db.PaymentTransfers.AsNoTracking().First().Status));
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Available);
        Assert.NotNull(await Pay.Lease(f));                                                                     // the lease was released
    }

    // 2 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task<(Shop S, Guid Id, string Mid, Shipment P1, Shipment P2, DateTime T0)> TwoParcels(ApiFactory f)
    {
        var s = await Orders.NewShop(f);
        await Pay.Fresh(s);
        var (mid, id, number, _) = await Orders.Place(s, qty: 2, price: 2000, shipping: 400);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        var line = await Orders.LineId(s.Owner, id);
        var t0 = f.Time.GetUtcNow().UtcDateTime;
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(line, 1)));
        f.Time.Advance(TimeSpan.FromHours(4));
        await Pay.Fresh(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship(line, 1)));
        var ships = f.WithDb(db => db.Shipments.AsNoTracking().Where(x => x.OrderId == id).OrderBy(x => x.ShippedAt).ToList());
        Assert.Equal(2, ships.Count);
        return (s, id, mid, ships[0], ships[1], t0);
    }

    [Fact]
    public async Task The_order_delivery_time_is_never_before_the_last_shipment_left_or_arrived()
    {
        using var f = new PayFactory();
        var (s, id, _, p1, p2, _) = await TwoParcels(f);
        f.Time.Advance(TimeSpan.FromHours(2));                                  // now = shipped(p2) + 2 h
        var arrive2 = p2.ShippedAt.AddHours(1);
        await Pay.Run(f, async sp =>
        {
            var actions = sp.GetRequiredService<OrderActions>(); var db = sp.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            var o = await actions.Full(db.Orders).FirstAsync(x => x.Id == id);
            Assert.True((await actions.ShipmentDeliveredAsync(o, o.Shipments.First(x => x.Id == p2.Id), arrive2)).Ok);           // parcel 2 arrives first (shipped + 1 h)
            o = await actions.Full(db.Orders).FirstAsync(x => x.Id == id);
            Assert.Equal(OrderStatus.Shipped, o.Status);
            // parcel 1 is reported with an OLD time (shipped(p1) + 1 h, which is before parcel 2 even left)
            Assert.True((await actions.ShipmentDeliveredAsync(o, o.Shipments.First(x => x.Id == p1.Id), p1.ShippedAt.AddHours(1))).Ok);
            return 0;
        });
        var after = f.WithDb(db => db.Orders.AsNoTracking().First(x => x.Id == id));
        Assert.Equal(OrderStatus.Delivered, after.Status);
        Assert.Equal(arrive2, after.DeliveredAt);                                // not shipped(p1) + 1 h: the release hold and return window start from the LAST event
        Assert.True(after.DeliveredAt >= p2.ShippedAt);
    }

    [Fact]
    public async Task Marking_a_multi_parcel_order_delivered_is_also_never_before_a_parcel_that_already_arrived()
    {
        using var f = new PayFactory();
        var (s, id, _, p1, p2, _) = await TwoParcels(f);
        f.Time.Advance(TimeSpan.FromHours(6));
        var arrive2 = p2.ShippedAt.AddHours(3);
        await Pay.Run(f, async sp =>
        {
            var actions = sp.GetRequiredService<OrderActions>(); var db = sp.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            var o = await actions.Full(db.Orders).FirstAsync(x => x.Id == id);
            Assert.True((await actions.ShipmentDeliveredAsync(o, o.Shipments.First(x => x.Id == p2.Id), arrive2)).Ok);
            o = await actions.Full(db.Orders).FirstAsync(x => x.Id == id);
            var res = await actions.MarkDeliveredAsync(o, Guid.NewGuid(), null, Party.Seller, null, at: p1.ShippedAt.AddHours(1));   // an old "delivered at" for the rest
            Assert.True(res.Ok);
            return 0;
        });
        var after = f.WithDb(db => db.Orders.AsNoTracking().First(x => x.Id == id));
        Assert.Equal(OrderStatus.Delivered, after.Status);
        Assert.Equal(arrive2, after.DeliveredAt);
    }

    [Fact]
    public async Task A_buyer_delivery_report_on_a_partly_shipped_order_can_not_precede_that_parcel_shipment()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (mid, id, _, _) = await Orders.Place(s, qty: 2, price: 2000, shipping: 400);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        var line = await Orders.LineId(s.Owner, id);
        var ship = Orders.Ship(line, 1);
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", ship));                                   // PartiallyShipped
        var shipped = f.WithDb(db => db.Shipments.AsNoTracking().First(x => x.OrderId == id));
        var tn = shipped.TrackingNumber;
        f.Time.Advance(TimeSpan.FromHours(3));
        var r = await Orders.Inbound(f, $"/api/internal/v1/orders/{mid}/delivery-confirmations", new { trackingNumber = tn, deliveredAt = shipped.ShippedAt.AddHours(-2) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = f.WithDb(db => db.Shipments.AsNoTracking().First(x => x.Id == shipped.Id));
        Assert.Equal(shipped.ShippedAt, after.DeliveredAt);                                       // clamped up to the shipment time, never before it
    }

    // 3 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_runner_that_lost_its_lease_stops_its_run()
    {
        using var f = new PayFactory();
        await Pay.Run(f, async sp =>
        {
            var leases = sp.GetRequiredService<JobLeaseService>();
            Assert.True(await leases.TryAcquireAsync(TransferService.JobName));
            f.WithDb(db => { db.PaymentJobLeases.Single().Owner = "someone-else"; db.SaveChanges(); return 0; });   // another runner owns it now
            await Assert.ThrowsAsync<LeaseLostException>(() => sp.GetRequiredService<ReleaseService>().RunAsync());
            await Assert.ThrowsAsync<LeaseLostException>(() => sp.GetRequiredService<TransferService>().RunAsync());
            await Assert.ThrowsAsync<LeaseLostException>(() => sp.GetRequiredService<ReconciliationService>().RunAsync());
            return 0;
        });
    }

    [Fact]
    public async Task A_stale_runner_can_neither_extend_nor_release_the_new_owners_lease()
    {
        using var f = new PayFactory();
        await Pay.Run(f, async sp =>
        {
            var stale = sp.GetRequiredService<JobLeaseService>();
            Assert.True(await stale.TryAcquireAsync(TransferService.JobName));
            f.Time.Advance(TimeSpan.FromSeconds(400));                                          // the stale runner's lease expired
            var fresh = await Pay.Lease(f);                                                     // a new runner took it over
            Assert.NotNull(fresh);
            Assert.False(await stale.RenewAsync());                                             // it can not extend the new owner's lease
            await stale.ReleaseAsync(TransferService.JobName);                                  // ... nor expire it
            var until = f.WithDb(db => db.PaymentJobLeases.AsNoTracking().Single());
            Assert.Equal(fresh, until.Owner);
            Assert.True(until.LockedUntil > f.Time.GetUtcNow().UtcDateTime);
            Assert.Null(await Pay.Lease(f));                                                    // still held by the new owner
            await Pay.Unlease(f, fresh!);                                                       // the owner's own release works
            Assert.NotNull(await Pay.Lease(f));
            return 0;
        });
    }

    [Fact]
    public async Task A_long_scan_over_orders_that_are_not_eligible_still_keeps_the_lease_alive()
    {
        using var f = new ShortLeaseFactory();                                                  // 60 s lease
        var s = await Orders.NewShop(f);
        for (var i = 0; i < 3; i++) { var (mid, _, number, _) = await Orders.Place(s); Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode); }   // pending, never delivered: not eligible
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        var steps = 0;
        hooks.AfterLeaseRenew = async () =>
        {
            steps++;
            f.Time.Advance(TimeSpan.FromSeconds(40));                                           // each step takes 40 s: three of them outlive a 60 s lease unless every step renews
            Assert.Null(await Pay.Lease(f));                                                    // nobody else may take it meanwhile
        };
        await Pay.Run(f, async sp =>
        {
            Assert.True(await sp.GetRequiredService<JobLeaseService>().TryAcquireAsync(TransferService.JobName));
            var run = await sp.GetRequiredService<ReleaseService>().RunAsync();
            Assert.Equal(0, run.Released);
            return 0;
        });
        Assert.True(steps >= 3, $"only {steps} lease checks for 3 orders");                      // the renewal runs at the top of EVERY iteration, eligible or not
    }

    // minor ------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_inquiry_that_is_already_under_review_can_not_be_responded_to_again()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        await Pay.Capture(f, o.Mid, o.Number, paymentRef: "pi_r4");
        var now = f.Time.GetUtcNow().ToUnixTimeSeconds();
        await Pay.Hook(f, "charge.dispute.created", new { id = "dp_r4", payment_intent = "pi_r4", amount = 2499, currency = "usd", status = "warning_needs_response" }, created: now);
        Assert.True((bool)(await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes")))["items"]![0]!["canRespond"]!);
        await Pay.Hook(f, "charge.dispute.updated", new { id = "dp_r4", payment_intent = "pi_r4", amount = 2499, currency = "usd", status = "warning_under_review" }, created: now + 5);
        var d = (await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes")))["items"]![0]!;
        Assert.Equal(("inquiry_review", false), ((string)d["status"]!, (bool)d["canRespond"]!));
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);                                 // still no money moved
        Assert.Equal(1, (int)(await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/summary")))["openDisputes"]!);   // but it is still open
        await Pay.Hook(f, "charge.dispute.closed", new { id = "dp_r4", payment_intent = "pi_r4", amount = 2499, currency = "usd", status = "warning_closed" }, created: now + 10);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
    }

    [Fact]
    public async Task Reconciliation_also_rechecks_old_transfers_that_were_updated_recently()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await Pay.Paid(s); Pay.Age(f, 40); await Pay.Release(f); await Pay.Transfers(f);
        var t = f.WithDb(db => db.PaymentTransfers.AsNoTracking().First());
        Pay.Age(f, 60);                                                                          // created 60 days ago, outside the 35-day window by CreatedAt
        f.Fake.Transfers[t.StripeTransferId!] = f.Fake.Transfers[t.StripeTransferId!] with { ReversedCents = 100 };
        Assert.Equal(0, (await Pay.Reconcile(f)).Checked);                                       // untouched since: not re-checked
        f.WithDb(db => { db.PaymentTransfers.First().UpdatedAt = f.Time.GetUtcNow().UtcDateTime; db.SaveChanges(); return 0; });   // a webhook just touched it (a reversal)
        var r = await Pay.Reconcile(f);
        Assert.Equal((1, 1), (r.Checked, r.Flagged));
    }

    [Fact]
    public async Task Staff_can_filter_a_sellers_ledger_by_order_or_booking_number()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var a = await Pay.Paid(s); await Pay.Paid(s);
        var staff = await StaffKit.LoginNew(f, Solvelt.Api.Admin.StaffRole.Support);
        var all = await Pay.Ok(await staff.Get($"/api/v1/admin/payments/sellers/{s.Seller}/ledger"));
        Assert.Equal(2, all["items"]!.AsArray().Count);
        var one = await Pay.Ok(await staff.Get($"/api/v1/admin/payments/sellers/{s.Seller}/ledger?reference={a.Number.ToLower()}"));
        Assert.Equal(a.Id.ToString(), (string)Assert.Single(one["items"]!.AsArray())!["orderId"]!);
    }
}
