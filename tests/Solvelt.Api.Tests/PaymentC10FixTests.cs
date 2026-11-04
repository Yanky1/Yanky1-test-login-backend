using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

/// <summary>Verification findings on C10 (disputes on multi-seller orders).</summary>
public class PaymentC10FixTests
{
    private static PostgresException Pg40001() => new("could not serialize access due to concurrent update", "ERROR", "ERROR", "40001");

    private static object StripeDp(string id, string charge, long amount, string status = "needs_response", string currency = "usd") => new { id, charge, amount, currency, status, reason = "fraudulent" };
    private static List<PaymentDispute> Rows(ApiFactory f, string dp) => f.WithDb(db => db.PaymentDisputes.AsNoTracking().Where(d => d.ExternalId == dp).ToList());
    private static long Debited(ApiFactory f, Guid seller, string type) => Pay.Journals(f, seller, type).Sum(j => j.Entries.Where(e => e.Account == LedgerAccount.Platform).Sum(e => e.AmountCents));
    private static List<ReconciliationFlag> Flags(ApiFactory f, string kind) => f.WithDb(db => db.ReconciliationFlags.AsNoTracking().Where(x => x.Kind == kind).ToList());
    private static UnallocatedDispute? Item(ApiFactory f, string dp) => f.WithDb(db => db.UnallocatedDisputes.AsNoTracking().FirstOrDefault(u => u.ExternalId == dp));

    private sealed record Split(Shop A, Shop B, string Mid, Guid IdA, string NumA, Guid IdB, string NumB);

    /// <summary>One marketplace order, two seller orders: A sells 2000 + 400 shipping (sale 2400), B sells 3000 + 500 shipping (sale 3500). Captured under charge ch_{mid}.</summary>
    private static async Task<Split> TwoSellers(ApiFactory f)
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var mid = Orders.Mid();
        var body = Orders.OrderBody(f, mid, [Orders.Line(a.Seller, a.Brand, a.Product, 1, 2000, "LA"), Orders.Line(b.Seller, b.Brand, b.Product, 1, 3000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 400 }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 500 }]);
        Assert.Equal(HttpStatusCode.Created, (await Orders.Inbound(f, "/api/internal/v1/orders", body)).StatusCode);
        var (ia, na) = f.WithDb(db => db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == mid && o.SellerId == a.Seller).Select(o => new ValueTuple<Guid, string>(o.Id, o.Number)).First());
        var (ib, nb) = f.WithDb(db => db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == mid && o.SellerId == b.Seller).Select(o => new ValueTuple<Guid, string>(o.Id, o.Number)).First());
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid)).StatusCode);
        return new(a, b, mid, ia, na, ib, nb);
    }

    private static Task<HttpResponseMessage> Share(ApiFactory f, string dp, Split s, bool a, long amount, string? status = null, DateTime? at = null, string? currency = null) =>
        Pay.Event(f, status is "won" or "lost" ? "dispute.closed" : "dispute.created", new { disputeId = dp, marketplaceOrderId = s.Mid, sellerOrderNumber = a ? s.NumA : s.NumB, amountCents = amount, feeCents = 1500L, status, createdAt = at, currency });

    // 1 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Stripe_event_whose_charge_maps_to_another_seller_order_than_the_existing_row_debits_nobody_and_raises_a_flag()
    {
        using var f = new PayFactory();
        var s1 = await Orders.NewShop(f); var s2 = await Orders.NewShop(f);
        var m1 = await Pay.Paid(s1); var m2 = await Pay.Paid(s2);
        // the marketplace allocates dp_x to seller order 1 ...
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_x", marketplaceOrderId = m1.Mid, sellerOrderNumber = m1.Number, amountCents = 1000L, feeCents = 1500L })).StatusCode);
        // ... and Stripe reports the same dispute on a charge that belongs to ANOTHER marketplace order (seller 2)
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_x", "ch_" + m2.Mid, 1000))).StatusCode);
        Assert.Single(Rows(f, "dp_x"));
        Assert.Equal(m1.Id, Rows(f, "dp_x")[0].OrderId);
        Assert.Equal(1000, Debited(f, s1.Seller, "dispute"));
        Assert.Empty(Pay.Journals(f, s2.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s2.Seller, "dispute_fee"));
        var flag = Assert.Single(Flags(f, "dispute_unattributed"));
        Assert.Null(flag.ResolvedAt); Assert.Contains("dp_x", flag.Detail);
        // a replay of the Stripe event (or a later update) does not raise a second flag, and still debits nobody
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_x", "ch_" + m2.Mid, 1000, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Single(Flags(f, "dispute_unattributed"));
        Assert.Empty(Pay.Journals(f, s2.Seller, "dispute")); Assert.Single(Rows(f, "dp_x"));
    }

    // 2 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Stripe_dispute_event_runs_in_a_serializable_transaction_and_other_Stripe_events_do_not()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        var before = hooks.Transactions.Count;
        await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_tx", "ch_" + o.Mid, 1000));
        Assert.Equal(before + 1, hooks.Transactions.Count);
        Assert.Equal(System.Data.IsolationLevel.Serializable, hooks.Transactions.Last());
        var acct = await Pay.Onboard(s);
        before = hooks.Transactions.Count;
        await Pay.Hook(f, "account.updated", Pay.AccountObj(acct), acct, created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Equal(before, hooks.Transactions.Count);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("commit")]
    public async Task A_serialization_failure_on_a_Stripe_dispute_event_is_a_409_and_the_retry_sees_the_marketplace_allocation(string where)
    {
        using var f = new PayFactory();
        var sp = await TwoSellers(f);
        var charge = "ch_" + sp.Mid;
        var hooks = f.Services.GetRequiredService<PaymentTestHooks>();
        var body = Pay.HookBody("charge.dispute.updated", StripeDp("dp_race", charge, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 10, f: f);
        // the marketplace's two shares commit while this Stripe event is in flight: on PostgreSQL the Stripe transaction (which read "no rows yet") loses with 40001
        if (where == "save") hooks.BeforeWebhookSave = () => { hooks.BeforeWebhookSave = null; throw Pg40001(); };
        else hooks.BeforeCommit = async tx => { hooks.BeforeCommit = null; await tx.RollbackAsync(); throw Pg40001(); };
        var r = await Pay.PostHook(f, body);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);                                                    // not acknowledged: Stripe retries
        Assert.Equal(0, f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "stripe" && p.Type == "charge.dispute.updated")));
        Assert.Null(Item(f, "dp_race"));                                                                         // nothing of the failed attempt is left behind
        Assert.Empty(Flags(f, "dispute_unallocated"));
        Assert.Equal(HttpStatusCode.OK, (await Share(f, "dp_race", sp, true, 2400)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Share(f, "dp_race", sp, false, 3500)).StatusCode);
        // Stripe's retry now sees both allocated rows: it updates their status and leaves no stale unallocated item
        Assert.Equal(HttpStatusCode.OK, (await Pay.PostHook(f, body)).StatusCode);
        Assert.All(Rows(f, "dp_race"), x => Assert.Equal("under_review", x.Status));
        Assert.Equal(2, Rows(f, "dp_race").Count);
        Assert.Null(Item(f, "dp_race")); Assert.Empty(Flags(f, "dispute_unallocated"));
        Assert.Equal(2400, Debited(f, sp.A.Seller, "dispute")); Assert.Equal(3500, Debited(f, sp.B.Seller, "dispute"));
    }

    // 3 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_marketplace_dispute_in_another_currency_than_the_order_is_rejected()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var r = await Pay.Event(f, "dispute.created", new { disputeId = "dp_eur", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 1000L, feeCents = 1500L, currency = "EUR" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("currency_mismatch", await Orders.Code(r));
        Assert.Empty(Rows(f, "dp_eur")); Assert.Empty(Pay.Journals(f, s.Seller, "dispute"));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_eur", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 1000L, feeCents = 1500L, currency = "USD" })).StatusCode);
    }

    [Fact]
    public async Task A_Stripe_dispute_in_another_currency_than_the_seller_orders_records_nothing_counts_for_nothing_and_raises_a_flag()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_cur1", "ch_" + o.Mid, 1000, currency: "eur"))).StatusCode);
        Assert.Empty(Rows(f, "dp_cur1")); Assert.Empty(Pay.Journals(f, s.Seller, "dispute"));
        Assert.Single(Flags(f, "dispute_currency_mismatch"));
        // multi-seller charge: a EUR dispute is not recorded as an unallocated item and does not count toward the allocation
        var sp = await TwoSellers(f);
        await Share(f, "dp_cur2", sp, true, 2400);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_cur2", "ch_" + sp.Mid, 5900, currency: "eur"))).StatusCode);
        Assert.Null(Item(f, "dp_cur2")); Assert.Equal(2, Flags(f, "dispute_currency_mismatch").Count);
        Assert.Equal("needs_response", Rows(f, "dp_cur2").Single().Status);
        // the same dispute in the right currency is recorded: 2400 of 5900 are allocated, so staff are told about the rest
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_cur2", "ch_" + sp.Mid, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5)).StatusCode);
        Assert.Equal(2400, Item(f, "dp_cur2")!.AllocatedCents);
    }

    // 4 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Stripe_won_with_no_marketplace_won_after_a_day_is_flagged_and_the_flag_clears_when_the_credit_arrives()
    {
        using var f = new PayFactory();
        var sp = await TwoSellers(f);
        var charge = "ch_" + sp.Mid;
        await Share(f, "dp_won", sp, true, 2400); await Share(f, "dp_won", sp, false, 3500);
        var at = f.Time.GetUtcNow().ToUnixTimeSeconds();
        await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_won", charge, 5900, "won"), created: at + 10);
        Assert.All(Rows(f, "dp_won"), x => Assert.Equal("won", x.Status));
        Assert.Empty(Pay.Journals(f, sp.A.Seller, "dispute_won")); Assert.Empty(Pay.Journals(f, sp.B.Seller, "dispute_won"));   // Stripe alone credits nobody
        await Pay.Reconcile(f);
        Assert.Empty(Flags(f, "dispute_won_credit_missing"));                                                    // not yet: the marketplace may still be on its way
        Pay.Age(f, 2);                                                                                           // a day later ...
        await Pay.Reconcile(f);
        Assert.Equal(2, Flags(f, "dispute_won_credit_missing").Count(x => x.ResolvedAt == null));
        await Pay.Reconcile(f);
        Assert.Equal(2, Flags(f, "dispute_won_credit_missing").Count);                                           // one open flag per seller order, not one per run
        // the marketplace's won event for seller order A credits A (an older event time does not matter: the money is posted whatever the status already is)
        Assert.Equal(HttpStatusCode.OK, (await Share(f, "dp_won", sp, true, 2400, status: "won", at: f.Time.GetUtcNow().UtcDateTime.AddDays(-5))).StatusCode);
        Assert.Single(Pay.Journals(f, sp.A.Seller, "dispute_won"));
        await Pay.Reconcile(f);
        var flags = Flags(f, "dispute_won_credit_missing");
        Assert.Equal(sp.B.Seller, Assert.Single(flags, x => x.ResolvedAt == null).SellerId);                     // only B's credit is still missing
    }

    [Fact]
    public async Task A_Stripe_won_that_the_marketplace_never_allocated_is_flagged_after_a_day()
    {
        using var f = new PayFactory();
        var sp = await TwoSellers(f);
        await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_wonu", "ch_" + sp.Mid, 5900, "won"));
        Assert.Equal("won", Item(f, "dp_wonu")!.Status);
        await Pay.Reconcile(f);
        Assert.Empty(Flags(f, "dispute_won_unallocated"));
        Pay.Age(f, 2);
        await Pay.Reconcile(f); await Pay.Reconcile(f);
        var flag = Assert.Single(Flags(f, "dispute_won_unallocated"));
        Assert.Contains("dp_wonu", flag.Detail); Assert.Null(flag.ResolvedAt);
    }

    // 5 ------------------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_database_itself_refuses_a_second_row_for_the_same_dispute_and_seller_order_or_booking()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var now = f.Time.GetUtcNow().UtcDateTime;
        PaymentDispute Row(string dp, Guid? order, Guid? booking) => new() { SellerId = s.Seller, ExternalId = dp, OrderId = order, BookingId = booking, AmountCents = 100, FeeCents = 1500, CreatedAt = now, UpdatedAt = now, EventAt = now };
        var o1 = Guid.NewGuid(); var o2 = Guid.NewGuid(); var b1 = Guid.NewGuid();
        f.WithDb(db => { db.PaymentDisputes.AddRange(Row("dp_u", o1, null), Row("dp_u", o2, null), Row("dp_u", null, b1), Row("dp_v", o1, null)); db.SaveChanges(); return 0; });   // other seller order / booking / dispute: allowed
        Assert.Throws<DbUpdateException>(() => f.WithDb(db => { db.PaymentDisputes.Add(Row("dp_u", o1, null)); db.SaveChanges(); return 0; }));
        Assert.Throws<DbUpdateException>(() => f.WithDb(db => { db.PaymentDisputes.Add(Row("dp_u", null, b1)); db.SaveChanges(); return 0; }));
        Assert.Equal(4, f.WithDb(db => db.PaymentDisputes.Count()));
    }
}
