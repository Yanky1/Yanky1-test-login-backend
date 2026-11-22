using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Payments;

namespace Solvelt.Api.Tests;

/// <summary>Owner switch on: transfers wait while the unfunded Sola balance of their currency is above zero.</summary>
public class SolaHoldFactory : PayFactory { public SolaHoldFactory() { Extra["Payments:Sola:HoldTransfersWhenUnfunded"] = "true"; } }

/// <summary>C19: Sola-paid phone orders. The provider holding the buyer money changes only the counter account; seller money, release and transfers are identical to Stripe.</summary>
public class PaymentSolaTests
{
    private const string Admin = "/api/v1/admin/payments";

    private static Task<HttpResponseMessage> SolaCapture(ApiFactory f, string mid, string? number, string xRef, string? eventId = null) =>
        Pay.Event(f, "payment.captured", new { kind = "order", provider = "sola", marketplaceOrderId = mid, sellerOrderNumber = number, paymentRef = xRef, currency = "USD" }, eventId);

    private static Task<HttpResponseMessage> SolaRefund(ApiFactory f, string mid, string number, long amount, string refundId, long shipping = 0, string? eventId = null) =>
        Pay.Event(f, "refund.processed", new { provider = "sola", refundId, marketplaceOrderId = mid, sellerOrderNumber = number, amountCents = amount, shippingCents = shipping }, eventId);

    private static Task<HttpResponseMessage> SolaDispute(ApiFactory f, string type, string id, string mid, string? number, long? amount = null, long? fee = null, string? status = null, string? eventId = null) =>
        Pay.Event(f, type, new { provider = "sola", disputeId = id, marketplaceOrderId = mid, sellerOrderNumber = number, amountCents = amount, feeCents = fee, status }, eventId);

    /// <summary>A delivered seller order (items 1999, shipping 500, fee 200, net 2299) paid through Sola.</summary>
    private static async Task<(string Mid, Guid Id, string Number)> SolaPaid(Shop s, string? xRef = null)
    {
        var o = await Pay.Delivered(s);
        var r = await SolaCapture(s.F, o.Mid, o.Number, xRef ?? "1098" + Random.Shared.NextInt64(1_000_000, 9_999_999));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return o;
    }

    private static LedgerJournal Journal(ApiFactory f, string key) => f.WithDb(db => db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Single(j => j.IdempotencyKey == key));
    private static long Counter(LedgerJournal j, LedgerAccount a) => j.Entries.Where(e => e.Account == a).Sum(e => e.AmountCents);
    private static (LedgerAccount, string, long)[] SellerSide(LedgerJournal j) => j.Entries
        .Where(e => e.Account is not (LedgerAccount.Platform or LedgerAccount.PlatformSola)).Select(e => (e.Account, e.Kind, e.AmountCents)).OrderBy(x => x.Account).ThenBy(x => x.Kind).ToArray();
    private static List<PaymentDispute> Disputes(ApiFactory f, string id) => f.WithDb(db => db.PaymentDisputes.AsNoTracking().Where(d => d.ExternalId == id).ToList());
    private static long Sola(ApiFactory f) => f.WithDb(db => db.LedgerEntries.Where(e => e.Account == LedgerAccount.PlatformSola).Sum(e => (long?)e.AmountCents) ?? 0);

    private static void AllJournalsBalance(ApiFactory f) => f.WithDb(db =>
    {
        var bad = db.LedgerEntries.AsNoTracking().GroupBy(e => e.JournalId).Select(g => new { g.Key, S = g.Sum(x => x.AmountCents) }).Where(x => x.S != 0).Count();
        Assert.Equal(0, bad);
        return 0;
    });

    // ---- sale, release, transfer --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Sola_sale_posts_to_PlatformSola_and_the_seller_side_equals_an_equal_Stripe_sale()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var stripe = await Pay.Paid(s);
        var o = await Pay.Delivered(s);
        var r = await Pay.Ok(await SolaCapture(f, o.Mid, o.Number, "10984432761"));
        Assert.Equal("sola", (string)r["result"]!["provider"]!);

        var js = Journal(f, $"sale:{stripe.Id}"); var jo = Journal(f, $"sale:{o.Id}");
        Assert.Equal(SellerSide(js), SellerSide(jo));                                       // pending items, shipping, fee and Fees: identical
        Assert.Equal((-2499L, 0L), (Counter(js, LedgerAccount.Platform), Counter(js, LedgerAccount.PlatformSola)));
        Assert.Equal((0L, -2499L), (Counter(jo, LedgerAccount.Platform), Counter(jo, LedgerAccount.PlatformSola)));
        Assert.Equal(("sola", "stripe"), (jo.Provider, js.Provider));
        Assert.Contains("Sola", jo.Memo);
        Assert.Equal((2 * 2299L, 0L), ((await Pay.Bal(f, s.Seller)).Pending, (await Pay.Bal(f, s.Seller)).Available));
        var reference = f.WithDb(db => db.PaymentReferences.AsNoTracking().Single(p => p.MarketplaceOrderId == o.Mid));
        Assert.Equal(("sola", "10984432761"), (reference.Provider, reference.Ref));

        // the seller sees the provider on the row (the statement / money panel says "Phone order (Sola)")
        var ledger = await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?orderId={o.Id}"));
        var row = ledger["items"]!.AsArray().Single()!;
        Assert.Equal(("sale", "sola", 2299L), ((string)row["type"]!, (string)row["provider"]!, (long)row["amountCents"]!));
        var stripeRow = (await Pay.Ok(await s.Owner.Get($"/api/v1/seller/payments/ledger?orderId={stripe.Id}")))["items"]!.AsArray().Single()!;
        Assert.Equal("stripe", (string)stripeRow["provider"]!);
        // the order money panel says which provider took the payment
        Assert.Equal("sola", (string)(await Orders.Detail(s.Owner, o.Id))["money"]!["paymentProvider"]!);
        Assert.Equal("stripe", (string)(await Orders.Detail(s.Owner, stripe.Id))["money"]!["paymentProvider"]!);
        var unpaid = await Pay.Delivered(s);
        Assert.Null((await Orders.Detail(s.Owner, unpaid.Id))["money"]!["paymentProvider"]);
        var csv = await (await s.Owner.Get($"/api/v1/seller/payments/statements/{f.Time.GetUtcNow():yyyy-MM}/csv")).Content.ReadAsStringAsync();
        Assert.Contains("phone order, Sola", csv);
        AllJournalsBalance(f);
    }

    [Fact]
    public async Task Release_and_transfer_of_a_Sola_sale_are_the_same_as_for_Stripe_and_are_not_held_by_default()
    {
        using var f = new PayFactory();
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        await Pay.Onboard(a); await Pay.Onboard(b);
        await Pay.Paid(a);                // Stripe
        await SolaPaid(b);                // Sola, same order shape
        Pay.Age(f, 8);
        await Pay.Release(f);
        var (ba, bb) = (await Pay.Bal(f, a.Seller), await Pay.Bal(f, b.Seller));
        Assert.Equal((ba.Pending, ba.Available, ba.Reserved), (bb.Pending, bb.Available, bb.Reserved));
        Assert.Equal(2299, bb.Available);
        var rel = Assert.Single(Pay.Journals(f, b.Seller, "release"));
        Assert.Null(rel.Provider);                                                        // releases are not buyer-money rows
        var t = await Pay.Transfers(f);
        Assert.Equal(2, t.Created);                                                         // default: the unfunded Sola balance holds nothing
        Assert.Equal(((await Pay.Bal(f, a.Seller)).Settled, 2299L), ((await Pay.Bal(f, b.Seller)).Settled, 2299L));
        var view = await Pay.Ok(await (await StaffKit.LoginNew(f, StaffRole.ReadOnly)).Get($"{Admin}/sola/treasury"));
        var usd = view["currencies"]!.AsArray().Single()!;
        Assert.Equal((2299L, 0L, 2299L, false), ((long)usd["releasedCents"]!, (long)usd["fundedCents"]!, (long)usd["unfundedCents"]!, (bool)usd["transfersHeld"]!));
        Assert.False((bool)view["holdTransfersWhenUnfunded"]!);
        AllJournalsBalance(f);
    }

    // ---- refunds ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_Sola_refund_before_and_after_release_credits_PlatformSola_and_debits_the_seller_like_Stripe()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var before = await SolaPaid(s);
        var after = await SolaPaid(s);
        var stripe = await Pay.Paid(s);

        // before release: reduces Pending
        var r1 = await Pay.Ok(await SolaRefund(f, before.Mid, before.Number, 1000, "11000001"));
        Assert.Equal(("sola", 900L, 100L, 900L), ((string)r1["result"]!["provider"]!, (long)r1["result"]!["debitCents"]!, (long)r1["result"]!["feeRefundedCents"]!, (long)r1["result"]!["fromPendingCents"]!));
        var j1 = Journal(f, "sola:refund:11000001");
        Assert.Equal((1000L, 0L, "sola"), (Counter(j1, LedgerAccount.PlatformSola), Counter(j1, LedgerAccount.Platform), j1.Provider));
        // the same refund id on a Stripe order is another refund (ids are unique per provider)
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, stripe.Mid, stripe.Number, 1000, refundId: "11000001")).StatusCode);
        Assert.Equal(1000L, Counter(Journal(f, "refund:11000001"), LedgerAccount.Platform));

        // after release: debits Available
        Pay.Age(f, 8); await Pay.Release(f);
        var avail = (await Pay.Bal(f, s.Seller)).Available;
        var r2 = await Pay.Ok(await SolaRefund(f, after.Mid, after.Number, 1999, "11000002", shipping: 500));
        Assert.Equal((2299L, 0L), ((long)r2["result"]!["debitCents"]!, (long)r2["result"]!["fromPendingCents"]!));
        Assert.Equal(avail - 2299, (await Pay.Bal(f, s.Seller)).Available);
        Assert.Equal(2499L, Counter(Journal(f, "sola:refund:11000002"), LedgerAccount.PlatformSola));
        // over-refund rules are the same: the rest of 'before' is 999 items
        var over = await SolaRefund(f, before.Mid, before.Number, 1000, "11000003");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exceeds_sale"), (over.StatusCode, await Orders.Code(over)));
        // held at Sola = two sales - refunds
        Assert.Equal(-(2 * 2499L) + 1000 + 2499, Sola(f));
        AllJournalsBalance(f);
    }

    // ---- disputes -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Sola_disputes_single_seller_won_and_lost_with_fee_and_no_respond_button()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var won = await SolaPaid(s); var lost = await SolaPaid(s);
        var solaBefore = Sola(f);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-1", won.Mid, null, 2499, 1500, "needs_response")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-2", lost.Mid, null, 2000, 1200)).StatusCode);
        Assert.Equal(solaBefore + 2499 + 1500 + 2000 + 1200, Sola(f));
        var d = Journal(f, $"sola:dispute:CB-1:{won.Id}");
        Assert.Equal((2499L, -2499L, "sola"), (Counter(d, LedgerAccount.PlatformSola), Counter(d, LedgerAccount.Available), d.Provider));
        Assert.Equal(1500L, Counter(Journal(f, $"sola:dispute-fee:CB-1:{won.Id}"), LedgerAccount.PlatformSola));
        Assert.Equal(-(2499L + 1500 + 2000 + 1200), (await Pay.Bal(f, s.Seller)).Available);

        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.closed", "CB-1", won.Mid, null, status: "won")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.closed", "CB-2", lost.Mid, null, status: "lost")).StatusCode);
        Assert.Equal(-2499L, Counter(Journal(f, $"sola:dispute-won:CB-1:{won.Id}"), LedgerAccount.PlatformSola));
        Assert.Equal(-(1500L + 2000 + 1200), (await Pay.Bal(f, s.Seller)).Available);       // the fee is kept on a won dispute, as for Stripe
        Assert.Equal(("sola", "won"), (Disputes(f, "CB-1").Single().Provider, Disputes(f, "CB-1").Single().Status));

        var list = await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes"));
        Assert.All(list["items"]!.AsArray(), i => { Assert.Equal("sola", (string)i!["provider"]!); Assert.False((bool)i["canRespond"]!); });
        // a fresh Sola dispute that needs a response: still no respond button, and the mail does not send the seller to Stripe
        var open = await SolaPaid(s);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-3", open.Mid, null, 1000, 1500, "needs_response")).StatusCode);
        var items = (await Pay.Ok(await s.Owner.Get("/api/v1/seller/payments/disputes")))["items"]!.AsArray();
        Assert.False((bool)items.Single(i => (long)i!["amountCents"]! == 1000)!["canRespond"]!);
        var mail = Pay.Mails(f, Pay.OwnerEmail(f, s.Seller)).Last(m => m.Subject.Contains("disputed") || m.Subject.Contains("dispute"));
        Assert.DoesNotContain("Stripe", mail.Text);
        AllJournalsBalance(f);
    }

    [Fact]
    public async Task Sola_dispute_on_a_multi_seller_order_takes_one_event_per_seller_order_with_the_same_rules()
    {
        using var f = new PayFactory();
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var mid = Orders.Mid();
        var body = Orders.OrderBody(f, mid, [Orders.Line(a.Seller, a.Brand, a.Product, 1, 2000, "LA"), Orders.Line(b.Seller, b.Brand, b.Product, 1, 3000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 400 }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 500 }]);
        Assert.Equal(HttpStatusCode.Created, (await Orders.Inbound(f, "/api/internal/v1/orders", body)).StatusCode);
        var na = f.WithDb(db => db.Orders.AsNoTracking().First(o => o.MarketplaceOrderId == mid && o.SellerId == a.Seller).Number);
        var nb = f.WithDb(db => db.Orders.AsNoTracking().First(o => o.MarketplaceOrderId == mid && o.SellerId == b.Seller).Number);
        var cap = await Pay.Ok(await SolaCapture(f, mid, null, "20000001"));           // no sellerOrderNumber: every seller order of the marketplace order
        Assert.Equal(2, cap["result"]!["results"]!.AsArray().Count);

        var noNumber = await SolaDispute(f, "dispute.created", "CB-M", mid, null, 2400, 1500);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "seller_order_required"), (noNumber.StatusCode, await Orders.Code(noNumber)));
        var tooMuch = await SolaDispute(f, "dispute.created", "CB-M", mid, na, 2401, 1500);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exceeds_sale"), (tooMuch.StatusCode, await Orders.Code(tooMuch)));
        var eur = await Pay.Event(f, "dispute.created", new { provider = "sola", disputeId = "CB-M", marketplaceOrderId = mid, sellerOrderNumber = na, amountCents = 100, currency = "EUR" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "currency_mismatch"), (eur.StatusCode, await Orders.Code(eur)));

        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-M", mid, na, 2400, 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-M", mid, nb, 3500, 1700)).StatusCode);
        Assert.Equal(2, Disputes(f, "CB-M").Count);
        Assert.Equal(-(2400L + 1500), (await Pay.Bal(f, a.Seller)).Available);
        Assert.Equal(-(3500L + 1700), (await Pay.Bal(f, b.Seller)).Available);
        // an update by id alone is ambiguous once there are two rows
        var byId = await Pay.Event(f, "dispute.updated", new { provider = "sola", disputeId = "CB-M", status = "under_review" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "seller_order_required"), (byId.StatusCode, await Orders.Code(byId)));
        // won for one seller order only credits that seller order
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.closed", "CB-M", mid, na, status: "won")).StatusCode);
        Assert.Equal(-1500L, (await Pay.Bal(f, a.Seller)).Available);
        Assert.Equal(-(3500L + 1700), (await Pay.Bal(f, b.Seller)).Available);
        // nothing of this ever created a Stripe unallocated item
        Assert.Equal(0, f.WithDb(db => db.UnallocatedDisputes.Count()));
        AllJournalsBalance(f);
    }

    // ---- idempotency -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_same_Sola_event_twice_and_a_resent_event_with_a_new_id_post_nothing_new()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        Assert.Equal("ok", (string)(await Pay.Ok(await SolaCapture(f, o.Mid, o.Number, "30000001", "sola-cap-30000001")))["status"]!);
        Assert.Equal("duplicate", (string)(await Pay.Ok(await SolaCapture(f, o.Mid, o.Number, "30000001", "sola-cap-30000001")))["status"]!);
        var resent = await Pay.Ok(await SolaCapture(f, o.Mid, o.Number, "30000001"));
        Assert.False((bool)resent["result"]!["results"]![0]!["recorded"]!);
        Assert.Equal(HttpStatusCode.OK, (await SolaRefund(f, o.Mid, o.Number, 500, "30000002", eventId: "sola-ref-1")).StatusCode);
        Assert.Equal("duplicate", (string)(await Pay.Ok(await SolaRefund(f, o.Mid, o.Number, 500, "30000002", eventId: "sola-ref-1")))["status"]!);
        Assert.False((bool)(await Pay.Ok(await SolaRefund(f, o.Mid, o.Number, 500, "30000002")))["result"]!["recorded"]!);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-I", o.Mid, null, 1000, 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-I", o.Mid, null, 1000, 1500)).StatusCode);
        Assert.Single(Pay.Journals(f, s.Seller, "sale")); Assert.Single(Pay.Journals(f, s.Seller, "refund"));
        Assert.Single(Pay.Journals(f, s.Seller, "dispute")); Assert.Single(Pay.Journals(f, s.Seller, "dispute_fee"));
        Assert.Single(Disputes(f, "CB-I"));
    }

    // ---- isolation from Stripe -------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_paymentRef_equal_to_a_Stripe_id_under_the_other_provider_never_cross_matches()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var stripe = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, stripe.Mid, stripe.Number, paymentRef: "ch_same")).StatusCode);
        var sola = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await SolaCapture(f, sola.Mid, sola.Number, "ch_same")).StatusCode);
        // a Sola chargeback with the same characters as the Stripe dispute id is another dispute
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "dp_same", sola.Mid, null, 1000, 1500)).StatusCode);

        // Stripe disputes charge ch_same: it maps to the STRIPE order only; the Sola row of the same id is untouched
        var r = await Pay.Hook(f, "charge.dispute.created", new { id = "dp_same", charge = "ch_same", amount = 2499, currency = "usd", status = "needs_response", reason = "fraudulent" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rows = Disputes(f, "dp_same");
        Assert.Equal(2, rows.Count);
        var stripeRow = rows.Single(x => x.Provider == "stripe"); var solaRow = rows.Single(x => x.Provider == "sola");
        Assert.Equal((stripe.Id, 2499L), (stripeRow.OrderId!.Value, stripeRow.AmountCents));
        Assert.Equal((sola.Id, 1000L, "needs_response"), (solaRow.OrderId!.Value, solaRow.AmountCents, solaRow.Status));
        Assert.Equal(2499L, Counter(Journal(f, $"dispute:dp_same:{stripe.Id}"), LedgerAccount.Platform));
        Assert.Equal(1000L, Counter(Journal(f, $"sola:dispute:dp_same:{sola.Id}"), LedgerAccount.PlatformSola));
        // Stripe closes it as lost: only the Stripe row changes
        Assert.Equal(HttpStatusCode.OK, (await Pay.Hook(f, "charge.dispute.closed", new { id = "dp_same", charge = "ch_same", amount = 2499, currency = "usd", status = "lost" })).StatusCode);
        rows = Disputes(f, "dp_same");
        Assert.Equal(("lost", "needs_response"), (rows.Single(x => x.Provider == "stripe").Status, rows.Single(x => x.Provider == "sola").Status));
        AllJournalsBalance(f);
    }

    [Fact]
    public async Task A_Stripe_charge_dispute_whose_reference_exists_only_as_Sola_does_nothing_and_raises_the_unattributed_flag()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await SolaPaid(s, "40000001");
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-40", o.Mid, null, 1000, 1500)).StatusCode);
        var journals = Pay.Journals(f, s.Seller).Count;
        foreach (var dp in new[] { "dp_unknown40", "CB-40" })   // a new Stripe id, and the Sola chargeback reference itself
        {
            var r = await Pay.Hook(f, "charge.dispute.created", new { id = dp, charge = "40000001", payment_intent = "40000001", amount = 2499, currency = "usd", status = "lost" });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.True(f.WithDb(db => db.ReconciliationFlags.Any(x => x.Kind == "dispute_unattributed" && x.ResolvedAt == null && x.Detail.Contains($"dispute {dp} "))), dp);
        }
        Assert.Equal(journals, Pay.Journals(f, s.Seller).Count);                           // no money moved
        var row = Disputes(f, "CB-40").Single();
        Assert.Equal(("sola", "needs_response", 1000L), (row.Provider, row.Status, row.AmountCents));
        Assert.Empty(Disputes(f, "dp_unknown40"));
    }

    // ---- validation --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Missing_provider_means_stripe_and_stripe_is_accepted_in_any_case()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var absent = await Pay.Paid(s);                                                     // the pre-C19 helper sends no provider
        var o = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "order", provider = " Stripe ", marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, paymentRef = "pi_x", currency = "USD" })).StatusCode);
        var n = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "payment.captured", new { kind = "order", provider = (string?)null, marketplaceOrderId = n.Mid, sellerOrderNumber = n.Number, paymentRef = "pi_y", currency = "USD" })).StatusCode);
        foreach (var id in new[] { absent.Id, o.Id, n.Id })
        {
            var j = Journal(f, $"sale:{id}");
            Assert.Equal(("stripe", -2499L, 0L), (j.Provider, Counter(j, LedgerAccount.Platform), Counter(j, LedgerAccount.PlatformSola)));
        }
        Assert.All(f.WithDb(db => db.PaymentReferences.AsNoTracking().ToList()), p => Assert.Equal("stripe", p.Provider));
        // a refund without provider on a Stripe sale: unchanged behaviour and key
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, absent.Mid, absent.Number, 500, refundId: "re_plain")).StatusCode);
        Assert.Equal(500L, Counter(Journal(f, "refund:re_plain"), LedgerAccount.Platform));
    }

    [Fact]
    public async Task Invalid_provider_bookings_with_Sola_and_paymentRef_limits_are_refused_and_store_nothing()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Delivered(s);
        foreach (var bad in new object[] { "paypal", "", 5, true })
        {
            var r = await Pay.Event(f, "payment.captured", new { kind = "order", provider = bad, marketplaceOrderId = o.Mid, sellerOrderNumber = o.Number, paymentRef = "1", currency = "USD" });
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_provider"), (r.StatusCode, await Orders.Code(r)));
        }
        foreach (var type in new[] { "refund.processed", "dispute.created" })
        {
            var r = await Pay.Event(f, type, new { provider = "square", refundId = "r1", disputeId = "d1", marketplaceOrderId = o.Mid, amountCents = 100 });
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_provider"), (r.StatusCode, await Orders.Code(r)));
        }
        foreach (var kind in new[] { "booking", "booking_cancellation_fee", "booking_no_show_fee", "booking_extra_charge" })
        {
            var r = await Pay.Event(f, "payment.captured", new { kind, provider = "sola", marketplaceBookingId = "MB-1", paymentRef = "5", currency = "USD", amountCents = 100 });
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "provider_not_supported"), (r.StatusCode, await Orders.Code(r)));
        }
        var rb = await Pay.Event(f, "refund.processed", new { provider = "sola", refundId = "r2", marketplaceBookingId = "MB-1", amountCents = 100 });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "provider_not_supported"), (rb.StatusCode, await Orders.Code(rb)));
        var db2 = await Pay.Event(f, "dispute.created", new { provider = "sola", disputeId = "d2", marketplaceBookingId = "MB-1", amountCents = 100 });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "provider_not_supported"), (db2.StatusCode, await Orders.Code(db2)));
        var noRef = await SolaCapture(f, o.Mid, o.Number, "");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_payment_ref"), (noRef.StatusCode, await Orders.Code(noRef)));
        var longRef = await SolaCapture(f, o.Mid, o.Number, new string('7', 101));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_payment_ref"), (longRef.StatusCode, await Orders.Code(longRef)));
        Assert.Empty(Pay.Journals(f, s.Seller));
        Assert.Equal(0, f.WithDb(db => db.PaymentReferences.Count()));
        Assert.Equal(0, f.WithDb(db => db.ProcessedPaymentEvents.Count(p => p.Source == "marketplace")));
        // 100 characters is the Sola limit; Stripe keeps its old limit of 120 (no Stripe event that worked before is refused)
        Assert.Equal(HttpStatusCode.OK, (await SolaCapture(f, o.Mid, o.Number, new string('7', 100))).StatusCode);
        var o2 = await Pay.Delivered(s);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, o2.Mid, o2.Number, paymentRef: "ch_" + new string('x', 110))).StatusCode);
    }

    [Fact]
    public async Task Refunds_disputes_and_captures_with_the_other_provider_than_the_sale_are_provider_mismatch()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var stripe = await Pay.Paid(s);
        var sola = await SolaPaid(s);
        var checks = new[]
        {
            await SolaRefund(f, stripe.Mid, stripe.Number, 100, "50000001"),
            await Pay.Refund(f, sola.Mid, sola.Number, 100, refundId: "re_5"),
            await SolaDispute(f, "dispute.created", "CB-5", stripe.Mid, null, 100, 0),
            await Pay.Event(f, "dispute.created", new { disputeId = "dp_5", marketplaceOrderId = sola.Mid, amountCents = 100 }),
            await SolaCapture(f, stripe.Mid, stripe.Number, "50000002"),
            await Pay.Capture(f, sola.Mid, sola.Number, paymentRef: "ch_5"),
        };
        foreach (var r in checks) Assert.Equal((HttpStatusCode.UnprocessableEntity, "provider_mismatch"), (r.StatusCode, await Orders.Code(r)));
        Assert.Equal(2, Pay.Journals(f, s.Seller).Count);                                   // the two sales, nothing else
        Assert.Empty(f.WithDb(db => db.PaymentDisputes.ToList()));
    }

    // ---- treasury ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Provider_funding_needs_super_admin_step_up_is_audited_idempotent_and_correctable_and_drives_the_unfunded_figure()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        await Pay.Onboard(s);
        await SolaPaid(s); await Pay.Paid(s);                                               // one Sola, one Stripe sale
        Pay.Age(f, 8); await Pay.Release(f);
        var body = new { direction = "sola_to_stripe", amountCents = 2000, currency = "USD", reference = "SOLA-BATCH-77", note = "Weekly Sola settlement move", requestId = "fund-1" };

        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly); var support = await StaffKit.LoginNew(f, StaffRole.Support); var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        foreach (var api in new[] { ro, support, mod })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"{Admin}/sola/funding", body)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Get($"{Admin}/sola/treasury")).StatusCode);
        }
        await Pay.Relog(s);                                                                 // the clock moved 8 days: sign the owner in again
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Get($"{Admin}/sola/treasury")).StatusCode);
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, email);
        var noStep = await sa.Post($"{Admin}/sola/funding", body);
        Assert.Equal((HttpStatusCode.Forbidden, "step_up_required"), (noStep.StatusCode, await StaffKit.Err(noStep)));
        await StaffKit.StepUp(f, sa, email);

        // only the Sola sale counts: 2299 released, nothing funded yet
        var usd = (await Pay.Ok(await sa.Get($"{Admin}/sola/treasury")))["currencies"]!.AsArray().Single()!;
        Assert.Equal((2299L, 0L, 2299L), ((long)usd["releasedCents"]!, (long)usd["fundedCents"]!, (long)usd["unfundedCents"]!));

        // validation
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"{Admin}/sola/funding", body with { direction = "sideways" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"{Admin}/sola/funding", body with { amountCents = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"{Admin}/sola/funding", body with { currency = "usd" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"{Admin}/sola/funding", body with { reference = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"{Admin}/sola/funding", body with { requestId = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Post($"{Admin}/sola/funding", body with { note = "x" })).StatusCode);
        Assert.Empty(f.WithDb(db => db.LedgerJournals.Where(j => j.Type == "provider_funding").ToList()));

        var first = await Pay.Ok(await sa.Post($"{Admin}/sola/funding", body));
        Assert.Equal((true, 2000L, 299L), ((bool)first["recorded"]!, (long)first["fundedCents"]!, (long)first["unfundedCents"]!));
        var again = await Pay.Ok(await sa.Post($"{Admin}/sola/funding", body));           // the same request: nothing new
        Assert.False((bool)again["recorded"]!);
        var reused = await sa.Post($"{Admin}/sola/funding", body with { amountCents = 999 });
        Assert.Equal((HttpStatusCode.Conflict, "request_id_reused"), (reused.StatusCode, await StaffKit.Err(reused)));
        var j = Journal(f, "provider-funding:fund-1");
        Assert.Equal((-2000L, 2000L, Guid.Empty, "SOLA-BATCH-77"), (Counter(j, LedgerAccount.Platform), Counter(j, LedgerAccount.PlatformSola), j.SellerId, j.SourceRef));
        Assert.Equal(("Weekly Sola settlement move", StaffKit.UserId(f, email)), (j.Reason, j.ActorUserId));
        var audit = f.WithDb(db => db.AuditEvents.Single(a => a.Type == "admin.payments.provider_funding"));
        Assert.Equal("Weekly Sola settlement move", audit.Reason);

        // a correction back to Sola; never more than was funded
        var tooBig = await sa.Post($"{Admin}/sola/funding", new { direction = "stripe_to_sola", amountCents = 2001, currency = "USD", reference = "FIX-1", note = "Typo in the batch amount", requestId = "fund-2" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "exceeds_funded"), (tooBig.StatusCode, await StaffKit.Err(tooBig)));
        var fix = await Pay.Ok(await sa.Post($"{Admin}/sola/funding", new { direction = "stripe_to_sola", amountCents = 500, currency = "USD", reference = "FIX-1", note = "Typo in the batch amount", requestId = "fund-3" }));
        Assert.Equal((1500L, 799L), ((long)fix["fundedCents"]!, (long)fix["unfundedCents"]!));
        var view = await Pay.Ok(await ro.Get($"{Admin}/sola/treasury"));
        Assert.Equal(new long[] { -500, 2000 }, view["fundings"]!["items"]!.AsArray().Select(x => (long)x!["amountCents"]!).Order().ToArray());
        // the funding journals never appear in any seller's ledger, and every journal balances
        Assert.DoesNotContain(Pay.Journals(f, s.Seller), x => x.Type == "provider_funding");
        AllJournalsBalance(f);
        Assert.Equal(0, (await Pay.Reconcile(f)).Flagged);
    }

    [Fact]
    public async Task The_hold_switch_off_never_holds_transfers_on_holds_them_until_the_Sola_money_is_funded()
    {
        using (var off = new PayFactory())
        {
            var s = await Orders.NewShop(off);
            await Pay.Onboard(s); await SolaPaid(s);
            Pay.Age(off, 8); await Pay.Release(off);
            Assert.Equal(1, (await Pay.Transfers(off)).Created);
        }
        using var f = new SolaHoldFactory();
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        await Pay.Onboard(a); await Pay.Onboard(b);
        await SolaPaid(a); await Pay.Paid(b);                                               // b only sold through Stripe: the hold is per currency, so b waits too
        Pay.Age(f, 8); await Pay.Release(f);
        var r = await Pay.Transfers(f);
        Assert.Equal((0, 2), (r.Created, r.Paused));
        Assert.Equal(2299L, (await Pay.Bal(f, a.Seller)).Available);                          // nothing lost: the funds stay available
        var view = await Pay.Ok(await (await StaffKit.LoginNew(f, StaffRole.ReadOnly)).Get($"{Admin}/sola/treasury"));
        Assert.True((bool)view["holdTransfersWhenUnfunded"]!);
        Assert.True((bool)view["currencies"]![0]!["transfersHeld"]!);
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"{Admin}/sola/funding", new { amountCents = 2299, currency = "USD", reference = "SOLA-1", note = "Move for the first phone order", requestId = "h-1" })).StatusCode);
        Assert.Equal(2, (await Pay.Transfers(f)).Created);
        Assert.Equal((2299L, 2299L), ((await Pay.Bal(f, a.Seller)).Settled, (await Pay.Bal(f, b.Seller)).Settled));
    }

    private static Task<List<SolaTreasuryLine>> Treasury(ApiFactory f) => Pay.Run(f, sp => sp.GetRequiredService<SolaTreasuryService>().LinesAsync());
    private static async Task<(long Released, long Unfunded)> Usd(ApiFactory f) { var l = (await Treasury(f)).Single(x => x.Currency == "USD"); return (l.ReleasedCents, l.UnfundedCents); }

    [Fact]
    public async Task The_unfunded_figure_follows_Sola_refunds_lost_and_won_chargebacks_after_release_and_fundings()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var refunded = await SolaPaid(s); var lost = await SolaPaid(s); var won = await SolaPaid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        Assert.Equal((3 * 2299L, 3 * 2299L), await Usd(f));                                // three releases, nothing funded

        // a partial Sola refund after release: 1000 items, 100 fee back, 900 taken from Available
        Assert.Equal(HttpStatusCode.OK, (await SolaRefund(f, refunded.Mid, refunded.Number, 1000, "60000001")).StatusCode);
        Assert.Equal((6897L - 900, 6897L - 900), await Usd(f));
        // a lost chargeback: amount + fee come out of Available
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-60L", lost.Mid, null, 1000, 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.closed", "CB-60L", lost.Mid, null, status: "lost")).StatusCode);
        Assert.Equal((5997L - 2500, 5997L - 2500), await Usd(f));
        // a won chargeback: the amount comes back, the fee stays taken
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "CB-60W", won.Mid, null, 1000, 1500)).StatusCode);
        Assert.Equal((3497L - 2500, 3497L - 2500), await Usd(f));
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.closed", "CB-60W", won.Mid, null, status: "won")).StatusCode);
        Assert.Equal((997L + 1000, 1997L), await Usd(f));

        // a funding takes the unfunded figure down; the released figure stays
        await Pay.Run(f, async sp =>
        {
            Assert.NotNull(await sp.GetRequiredService<SolaTreasuryService>().AddFundingAsync("sola_to_stripe", 1500, "USD", "SOLA-60", "Part of the week", "f-60", Guid.NewGuid()));
            return await sp.GetRequiredService<LedgerService>().SaveAsync();
        });
        Assert.Equal((1997L, 497L), await Usd(f));
        AllJournalsBalance(f);
    }

    [Fact]
    public async Task The_hold_switch_holds_only_the_unfunded_currency_a_EUR_seller_is_still_paid()
    {
        using var f = new SolaHoldFactory();
        var usd = await Orders.NewShop(f); var eur = await Orders.NewShop(f);
        await Pay.Onboard(usd); await Pay.Onboard(eur);
        await SolaPaid(usd);
        Pay.Age(f, 8); await Pay.Release(f);
        // the EUR seller has an available EUR balance (a staff credit), no Sola money in EUR
        await Pay.Run(f, async sp =>
        {
            var ledger = sp.GetRequiredService<LedgerService>();
            await ledger.PostAsync(eur.Seller, "EUR", "adjustment", "adjustment:eur-60", "Adjustment by Solvelt (credit)",
                [new(LedgerAccount.Available, 3000, "adjustment"), new(LedgerAccount.Platform, -3000, "adjustment")]);
            return await ledger.SaveAsync();
        });
        Assert.True((await Treasury(f)).Single(l => l.Currency == "USD").TransfersHeld);
        Assert.DoesNotContain(await Treasury(f), l => l.Currency == "EUR");
        var r = await Pay.Transfers(f);
        Assert.Equal((1, 1), (r.Created, r.Paused));
        var t = Assert.Single(f.WithDb(db => db.PaymentTransfers.AsNoTracking().ToList()));
        Assert.Equal((eur.Seller, "EUR", 3000L), (t.SellerId, t.Currency, t.AmountCents));
        Assert.Equal(2299L, (await Pay.Bal(f, usd.Seller)).Available);                       // the USD seller waits, nothing lost
    }

    // ---- reconciliation ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reconciliation_checks_against_Stripe_skip_Sola_dispute_rows_and_every_journal_sums_to_zero()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var sola = await SolaPaid(s);
        var stripe = await Pay.Paid(s);
        Assert.Equal(HttpStatusCode.OK, (await SolaDispute(f, "dispute.created", "dp_rc", sola.Mid, null, 1000, 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Event(f, "dispute.created", new { disputeId = "dp_rc", marketplaceOrderId = stripe.Mid, amountCents = 1000, feeCents = 1500 })).StatusCode);
        // both rows say "won" a day ago with no credit posted (what a Stripe multi-seller won looks like before the marketplace allocates it)
        var old = f.Time.GetUtcNow().UtcDateTime.AddHours(-30);
        f.WithDb(db => db.PaymentDisputes.Where(d => d.ExternalId == "dp_rc").ExecuteUpdate(u => u.SetProperty(d => d.Status, "won").SetProperty(d => d.ClosedAt, old)));
        await Pay.Reconcile(f);
        var flags = f.WithDb(db => db.ReconciliationFlags.AsNoTracking().Where(x => x.Kind == "dispute_won_credit_missing").ToList());
        var flag = Assert.Single(flags);                                                    // the Stripe row only
        Assert.Contains(stripe.Id.ToString(), flag.Detail);
        Assert.DoesNotContain(sola.Id.ToString(), flag.Detail);
        Assert.Equal(0, f.WithDb(db => db.ReconciliationFlags.Count(x => x.Kind == "unbalanced_journal")));
        AllJournalsBalance(f);
    }

    // ---- demo -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_demo_fill_has_one_Sola_paid_order()
    {
        using var f = new DemoFactory();
        var (api, seller) = await ProdApi.Approved(f);
        var r = await api.Post("/api/v1/seller/demo-data");
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync() + string.Join("\n", f.Errors));
        var sales = Pay.Journals(f, seller, "sale");
        var sola = Assert.Single(sales, j => j.Provider == "sola");
        Assert.True(Counter(sola, LedgerAccount.PlatformSola) < 0);
        Assert.Contains(sales, j => j.Provider == "stripe");
        Assert.Equal(HttpStatusCode.OK, (await api.Delete("/api/v1/seller/demo-data")).StatusCode);
        Assert.Empty(Pay.Journals(f, seller));
    }
}
