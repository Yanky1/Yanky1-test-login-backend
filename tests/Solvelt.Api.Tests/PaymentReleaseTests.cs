using System.Net;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class ReserveFactory : PayFactory
{
    public ReserveFactory() { Extra["Payments:NewSellerReservePercent"] = "10"; Extra["Payments:ReserveReleaseDays"] = "30"; }
}

public class WindowFactory : PayFactory
{
    public WindowFactory() { Extra["Payments:RequireReturnWindowClosed"] = "true"; Extra["Orders:ReturnWindowDays"] = "30"; }
}

public class PaymentReleaseTests
{
    [Fact]
    public async Task Funds_become_available_N_days_after_delivery_not_before()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        Pay.Age(f, 6);
        Assert.Equal(0, (await Pay.Release(f)).Released);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, 0), (b.Pending, b.Available));
        Pay.Age(f, 2);
        Assert.Equal(1, (await Pay.Release(f)).Released);
        b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, 2299), (b.Pending, b.Available));
        Assert.Equal(0, (await Pay.Release(f)).Released);      // running again changes nothing
        Assert.Single(Pay.Journals(f, s.Seller, "release"));
    }

    [Fact]
    public async Task Undelivered_and_unpaid_orders_are_never_released()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var (mid, id, number, _) = await Orders.Place(s);
        await Orders.Ok(Orders.Act(s.Owner, id, "accept"));
        await Orders.Ok(Orders.Act(s.Owner, id, "ship", Orders.Ship()));
        Assert.Equal(HttpStatusCode.OK, (await Pay.Capture(f, mid, number)).StatusCode);   // shipped, paid, but not delivered
        var unpaid = await Pay.Delivered(s);                                               // delivered, not captured: no pending funds at all
        Pay.Age(f, 60);
        Assert.Equal(0, (await Pay.Release(f)).Released);
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Pending);
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Available);
    }

    [Fact]
    public async Task An_open_return_blocks_the_release_and_a_refund_before_release_leaves_only_the_rest()
    {
        using var f = new PayFactory();
        var s = await Orders.NewShop(f);
        var o = await Pay.Paid(s);
        var ret = await Orders.Inbound(f, $"/api/internal/v1/orders/{o.Mid}/return-requests", new { returnId = "R1", reason = "damaged", lines = new[] { new { marketplaceLineId = "L1", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.OK, ret.StatusCode);
        Pay.Age(f, 10);
        Assert.Equal(0, (await Pay.Release(f)).Released);
        Assert.Equal(2299, (await Pay.Bal(f, s.Seller)).Pending);
        Assert.Equal(HttpStatusCode.OK, (await Pay.Refund(f, o.Mid, o.Number, 1999, 500)).StatusCode);   // refund while the return is open
        Assert.Equal(0, (await Pay.Bal(f, s.Seller)).Pending);
        Assert.Equal(0, (await Pay.Release(f)).Released);
    }

    [Fact]
    public async Task Release_can_also_wait_for_the_return_window()
    {
        using var f = new WindowFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s);
        Pay.Age(f, 10);
        Assert.Equal(0, (await Pay.Release(f)).Released);
        Pay.Age(f, 21);
        Assert.Equal(1, (await Pay.Release(f)).Released);
    }

    [Fact]
    public async Task New_seller_reserve_is_withheld_then_released_after_M_days()
    {
        using var f = new ReserveFactory();
        var s = await Orders.NewShop(f);
        await Pay.Paid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        var b = await Pay.Bal(f, s.Seller);
        Assert.Equal((0, 2069, 230), (b.Pending, b.Available, b.Reserved));      // 10% of 2299 = 229.9 -> 230 (half up)
        Pay.Age(f, 29);
        Assert.Equal(0, (await Pay.Release(f)).ReserveReleased);                // 29 days after the release: still held
        Pay.Age(f, 2);
        Assert.Equal(1, (await Pay.Release(f)).ReserveReleased);
        b = await Pay.Bal(f, s.Seller);
        Assert.Equal((2299, 0), (b.Available, b.Reserved));
        Assert.Equal(0, (await Pay.Release(f)).ReserveReleased);                // idempotent
        Assert.Single(Pay.Journals(f, s.Seller, "reserve_release"));
    }

    [Fact]
    public async Task Established_sellers_have_no_reserve()
    {
        using var f = new ReserveFactory();
        var s = await Orders.NewShop(f);
        f.WithDb(db => { var a = db.Sellers.First(x => x.Id == s.Seller); a.CreatedAt = f.Time.GetUtcNow().UtcDateTime.AddDays(-400); db.SaveChanges(); return 0; });
        await Pay.Paid(s);
        Pay.Age(f, 8); await Pay.Release(f);
        Assert.Equal((2299, 0), ((await Pay.Bal(f, s.Seller)).Available, (await Pay.Bal(f, s.Seller)).Reserved));
    }
}
