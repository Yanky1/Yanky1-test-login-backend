using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Offers;
using static Solvelt.Api.Tests.OfferKit;

namespace Solvelt.Api.Tests;

/// <summary>C18 pure money rules (no I/O).</summary>
public class OfferPricingTests
{
    private static readonly List<OfferTier> Tiers = [new(10, 95), new(20, 90), new(50, 80)];

    [Fact]
    public void The_highest_step_reached_applies_and_below_the_first_step_there_is_none()
    {
        Assert.Null(OfferPricing.TierFor(Tiers, 9));
        Assert.Equal(95, OfferPricing.TierFor(Tiers, 10)!.UnitPriceCents);
        Assert.Equal(95, OfferPricing.TierFor(Tiers, 19)!.UnitPriceCents);
        Assert.Equal(90, OfferPricing.TierFor(Tiers, 20)!.UnitPriceCents);
        Assert.Equal(80, OfferPricing.TierFor(Tiers, 500)!.UnitPriceCents);
    }

    [Fact]
    public void A_tier_never_raises_a_price_and_a_lower_sale_price_wins()
    {
        var t = new OfferTier(20, 90);
        Assert.Equal(200, OfferPricing.LineDiscount(100, 20, t));   // 20 x ($1.00 - $0.90)
        Assert.Equal(0, OfferPricing.LineDiscount(85, 20, t));      // a $0.85 sale price is below the tier: no discount, never negative
        Assert.Equal(0, OfferPricing.LineDiscount(90, 20, t));
        Assert.Equal(0, OfferPricing.LineDiscount(100, 20, null));
    }

    [Fact]
    public void Gifts_are_earned_once_at_their_minimum_and_money_formats_in_dollars()
    {
        var g = new List<OfferGift> { new(10, Guid.NewGuid(), 1), new(30, Guid.NewGuid(), 2) };
        Assert.Empty(OfferPricing.GiftsFor(g, 9));
        Assert.Single(OfferPricing.GiftsFor(g, 29));
        Assert.Equal(2, OfferPricing.GiftsFor(g, 300).Count);                 // not stacked: still one entry per gift
        Assert.Equal("$18.00", OfferPricing.Money(1800));
        Assert.Equal("$22.50", OfferPricing.Money(2250));
        Assert.Equal("$1,234.05", OfferPricing.Money(123405));
    }
}

/// <summary>The acceptance list of issue #101, end to end through the seller API and the signed order intake.</summary>
public class OfferAcceptanceTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Twenty_mixed_cards_from_one_seller_total_18_dollars_with_the_gifts_at_zero()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var (id, entry) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 12, discount: 120, offer: offer), Line(s, s.CardB, 8, discount: 80, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        var d = await Orders.Detail(s.Owner, id);
        var m = d["money"]!;
        Assert.Equal(2000, (long)m["itemsCents"]!); Assert.Equal(200, (long)m["discountCents"]!);
        Assert.Equal(1800, (long)m["totalCents"]!);                                   // $18.00 before shipping (no shipping, no tax here)
        Assert.Equal(1800, (long)entry["totalCents"]!);
        Assert.Equal(1, (int)LineOf(d, s.CardA)["offerVersion"]!); Assert.Equal(offer.ToString(), (string)LineOf(d, s.CardA)["offerId"]!);
        Assert.Equal(0, (long)LineOf(d, s.Wallet, gift: true)["unitPriceCents"]!);
    }

    [Fact]
    public async Task Twenty_five_cards_total_22_50()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var (id, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 15, discount: 150, offer: offer), Line(s, s.CardB, 10, discount: 100, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        var m = (await Orders.Detail(s.Owner, id))["money"]!;
        Assert.Equal(2250, (long)m["itemsCents"]! - (long)m["discountCents"]!);
        Assert.Equal(2250, (long)m["totalCents"]!);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(19)]
    public async Task Ten_to_nineteen_cards_include_zero_dollar_gift_lines_the_seller_sees_on_the_order_and_the_packing_slip(int cards)
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var (id, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, cards - 3, offer: offer), Line(s, s.CardB, 3, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        var d = await Orders.Detail(s.Owner, id);
        foreach (var gift in new[] { s.Wallet, s.Pack })
        {
            var l = LineOf(d, gift, gift: true);
            Assert.Equal(0, (long)l["unitPriceCents"]!); Assert.Equal(1, (int)l["quantity"]!); Assert.Equal(0, (long)l["lineTotalCents"]!);
            Assert.Equal(offer.ToString(), (string)l["giftOfOfferId"]!);
        }
        Assert.Equal(cards * 100, (long)d["money"]!["totalCents"]!);                    // below 20 cards: full price, the gifts add nothing
        Assert.False((bool)d["giftUnavailable"]!);
        var slip = await Orders.Node(await s.Owner.Get($"/api/v1/seller/orders/{id}/packing-slip"));
        Assert.Equal(2, slip["lines"]!.AsArray().Count(l => (bool)l!["isGift"]!));
        Assert.Contains(slip["lines"]!.AsArray(), l => (bool)l!["isGift"]! && (string)l["title"]! == "Card wallet");
    }

    [Fact]
    public async Task Cards_of_another_seller_do_not_count_toward_the_tiers()
    {
        var s = await Shop(f);
        var other = await Shop(f);
        var offer = await Standard(s);
        // the marketplace counted the other seller's 10 cards for this seller's tier: both problems are reported, nothing is created
        var mid = Orders.Mid();
        var n = await Mismatch(f, Order(f, Join([Line(s, s.CardA, 10, discount: 100, offer: offer), Line(other, other.CardA, 10, discount: 100, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)], mid), "not_eligible", mid);
        var errs = n["errors"]!.AsArray();
        Assert.Contains(errs, e => (string)e!["kind"]! == "not_eligible" && (int)e["index"]! == 1);
        Assert.Contains(errs, e => (string)e!["kind"]! == "discount" && (int)e["index"]! == 0 && (long)e["expected"]!["discountCents"]! == 0);
        Assert.Equal(10, (int)n["expected"]!["offers"]![0]!["eligibleUnits"]!);
        // priced correctly: 10 cards of this seller earn the gifts but no tier, the other seller's cards are plain
        var r = await Send(f, Order(f, Join([Line(s, s.CardA, 10, offer: offer), Line(other, other.CardA, 10)], Gifts(s, offer)), [Ref(offer, 1)]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var orders = (await Orders.Node(r))["orders"]!.AsArray();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.Equal(1000, (long)o!["totalCents"]!));
    }

    [Fact]
    public async Task Partial_refunds_and_the_fee_use_the_discounted_amounts_and_a_gift_refunds_zero()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var (id, entry) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        Assert.Equal(180, (long)entry["feeCents"]!);                                     // 10% of $18.00, not of $20.00
        Assert.Equal(1620, (long)entry["netCents"]!);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship()));
        var d = await Orders.Detail(s.Owner, id);
        var card = Guid.Parse((string)LineOf(d, s.CardA)["id"]!);
        var wallet = Guid.Parse((string)LineOf(d, s.Wallet, gift: true)["id"]!);
        // 5 of the 20 cards come back: 5 x $0.90. The 15 kept cards are NOT re-priced at $1.00 (the buyer keeps the price paid)
        var pv = await Orders.Ok(s.Owner.Post($"/api/v1/seller/orders/{id}/refunds/preview", new { lines = new[] { new { lineId = card, quantity = 5 }, new { lineId = wallet, quantity = 1 } }, reason = "goodwill" }));
        Assert.Equal(450, (long)pv["amountCents"]!);
        Assert.Equal(0, (long)pv["lines"]!.AsArray().Single(l => (string)l!["lineId"]! == wallet.ToString())!["amountCents"]!);
        await Orders.Ok(s.Owner.Post($"/api/v1/seller/orders/{id}/refunds", new { lines = new[] { new { lineId = card, quantity = 5 } }, reason = "goodwill" }));
        var rest = await Orders.Ok(s.Owner.Post($"/api/v1/seller/orders/{id}/refunds/preview", new { lines = new[] { new { lineId = card, quantity = 15 } }, reason = "goodwill" }));
        Assert.Equal(1350, (long)rest["amountCents"]!);                                  // 450 + 1350 = exactly the $18.00 paid
    }
}

/// <summary>C18 seller API validation.</summary>
public class OfferValidationTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static async Task<JsonNode> Bad(CardShop s, object body, string field)
    {
        var r = await s.Owner.Post(Path, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{(int)r.StatusCode} {text}");
        var n = JsonNode.Parse(text)!;
        Assert.NotNull(n["errors"]![field]);
        return n;
    }

    [Fact]
    public async Task Tiers_need_increasing_quantities_decreasing_prices_and_a_price_above_zero()
    {
        var s = await Shop(f);
        await Bad(s, Write(s, tiers: [Tier(10, 90), Tier(20, 95)]), "tiers");          // more cards must not cost more each
        await Bad(s, Write(s, tiers: [Tier(10, 90), Tier(20, 90)]), "tiers");          // strictly decreasing
        await Bad(s, Write(s, tiers: [Tier(10, 90), Tier(10, 80)]), "tiers");          // strictly increasing quantities
        await Bad(s, Write(s, tiers: [Tier(10, 0)]), "tiers");
        await Bad(s, Write(s, tiers: [Tier(0, 50)]), "tiers");
        await Bad(s, Write(s, tiers: [], gifts: []), "tiers");                           // an offer must offer something
        Assert.Equal(HttpStatusCode.Created, (await s.Owner.Post(Path, Write(s, tiers: [Tier(20, 90), Tier(10, 95)]))).StatusCode);   // order of the input does not matter
    }

    [Fact]
    public async Task At_most_ten_tiers_and_five_gifts()
    {
        var s = await Shop(f);
        await Bad(s, Write(s, tiers: Enumerable.Range(1, 11).Select(i => Tier(i * 10, 200 - i)).ToArray()), "tiers");
        var six = new List<object>();
        for (var i = 0; i < 6; i++) six.Add(Gift(10, await Product(s.Owner, s.Brand, $"Gift {i}", 1.00)));
        await Bad(s, Write(s, gifts: six.ToArray()), "gifts");
        Assert.Equal(HttpStatusCode.Created, (await s.Owner.Post(Path, Write(s, name: "Ten steps", tiers: Enumerable.Range(1, 10).Select(i => Tier(i * 10, 200 - i)).ToArray(), gifts: six.Take(5).ToArray(), status: "draft"))).StatusCode);
    }

    [Fact]
    public async Task A_gift_or_a_scope_product_of_another_seller_is_rejected()
    {
        var s = await Shop(f);
        var other = await Shop(f);
        await Bad(s, Write(s, gifts: [Gift(10, other.Wallet)]), "gifts");
        await Bad(s, Write(s, products: [s.CardA, other.CardA]), "productIds");
        await Bad(s, Write(s, gifts: [Gift(10, s.Wallet), Gift(5, s.Wallet)]), "gifts");                 // the same gift twice
        await Bad(s, Write(s, gifts: [Gift(10, s.Wallet, 11)]), "gifts");
        // an inactive product is not a gift
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Pack}/status", new { status = "inactive" })).StatusCode);
        await Bad(s, Write(s, gifts: [Gift(10, s.Pack)]), "gifts");
        Assert.Equal(0, f.WithDb(db => db.Offers.Count(o => o.SellerId == s.Seller)));
    }
}

/// <summary>C18 order intake: recomputation from the offer version, one 422 per mismatch kind, gift stock.</summary>
public class OfferIntakeTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task A_correct_order_is_accepted_and_reserves_the_gift_stock_which_shipping_then_takes_off()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var before = Stock(f, s.Wallet);
        var (id, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        Assert.Equal(before.Reserved + 1, Stock(f, s.Wallet).Reserved);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship()));
        Assert.Equal((before.Stock - 1, before.Reserved), Stock(f, s.Wallet));                     // the gift counts against the wallet's stock
    }

    [Fact]
    public async Task Each_mismatch_kind_is_a_422_offer_mismatch_and_creates_or_reserves_nothing()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var walletBefore = Stock(f, s.Wallet);
        var cards = Stock(f, s.CardA);
        async Task Check(string kind, object[] lines, object[]? offers)
        {
            var mid = Orders.Mid();
            await Mismatch(f, Order(f, lines, offers, mid), kind, mid);
        }
        // a wrong discount (the marketplace applied the tier to 19 cards); the body names the expected figure
        var mid1 = Orders.Mid();
        var n = await Mismatch(f, Order(f, Join([Line(s, s.CardA, 19, discount: 190, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)], mid1), "discount", mid1);
        var exp = n["expected"]!["offers"]![0]!;
        Assert.Equal(19, (int)exp["eligibleUnits"]!); Assert.Null(exp["tier"]); Assert.Equal(0, (long)exp["lines"]![0]!["discountCents"]!);
        Assert.Equal(2, exp["gifts"]!.AsArray().Count);
        await Check("discount", Join([Line(s, s.CardA, 20, discount: 150, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]);
        // a gift not earned (9 cards), too many of a gift, a gift with a price
        await Check("gift_not_earned", Join([Line(s, s.CardA, 9, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]);
        await Check("gift_not_earned", [Line(s, s.CardA, 10, offer: offer), GiftLine(s, s.Wallet, offer, qty: 2), GiftLine(s, s.Pack, offer)], [Ref(offer, 1)]);
        await Check("gift_price", [Line(s, s.CardA, 10, offer: offer), GiftLine(s, s.Wallet, offer, price: 400), GiftLine(s, s.Pack, offer)], [Ref(offer, 1)]);
        // an earned gift left out without giftUnavailable
        await Check("gift_missing", [Line(s, s.CardA, 10, offer: offer), GiftLine(s, s.Pack, offer)], [Ref(offer, 1)]);
        // a version that never existed / was not active
        await Check("version_not_active", Join([Line(s, s.CardA, 10, offer: offer)], Gifts(s, offer)), [Ref(offer, 7)]);
        // the order ignores an offer that is active (and was for the whole grace window: it was created before the window started)
        f.Time.Advance(TimeSpan.FromMinutes(20));
        await Check("offer_missing", [Line(s, s.CardA, 20)], null);
        // a line names an offer that is not declared, or an unknown offer
        await Check("unknown_offer", Join([Line(s, s.CardA, 10, offer: Guid.NewGuid())], Gifts(s, offer)), [Ref(offer, 1)]);
        await Check("unknown_offer", [Line(s, s.CardA, 10)], [Ref(Guid.NewGuid(), 1)]);
        // a product outside the scope claims the offer
        await Check("not_eligible", Join([Line(s, s.CardA, 10, offer: offer), Line(s, s.Wallet, 1, price: 400, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]);
        Assert.Equal(walletBefore, Stock(f, s.Wallet));
        Assert.Equal(cards, Stock(f, s.CardA));
    }

    [Fact]
    public async Task A_gift_out_of_stock_at_intake_is_left_out_and_flagged_not_rejected()
    {
        var s = await Shop(f, giftStock: 1);
        var offer = await Standard(s);
        await Accepted(f, Order(f, Join([Line(s, s.CardA, 10, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));   // takes the last wallet and the last pack
        var (id, entry) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 10, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        Assert.True((bool)entry["giftUnavailable"]!);
        Assert.False((bool)entry["oversold"]!);
        var d = await Orders.Detail(s.Owner, id);
        Assert.True((bool)d["giftUnavailable"]!);
        Assert.DoesNotContain(d["lines"]!.AsArray(), l => (bool)l!["isGift"]!);
        Assert.Equal(1000, (long)d["money"]!["totalCents"]!);
        Assert.Equal(1, f.WithDb(db => db.OrderEvents.Count(e => e.OrderId == id && e.Type == "order.gift_unavailable")));
        var slip = await Orders.Node(await s.Owner.Get($"/api/v1/seller/orders/{id}/packing-slip"));
        Assert.True((bool)slip["giftUnavailable"]!);
        Assert.Equal((1, 1), Stock(f, s.Wallet));                                                 // never oversold
    }

    [Fact]
    public async Task The_marketplace_can_say_the_gift_was_unavailable_and_the_order_records_it()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        var (id, entry) = await Accepted(f, Order(f, [Line(s, s.CardA, 12, offer: offer)], [Ref(offer, 1, giftUnavailable: true)]));
        Assert.True((bool)entry["giftUnavailable"]!);
        Assert.True((bool)(await Orders.Detail(s.Owner, id))["giftUnavailable"]!);
    }

    [Fact]
    public async Task A_tier_never_raises_a_price_and_a_sale_price_below_the_tier_wins()
    {
        var s = await Shop(f);
        var cheap = await Product(s.Owner, s.Brand, "Plastic prayer card, clearance", 0.80);
        var onSale = await Product(s.Owner, s.Brand, "Plastic prayer card, on sale", 1.00, sale: 0.85);
        var offer = Guid.Parse((string)(await Create(s, Write(s, products: [s.CardA, cheap, onSale])))["id"]!);
        // 20 eligible units: the $1.00 card gets $0.10 off each, the $0.80 card and the $0.85 sale card stay at their own lower price
        var mid = Orders.Mid();
        await Mismatch(f, Order(f, Join([Line(s, s.CardA, 10, discount: 100, offer: offer), Line(s, cheap, 5, price: 80, offer: offer), Line(s, onSale, 5, price: 85, discount: 25, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)], mid), "discount", mid);
        var (id, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 10, discount: 100, offer: offer), Line(s, cheap, 5, price: 80, offer: offer), Line(s, onSale, 5, price: 85, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        var m = (await Orders.Detail(s.Owner, id))["money"]!;
        Assert.Equal(900 + 400 + 425, (long)m["totalCents"]!);
        // the preview quotes the sale price for the sale card, never the higher tier price
        var pv = await Orders.Ok(s.Owner.Post($"{Path}/preview", new { scope = "products", productIds = new[] { onSale }, tiers = new[] { Tier(20, 90) }, gifts = Array.Empty<object>(), sampleProductId = onSale, quantities = new[] { 20 } }));
        Assert.Equal(85, (long)pv["rows"]![0]!["unitPriceCents"]!);
        Assert.Equal(1700, (long)pv["rows"]![0]!["totalCents"]!);
    }

    [Fact]
    public async Task The_most_specific_offer_prices_a_line_and_offers_do_not_stack()
    {
        var s = await Shop(f);
        var wide = Guid.Parse((string)(await Create(s, Write(s, scope: "all", products: [], gifts: [], tiers: [Tier(5, 95)], name: "Everything")))["id"]!);
        var cards = await Standard(s);
        // the cards belong to the product-scoped offer; the wallet (no card offer) falls back to the all-products offer
        var mid = Orders.Mid();
        await Mismatch(f, Order(f, Join([Line(s, s.CardA, 10, discount: 50, offer: wide)], Gifts(s, cards)), [Ref(wide, 1), Ref(cards, 1)], mid), "not_eligible", mid);
        var (id, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 10, offer: cards), Line(s, s.Wallet, 5, price: 400, discount: 1525, offer: wide)], Gifts(s, cards)), [Ref(wide, 1), Ref(cards, 1)]));
        var d = await Orders.Detail(s.Owner, id);
        Assert.Equal(wide.ToString(), (string)LineOf(d, s.Wallet)["offerId"]!);
        Assert.Equal(cards.ToString(), (string)LineOf(d, s.CardA)["offerId"]!);
    }
}

public class OfferGraceFactory : OrdersFactory { }

/// <summary>C18 version grace window and versioning of orders (own fixture: these tests move the clock).</summary>
public class OfferGraceTests(OfferGraceFactory f) : IClassFixture<OfferGraceFactory>
{
    [Fact]
    public async Task An_order_priced_with_the_previous_version_is_accepted_for_15_minutes_after_an_edit_and_keeps_that_version()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        f.Time.Advance(TimeSpan.FromMinutes(30));
        var v2 = await Update(s, offer, Write(s, tiers: [Tier(20, 85)], version: 1));
        Assert.Equal(2, (int)v2["version"]!);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        // a cart priced with version 1 just before the edit still goes through, and the order keeps version 1
        var (old, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));
        var (cur, _) = await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 300, offer: offer)], Gifts(s, offer)), [Ref(offer, 2)]));
        f.Time.Advance(TimeSpan.FromMinutes(11));                                                     // 16 minutes after the edit
        var mid = Orders.Mid();
        var n = await Mismatch(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)], mid), "version_not_active", mid);
        Assert.Contains(n["errors"]!.AsArray(), e => (string)e!["kind"]! == "version_not_active" && e["expected"]!["offerVersions"]!.AsArray().Select(x => (int)x!).SequenceEqual(new[] { 2 }));
        await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 300, offer: offer)], Gifts(s, offer)), [Ref(offer, 2)]));
        // an edit and an end later, the first order still names version 1
        await Update(s, offer, Write(s, tiers: [Tier(20, 80)], version: 2));
        await Orders.Ok(s.Owner.Post($"{Path}/{offer}/end", new { version = 3 }));
        Assert.Equal(1, (int)LineOf(await Orders.Detail(s.Owner, old), s.CardA)["offerVersion"]!);
        Assert.Equal(2, (int)LineOf(await Orders.Detail(s.Owner, cur), s.CardA)["offerVersion"]!);
        Assert.Equal(1, f.WithDb(db => db.OrderLines.AsNoTracking().Single(l => l.OrderId == old && l.ProductId == s.CardA).OfferVersion));
    }

    [Fact]
    public async Task A_new_offer_is_optional_during_the_grace_window_then_required_and_an_ended_one_stops_applying()
    {
        var s = await Shop(f);
        var offer = await Standard(s);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        // the cart was priced before the offer existed: accepted without it
        await Accepted(f, Order(f, [Line(s, s.CardA, 20)], null));
        f.Time.Advance(TimeSpan.FromMinutes(11));
        var mid = Orders.Mid();
        await Mismatch(f, Order(f, [Line(s, s.CardA, 20)], null, mid), "offer_missing", mid);
        await Orders.Ok(s.Owner.Post($"{Path}/{offer}/end", new { version = 1 }));
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await Accepted(f, Order(f, [Line(s, s.CardA, 20)], null));                                    // ended: no longer required
        await Accepted(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)]));   // ... but still honoured within the grace
        f.Time.Advance(TimeSpan.FromMinutes(15));
        var mid2 = Orders.Mid();
        await Mismatch(f, Order(f, Join([Line(s, s.CardA, 20, discount: 200, offer: offer)], Gifts(s, offer)), [Ref(offer, 1)], mid2), "version_not_active", mid2);
    }
}

/// <summary>C18 seller API: roles, IDOR, audit log, versions.</summary>
public class OfferSellerApiTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Owner_admin_and_manager_can_write_viewers_can_only_read()
    {
        var s = await Shop(f);
        foreach (var role in new[] { "Admin", "Manager" })
        {
            var (m, _) = await TeamHelpers.JoinAs(f, s.Owner, role);
            await m.Switch(s.Seller);
            var created = await Create(s, Write(s, status: "draft", name: role + " draft"), m);
            var id = (string)created["id"]!;
            Assert.Equal(HttpStatusCode.OK, (await m.Put($"{Path}/{id}", Write(s, status: "draft", name: role + " edit", version: 1))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await m.Post($"{Path}/{id}/end", new { version = 2 })).StatusCode);
        }
        var draft = await Create(s, Write(s, status: "draft"));
        var did = (string)draft["id"]!;
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        await viewer.Switch(s.Seller);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"{Path}/{did}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Post($"{Path}/preview", Write(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post(Path, Write(s, status: "draft"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Put($"{Path}/{did}", Write(s, status: "draft", version: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"{Path}/{did}/end", new { version = 1 })).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.Offers.Single(o => o.Id == Guid.Parse(did)).CurrentVersion));
    }

    [Fact]
    public async Task Another_accounts_offer_is_404_everywhere()
    {
        var s = await Shop(f);
        var other = await Shop(f);
        var id = await Standard(s);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Get($"{Path}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Put($"{Path}/{id}", Write(other, version: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Post($"{Path}/{id}/end", new { version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Post($"{Path}/preview", new { brandId = s.Brand, scope = "all", tiers = new[] { Tier(5, 50) } })).StatusCode);
        Assert.DoesNotContain((await Orders.Node(await other.Owner.Get(Path)))["items"]!.AsArray(), o => (string)o!["id"]! == id.ToString());
        Assert.Equal(1, f.WithDb(db => db.Offers.Single(o => o.Id == id).CurrentVersion));
        Assert.Equal(OfferStatus.Active, f.WithDb(db => db.Offers.Single(o => o.Id == id).Status));
    }

    [Fact]
    public async Task Every_write_is_audited_and_creates_an_immutable_version()
    {
        var s = await Shop(f);
        var id = await Standard(s);
        await Update(s, id, Write(s, tiers: [Tier(20, 85)], version: 1));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.Put($"{Path}/{id}", Write(s, version: 1))).StatusCode);       // stale version
        var ended = await Orders.Ok(s.Owner.Post($"{Path}/{id}/end", new { version = 2 }));
        Assert.Equal("ended", (string)ended["status"]!);
        Assert.Equal(3, ended["versions"]!.AsArray().Count);
        var r = await s.Owner.Put($"{Path}/{id}", Write(s, version: 3));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("offer_ended", await Orders.Code(r));
        var types = f.WithDb(db => db.AuditEvents.Where(e => e.Detail != null && e.Detail.Contains($"offer={id}")).OrderBy(e => e.Id).Select(e => e.Type).ToList());
        Assert.Equal(new[] { "offer.created", "offer.updated", "offer.ended" }, types);
        var versions = f.WithDb(db => db.OfferVersions.AsNoTracking().Where(v => v.OfferId == id).OrderBy(v => v.Version).ToList());
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.Version));
        Assert.Contains("\"unitPriceCents\":90", versions[0].TiersJson);                                // version 1 is unchanged by the edit
        Assert.Contains("\"unitPriceCents\":85", versions[1].TiersJson);
        Assert.NotNull(versions[0].SupersededAt); Assert.NotNull(versions[1].SupersededAt); Assert.Null(versions[2].SupersededAt);
    }

    [Fact]
    public async Task One_active_offer_per_scope_and_the_preview_does_the_money()
    {
        var s = await Shop(f);
        await Standard(s);
        var r = await s.Owner.Post(Path, Write(s, products: [s.CardB], name: "Gold only"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("scope_conflict", await Orders.Code(r));
        Assert.Equal(HttpStatusCode.Created, (await s.Owner.Post(Path, Write(s, products: [s.CardB], name: "Gold only", status: "draft"))).StatusCode);
        // the issue's example: 10 cards $10.00 + free Wallet + free Pack, 20 cards $18.00, 25 cards $22.50
        var pv = await Orders.Ok(s.Owner.Post($"{Path}/preview", Write(s)));
        var rows = pv["rows"]!.AsArray();
        Assert.Equal(new[] { 10, 20, 25 }, rows.Select(x => (int)x!["quantity"]!));
        Assert.Equal(new[] { 1000L, 1800L, 2250L }, rows.Select(x => (long)x!["totalCents"]!));
        Assert.Equal(2, rows[0]!["gifts"]!.AsArray().Count);
        Assert.Equal("10 items: $10.00 + free Card wallet + free Card pack. 20 items: $18.00 + free Card wallet + free Card pack. 25 items: $22.50 + free Card wallet + free Card pack.", (string)pv["summary"]!);
    }
}

/// <summary>C18 feed and events.</summary>
public class OfferFeedTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private async Task<List<JsonNode>> ReadAll(string since = "0")
    {
        var items = new List<JsonNode>();
        var cursor = since;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await Cat.GetOk(f, $"{Feed}?since={cursor}&limit=2");
            items.AddRange(page["items"]!.AsArray().Select(i => i!));
            cursor = (string)page["nextCursor"]!;
            if (!(bool)page["hasMore"]!) break;
        }
        return items;
    }

    [Fact]
    public async Task The_feed_is_signed_ordered_by_feed_version_and_shows_resolved_products_tiers_and_gifts()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().GetAsync(Feed)).StatusCode);
        var s = await Shop(f);
        var start = (string)(await Cat.GetOk(f, $"{Feed}?limit=200"))["nextCursor"]!;
        var all = await ReadAll(start);                                         // drain whatever other tests wrote
        var cursor = all.Count == 0 ? start : all[^1]["feedVersion"]!.ToString();
        var draft = Guid.Parse((string)(await Create(s, Write(s, status: "draft", products: [s.CardB], name: "Draft")))["id"]!);
        var x = await Standard(s);
        var y = Guid.Parse((string)(await Create(s, Write(s, scope: "category", categories: [await ProdApi.CategoryId(s.Owner, "kitchen-dining")], products: [], gifts: [], tiers: [Tier(5, 95)], name: "Kitchen")))["id"]!);
        await Update(s, x, Write(s, tiers: [Tier(20, 88)], version: 1));
        var feed = (await ReadAll(cursor)).Where(i => (string)i["sellerId"]! == s.Seller.ToString()).ToList();
        Assert.DoesNotContain(feed, i => (string)i["offerId"]! == draft.ToString());                  // never active: never in the feed
        Assert.Equal(new[] { y.ToString(), x.ToString() }, feed.Select(i => (string)i["offerId"]!));             // x moved behind y when it changed
        var fv = feed.Select(i => (long)i["feedVersion"]!).ToList();
        Assert.True(fv[0] < fv[1]);
        var doc = feed[1];
        Assert.Equal(2, (int)doc["version"]!); Assert.True((bool)doc["listed"]!); Assert.Equal("active", (string)doc["status"]!);
        Assert.Equal("products", (string)doc["scope"]!["kind"]!);
        Assert.Equal(new[] { s.CardA, s.CardB }.Select(g => g.ToString()).Order(), doc["productIds"]!.AsArray().Select(p => (string)p!).Order());
        Assert.Equal(88, (long)doc["tiers"]![0]!["unitPriceCents"]!); Assert.Equal(20, (int)doc["tiers"]![0]!["minQuantity"]!);
        Assert.Contains(doc["gifts"]!.AsArray(), g => (string)g!["productId"]! == s.Wallet.ToString() && (bool)g["available"]! && (string)g["title"]! == "Card wallet" && (int)g["minQuantity"]! == 10);
        Assert.False((bool)doc["demo"]!);
        Assert.Equal("USD", (string)doc["currency"]!);
        // the category offer resolves the products of that category (all four of this shop are in kitchen-dining)
        Assert.Equal(4, feed[0]["productIds"]!.AsArray().Count);
        var one = await Cat.GetOk(f, $"{Feed}/{x}");
        Assert.Equal(2, (int)one["version"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Cat.SignedGet(f, $"{Feed}/{draft}")).StatusCode);
        // ending x puts an ended entry at the end of the feed
        await Orders.Ok(s.Owner.Post($"{Path}/{x}/end", new { version = 2 }));
        var after = (await ReadAll(cursor)).Where(i => (string)i["sellerId"]! == s.Seller.ToString()).ToList();
        var last = after[^1];
        Assert.Equal(x.ToString(), (string)last["offerId"]!); Assert.False((bool)last["listed"]!); Assert.Equal("ended", (string)last["status"]!); Assert.Equal("ended", (string)last["reason"]!);
    }

    [Fact]
    public async Task Changes_emit_ordered_offer_events_in_their_own_sequence_scope()
    {
        var s = await Shop(f);
        var x = await Standard(s);
        await Update(s, x, Write(s, status: "draft", version: 1));     // paused
        await Update(s, x, Write(s, version: 2));                      // active again
        await Orders.Ok(s.Owner.Post($"{Path}/{x}/end", new { version = 3 }));
        var msgs = f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == x.ToString()).OrderBy(m => m.OrderKey).ToList());
        Assert.Equal(new[] { "offer.updated", "offer.ended", "offer.updated", "offer.ended" }, msgs.Select(m => m.EventType));
        Assert.All(msgs, m => Assert.Equal("offer:" + x.ToString("N"), m.SequenceScope));
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, msgs.Select(m => m.OrderKey!.Value));
        var data = msgs.Select(m => JsonNode.Parse(m.PayloadJson)!["data"]!).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, data.Select(d => (int)d["version"]!));
        Assert.Equal("paused", (string)data[1]["reason"]!); Assert.Equal("ended", (string)data[3]["reason"]!);
        var fvs = data.Select(d => (long)d["feedVersion"]!).ToList();
        Assert.Equal(fvs.Order(), fvs);
        Assert.Equal(x.ToString(), (string)data[0]["offerId"]!);
        // a draft that was never active sends nothing
        var d2 = Guid.Parse((string)(await Create(s, Write(s, status: "draft", products: [s.CardB], name: "Quiet")))["id"]!);
        Assert.Empty(f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == d2.ToString()).ToList()));
    }
}

/// <summary>C18 demo data: one offer with tiers and a gift on the demo products, removed with the rest.</summary>
public class OfferDemoTests(DemoFactory f) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task The_demo_fill_seeds_one_offer_flagged_demo_and_removal_ends_it_in_the_feed()
    {
        var (api, seller) = await ProdApi.Approved(f);
        Assert.Equal(HttpStatusCode.Created, (await api.Post("/api/v1/seller/demo-data")).StatusCode);
        var list = (await Orders.Node(await api.Get(Path)))["items"]!.AsArray();
        var offer = Assert.Single(list)!;
        Assert.True((bool)offer["demo"]!); Assert.Equal("active", (string)offer["state"]!);
        Assert.Equal(2, offer["tiers"]!.AsArray().Count); Assert.Equal(1, (int)offer["giftCount"]!);
        var id = (string)offer["id"]!;
        var doc = await Cat.GetOk(f, $"{Feed}/{id}");
        Assert.True((bool)doc["demo"]!); Assert.Equal(2, doc["productIds"]!.AsArray().Count);
        Assert.Equal(1, f.WithDb(db => db.DemoRecords.Count(r => r.SellerId == seller && r.Kind == "offer")));
        Assert.Empty(f.WithDb(db => db.Outbox.Where(m => m.AggregateId == id).ToList()));                 // demo data sends no events
        Assert.Equal(HttpStatusCode.OK, (await api.Delete("/api/v1/seller/demo-data")).StatusCode);
        Assert.Empty((await Orders.Node(await api.Get(Path)))["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"{Path}/{id}")).StatusCode);
        var gone = await Cat.GetOk(f, $"{Feed}/{id}");
        Assert.False((bool)gone["listed"]!); Assert.Equal("deleted", (string)gone["reason"]!);
        Assert.Equal(0, f.WithDb(db => db.OfferVersions.Count(v => v.OfferId == Guid.Parse(id))));
    }
}
