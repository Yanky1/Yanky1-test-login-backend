using System.Net;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>C10: one Stripe dispute can span several seller orders. The key is (disputeId, seller order); the marketplace sends one dispute.* event per seller order with that seller order's share.</summary>
public class PaymentMultiSellerDisputeTests
{
    private sealed record Split(Shop A, Shop B, string Mid, Guid IdA, string NumA, Guid IdB, string NumB, long SaleA, long SaleB);

    /// <summary>One marketplace order with two seller orders: A sells 2000 + 400 shipping (sale 2400), B sells 3000 + 500 shipping (sale 3500). Captured under charge ch_{mid}.</summary>
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
        return new(a, b, mid, ia, na, ib, nb, 2400, 3500);
    }

    private static Task<HttpResponseMessage> Dp(ApiFactory f, string type, string dp, Split s, bool a, long amount, long fee = 1500, string? status = null, DateTime? at = null, bool withNumber = true) =>
        Pay.Event(f, type, new { disputeId = dp, marketplaceOrderId = s.Mid, sellerOrderNumber = withNumber ? (a ? s.NumA : s.NumB) : null, amountCents = amount, feeCents = fee, status, createdAt = at });

    private static object StripeDp(string id, string charge, long amount, string status = "needs_response") => new { id, charge, amount, currency = "usd", status, reason = "fraudulent" };

    private static List<PaymentDispute> Rows(ApiFactory f, string dp) => f.WithDb(db => db.PaymentDisputes.AsNoTracking().Where(d => d.ExternalId == dp).ToList());
    private static long Debited(ApiFactory f, Guid seller, string type) => Pay.Journals(f, seller, type).Sum(j => j.Entries.Where(e => e.Account == LedgerAccount.Platform).Sum(e => e.AmountCents));
    private static UnallocatedDispute? Item(ApiFactory f, string dp) => f.WithDb(db => db.UnallocatedDisputes.AsNoTracking().FirstOrDefault(u => u.ExternalId == dp));
    private static ReconciliationFlag? Flag(ApiFactory f, Guid? id) => f.WithDb(db => db.ReconciliationFlags.AsNoTracking().FirstOrDefault(x => x.Id == id));

    [Fact]
    public async Task Two_seller_orders_under_one_dispute_are_each_debited_exactly_their_share_and_replays_do_nothing()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms1", s, true, 2400, 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms1", s, false, 3500, 1700)).StatusCode);
        Assert.Equal(2, Rows(f, "dp_ms1").Count);
        Assert.Equal(2400, Debited(f, s.A.Seller, "dispute")); Assert.Equal(1500, Debited(f, s.A.Seller, "dispute_fee"));
        Assert.Equal(3500, Debited(f, s.B.Seller, "dispute")); Assert.Equal(1700, Debited(f, s.B.Seller, "dispute_fee"));
        // replays: the same event id, and a fresh event id for the same (dispute, seller order)
        var evId = "ev-r-" + Guid.NewGuid().ToString("N");
        var first = await Pay.Event(f, "dispute.updated", new { disputeId = "dp_ms1", marketplaceOrderId = s.Mid, sellerOrderNumber = s.NumA, status = "under_review" }, evId);
        var again = await Pay.Event(f, "dispute.updated", new { disputeId = "dp_ms1", marketplaceOrderId = s.Mid, sellerOrderNumber = s.NumA, status = "under_review" }, evId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms1", s, true, 2400, 1500)).StatusCode);
        Assert.Single(Pay.Journals(f, s.A.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.B.Seller, "dispute"));
        Assert.Equal(2, Rows(f, "dp_ms1").Count);
        // each journal is booked against ITS seller order, under a key scoped to that seller order
        Assert.Equal(s.IdA, Pay.Journals(f, s.A.Seller, "dispute")[0].OrderId);
        Assert.Equal($"dispute:dp_ms1:{s.IdB}", Pay.Journals(f, s.B.Seller, "dispute")[0].IdempotencyKey);
    }

    [Fact]
    public async Task A_dispute_event_without_sellerOrderNumber_on_a_multi_seller_order_is_rejected_and_books_nothing()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        var r = await Dp(f, "dispute.created", "dp_ms2", s, true, 2400, withNumber: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("seller_order_required", await Orders.Code(r));
        Assert.Empty(Rows(f, "dp_ms2")); Assert.Empty(Pay.Journals(f, s.A.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s.B.Seller, "dispute"));
        // an update by dispute id alone is ambiguous too once the dispute has two rows
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms2", s, true, 2400)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms2", s, false, 3500)).StatusCode);
        var u = await Pay.Event(f, "dispute.updated", new { disputeId = "dp_ms2", status = "under_review" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, u.StatusCode); Assert.Equal("seller_order_required", await Orders.Code(u));
        // the marketplace order id with a number the order does not have is a 404, not a silent other row
        var bad = await Pay.Event(f, "dispute.updated", new { disputeId = "dp_ms2", marketplaceOrderId = s.Mid, sellerOrderNumber = "NOPE-1", status = "under_review" });
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }

    [Fact]
    public async Task A_share_above_the_seller_orders_sale_is_rejected_and_the_exact_sale_is_accepted()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        var r = await Dp(f, "dispute.created", "dp_ms3", s, true, s.SaleA + 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("exceeds_sale", await Orders.Code(r));
        Assert.Empty(Rows(f, "dp_ms3")); Assert.Empty(Pay.Journals(f, s.A.Seller, "dispute"));
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms3", s, true, s.SaleA)).StatusCode);
        // B's sale is bigger than A's: a share valid for B is not valid for A
        var other = await Dp(f, "dispute.created", "dp_ms3b", s, true, s.SaleB);
        Assert.Equal("exceeds_sale", await Orders.Code(other));
    }

    [Fact]
    public async Task Won_and_closed_apply_per_seller_order_and_an_older_event_never_reopens_a_closed_dispute()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        var t = f.Time.GetUtcNow().UtcDateTime;
        await Dp(f, "dispute.created", "dp_ms4", s, true, 2400, at: t);
        await Dp(f, "dispute.created", "dp_ms4", s, false, 3500, at: t);
        // A's dispute is won, B's stays open
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.closed", "dp_ms4", s, true, 2400, status: "won", at: t.AddMinutes(10))).StatusCode);
        Assert.Equal("won", Rows(f, "dp_ms4").Single(r => r.OrderId == s.IdA).Status);
        Assert.Equal("needs_response", Rows(f, "dp_ms4").Single(r => r.OrderId == s.IdB).Status);
        Assert.Single(Pay.Journals(f, s.A.Seller, "dispute_won")); Assert.Empty(Pay.Journals(f, s.B.Seller, "dispute_won"));
        // an older event, and a newer one that tries to reopen, change nothing on A; B is not touched by either
        await Dp(f, "dispute.updated", "dp_ms4", s, true, 2400, status: "under_review", at: t.AddMinutes(5));
        await Dp(f, "dispute.updated", "dp_ms4", s, true, 2400, status: "needs_response", at: t.AddMinutes(20));
        Assert.Equal("won", Rows(f, "dp_ms4").Single(r => r.OrderId == s.IdA).Status);
        Assert.Equal("needs_response", Rows(f, "dp_ms4").Single(r => r.OrderId == s.IdB).Status);
        // B is lost: no credit for B, and A's credit is still the only one
        await Dp(f, "dispute.closed", "dp_ms4", s, false, 3500, status: "lost", at: t.AddMinutes(30));
        Assert.Equal("lost", Rows(f, "dp_ms4").Single(r => r.OrderId == s.IdB).Status);
        Assert.Empty(Pay.Journals(f, s.B.Seller, "dispute_won")); Assert.Single(Pay.Journals(f, s.A.Seller, "dispute_won"));
    }

    [Fact]
    public async Task A_Stripe_webhook_on_a_multi_seller_charge_never_debits_and_leaves_an_unallocated_item_that_clears_when_the_shares_arrive()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        var charge = "ch_" + s.Mid;
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_ms5", charge, 5900))).StatusCode);
        Assert.Empty(Rows(f, "dp_ms5"));
        Assert.Empty(Pay.Journals(f, s.A.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s.B.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s.A.Seller, "dispute_fee"));
        var item = Item(f, "dp_ms5")!;
        Assert.Null(item.ResolvedAt); Assert.Equal(5900, item.AmountCents); Assert.Equal(0, item.AllocatedCents); Assert.Equal(s.Mid, item.MarketplaceOrderId);
        var flag = Flag(f, item.FlagId)!;
        Assert.Equal("dispute_unallocated", flag.Kind); Assert.Null(flag.ResolvedAt); Assert.Contains("dp_ms5", flag.Detail);
        // a replay or a later Stripe update does not duplicate the item
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_ms5", charge, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Equal(1, f.WithDb(db => db.UnallocatedDisputes.Count(u => u.ExternalId == "dp_ms5")));
        Assert.Equal("under_review", Item(f, "dp_ms5")!.Status);
        // the marketplace allocates A's share: money moves for A only, the item stays open (2400 of 5900)
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms5", s, true, 2400)).StatusCode);
        Assert.Equal(2400, Debited(f, s.A.Seller, "dispute")); Assert.Empty(Pay.Journals(f, s.B.Seller, "dispute"));
        Assert.Null(Item(f, "dp_ms5")!.ResolvedAt); Assert.Equal(2400, Item(f, "dp_ms5")!.AllocatedCents);
        // the newer Stripe state (under_review) applies to the row the marketplace created afterwards
        Assert.Equal("under_review", Rows(f, "dp_ms5").Single().Status);
        // B's share completes the allocation: the item and its flag are cleared
        Assert.Equal(HttpStatusCode.OK, (await Dp(f, "dispute.created", "dp_ms5", s, false, 3500)).StatusCode);
        var done = Item(f, "dp_ms5")!;
        Assert.NotNull(done.ResolvedAt); Assert.Equal(5900, done.AllocatedCents);
        Assert.NotNull(Flag(f, done.FlagId)!.ResolvedAt);
        Assert.Equal(3500, Debited(f, s.B.Seller, "dispute"));
        // a Stripe update after the allocation updates both rows (no money) and opens no new item
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_ms5", charge, 5900, "needs_response"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 50);
        Assert.Single(Pay.Journals(f, s.A.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.B.Seller, "dispute"));
        Assert.NotNull(Item(f, "dp_ms5")!.ResolvedAt);
    }

    [Fact]
    public async Task A_Stripe_event_after_the_allocation_only_updates_the_rows_and_never_debits_twice()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        var charge = "ch_" + s.Mid;
        await Dp(f, "dispute.created", "dp_ms6", s, true, 2400);
        await Dp(f, "dispute.created", "dp_ms6", s, false, 3500);
        var at = f.Time.GetUtcNow().ToUnixTimeSeconds();
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_ms6", charge, 5900, "under_review"), created: at + 10);
        Assert.All(Rows(f, "dp_ms6"), r => Assert.Equal("under_review", r.Status));
        Assert.Null(Item(f, "dp_ms6"));                                    // fully allocated: nothing for staff to chase
        await Pay.Hook(f, "charge.dispute.closed", StripeDp("dp_ms6", charge, 5900, "lost"), created: at + 20);
        Assert.All(Rows(f, "dp_ms6"), r => Assert.Equal("lost", r.Status));
        Assert.Single(Pay.Journals(f, s.A.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.B.Seller, "dispute"));
        Assert.Equal(2400, Debited(f, s.A.Seller, "dispute")); Assert.Equal(3500, Debited(f, s.B.Seller, "dispute"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Stripe_event_and_a_marketplace_event_for_the_same_dispute_and_seller_order_debit_once(bool stripeFirst)
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var charge = "ch_" + o.Mid;
        Task<HttpResponseMessage> Stripe() => Pay.Hook(f, "charge.dispute.created", StripeDp("dp_one", charge, 2499));
        Task<HttpResponseMessage> Market() => Pay.Event(f, "dispute.created", new { disputeId = "dp_one", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, amountCents = 2499L, feeCents = 1500L });
        if (stripeFirst) { await Stripe(); await Market(); } else { await Market(); await Stripe(); }
        Assert.Single(Rows(f, "dp_one"));
        Assert.Single(Pay.Journals(f, s.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.Seller, "dispute_fee"));
        Assert.Equal(2499, Debited(f, s.Seller, "dispute"));
        Assert.Null(Item(f, "dp_one"));
    }

    [Fact]
    public async Task A_single_seller_order_needs_no_sellerOrderNumber_and_rows_booked_under_the_old_key_are_not_debited_again()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var r = await Pay.Event(f, "dispute.created", new { disputeId = "dp_old", marketplaceOrderId = o.Mid, amountCents = 1000L, feeCents = 1500L });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // the same journals as an earlier release would have written them: keyed by the dispute id alone
        f.WithDb(db =>
        {
            foreach (var j in db.LedgerJournals.Where(j => j.IdempotencyKey.StartsWith("dispute:dp_old") || j.IdempotencyKey.StartsWith("dispute-fee:dp_old")).ToList())
                db.LedgerJournals.Where(x => x.Id == j.Id).ExecuteUpdate(u => u.SetProperty(x => x.IdempotencyKey, j.IdempotencyKey.Replace($":{o.Id}", "")));
            return 0;
        });
        Assert.Contains(Pay.Journals(f, s.Seller, "dispute"), j => j.IdempotencyKey == "dispute:dp_old");
        await Pay.Event(f, "dispute.updated", new { disputeId = "dp_old", marketplaceOrderId = o.Mid, status = "under_review" });
        await Pay.Event(f, "dispute.created", new { disputeId = "dp_old", marketplaceOrderId = o.Mid, amountCents = 1000L, feeCents = 1500L });
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_old", "ch_" + o.Mid, 1000, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.Single(Pay.Journals(f, s.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.Seller, "dispute_fee"));
        Assert.Single(Rows(f, "dp_old"));
    }

    [Fact]
    public async Task Staff_see_the_unallocated_dispute_and_resolving_its_flag_closes_the_item_for_good()
    {
        using var f = new PayFactory();
        var s = await TwoSellers(f);
        await Pay.Hook(f, "charge.dispute.created", StripeDp("dp_ms8", "ch_" + s.Mid, 5900));
        var item = Item(f, "dp_ms8")!;
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        var list = await Pay.Ok(await ro.Get("/api/v1/admin/payments/unallocated-disputes"));
        var row = list["items"]!.AsArray().Single(x => (string)x!["disputeId"]! == "dp_ms8")!;
        Assert.Equal(5900, (long)row["stripeAmountCents"]!); Assert.Equal(0, (long)row["allocatedCents"]!); Assert.Equal(item.FlagId.ToString(), (string)row["flagId"]!);
        // the same item is a flag in the reconciliation list staff already work from; a read-only account cannot resolve it
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/api/v1/admin/payments/reconciliation/{item.FlagId}/resolve", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.Post($"/api/v1/admin/payments/reconciliation/{item.FlagId}/resolve", new { reason = "Handled with the marketplace by phone" })).StatusCode);
        var after = Item(f, "dp_ms8")!;
        Assert.NotNull(after.ResolvedAt); Assert.StartsWith("staff", after.Note);
        Assert.Empty((await Pay.Ok(await ro.Get("/api/v1/admin/payments/unallocated-disputes")))["items"]!.AsArray());
        // a later Stripe update must not reopen what staff closed, nor raise a second flag
        await Pay.Hook(f, "charge.dispute.updated", StripeDp("dp_ms8", "ch_" + s.Mid, 5900, "under_review"), created: f.Time.GetUtcNow().ToUnixTimeSeconds() + 5);
        Assert.NotNull(Item(f, "dp_ms8")!.ResolvedAt);
        Assert.Equal(1, f.WithDb(db => db.ReconciliationFlags.Count(x => x.Kind == "dispute_unallocated")));
        Assert.Equal("under_review", Item(f, "dp_ms8")!.Status);
    }
}
