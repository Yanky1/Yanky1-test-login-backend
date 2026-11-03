using System.Net;
using System.Net.Http.Json;
using System.Text;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class LimitFactory : OrdersFactory { public LimitFactory() { Extra["Orders:ManagerRefundLimitCents"] = "1500"; } }

public class OrderSecurityTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Another_accounts_order_is_a_404_on_every_seller_endpoint()
    {
        var s = await NewShop(f);
        var other = await NewShop(f);
        var (mid, id, _, _) = await Place(s, lid: "L1");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        var d = await Detail(s.Owner, id);
        var line = (string)d["lines"]![0]!["id"]!; var ship = (string)d["shipments"]![0]!["id"]!; var rnd = Guid.NewGuid();
        var o = other.Owner; var b = $"/api/v1/seller/orders/{id}";
        foreach (var url in new[] { b, b + "/packing-slip", $"{b}/lines/{line}/image" }) Assert.Equal(HttpStatusCode.NotFound, (await o.Get(url)).StatusCode);
        foreach (var act in new[] { "accept", "hold", "resume", "ship", "deliver", "cancel", "refunds", "refunds/preview", "notes", $"returns/{rnd}/approve", $"returns/{rnd}/reject", $"returns/{rnd}/receive",
                     $"cancellation-requests/{rnd}/approve", $"cancellation-requests/{rnd}/reject" })
            Assert.Equal(HttpStatusCode.NotFound, (await o.Post($"{b}/{act}", new { carrier = "UPS", trackingNumber = "1Z999AA10123456784", reason = "other", full = true, text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.Put($"{b}/shipments/{ship}", new { carrier = "UPS", trackingNumber = "1Z999AA10123456784" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.Post($"{b}/messages", new { text = "hello" })).StatusCode);
        // bulk endpoints answer per id / empty, never leak
        var bulk = await Node(await o.Post("/api/v1/seller/orders/bulk/accept", new { ids = new[] { id } }));
        Assert.Equal("not_found", (string)bulk["results"]![0]!["code"]!);
        Assert.Empty((await Node(await o.Post("/api/v1/seller/orders/bulk/packing-slips", new { ids = new[] { id } })))["slips"]!.AsArray());
        Assert.Equal(0, (int)(await Node(await o.Get($"/api/v1/seller/orders?q={mid}")))["total"]!);
        // the owner's order is untouched
        Assert.Equal("Shipped", St(await Detail(s.Owner, id)));
    }

    [Fact]
    public async Task Unauthenticated_and_unverified_callers_get_nothing()
    {
        var anon = new Api(f);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/v1/seller/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/v1/seller/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/v1/seller/events")).StatusCode);
        var unverified = new Api(f); await unverified.UnverifiedSession();
        Assert.Equal(HttpStatusCode.Forbidden, (await unverified.Get("/api/v1/seller/orders")).StatusCode);
    }

    [Fact]
    public async Task Manager_is_limited_to_their_brands_and_cannot_refund_by_default()
    {
        var s = await NewShop(f);
        var brandY = await TeamHelpers.NewBrand(s.Owner, "Brand Y");
        var py = await ActiveProduct(s.Owner, "Y item", brandY, "Y-" + Guid.NewGuid().ToString("N")[..6]);
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]);
        await mgr.Switch(s.Seller);
        var (_, ox, _, _) = await Place(s, qty: 1);
        var mid = Mid();
        var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, mid, [Line(s.Seller, brandY, py)]));
        var oy = Guid.Parse((string)(await Node(r))["orders"]![0]!["sellerOrderId"]!);

        // sees brand X only
        Assert.Equal(HttpStatusCode.OK, (await mgr.Get($"/api/v1/seller/orders/{ox}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Get($"/api/v1/seller/orders/{oy}")).StatusCode);
        Assert.Equal(0, (int)(await Node(await mgr.Get($"/api/v1/seller/orders?q={mid}")))["total"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(mgr, oy, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(mgr, oy, "cancel", new { reason = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Post($"/api/v1/seller/orders/{oy}/messages", new { text = "hi" })).StatusCode);
        // works the orders of their brand
        await Ok(Act(mgr, ox, "accept")); await Ok(Act(mgr, ox, "ship", Ship())); await Ok(Act(mgr, ox, "deliver"));
        // ...but cannot refund (default limit 0) even on their own brand; owner can
        var denied = await Act(mgr, ox, "refunds", new { reason = "goodwill", full = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal("refund_limit", await Code(denied));
        Assert.False((bool)((await Detail(mgr, ox))["actions"]!["refund"]!));
        Assert.Equal(0, (long)(await Detail(s.Owner, ox))["money"]!["refundedCents"]!);
        Assert.Equal("Refunded", St(await Ok(Act(s.Owner, ox, "refunds", new { reason = "goodwill", full = true }))));
        // owner sees both
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Get($"/api/v1/seller/orders/{oy}")).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_never_act()
    {
        var s = await NewShop(f);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer");
        await viewer.Switch(s.Seller);
        var (_, id, _, _) = await Place(s);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/v1/seller/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get("/api/v1/seller/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get("/api/v1/seller/orders/counts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get("/api/v1/seller/orders/export.csv")).StatusCode);
        foreach (var act in new[] { "accept", "hold", "resume", "ship", "deliver", "cancel", "refunds", "refunds/preview", "notes" })
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/orders/{id}/{act}", new { reason = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/v1/seller/orders/bulk/accept", new { ids = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/orders/{id}/messages", new { text = "hi" })).StatusCode);
        Assert.False((bool)((await Detail(viewer, id))["actions"]!["accept"]!));
        Assert.Equal("New", St(await Detail(s.Owner, id)));
    }

    [Fact]
    public void Permission_matrix_gives_refunds_to_owner_and_admin_only()
    {
        Assert.True(Permissions.Has(AccountRole.Owner, Permission.OrderRefund)); Assert.True(Permissions.Has(AccountRole.Admin, Permission.OrderRefund));
        Assert.False(Permissions.Has(AccountRole.Manager, Permission.OrderRefund)); Assert.False(Permissions.Has(AccountRole.Viewer, Permission.OrderRefund));
        Assert.True(Permissions.Has(AccountRole.Manager, Permission.BrandWorkWrite)); Assert.False(Permissions.Has(AccountRole.Viewer, Permission.BrandWorkWrite));
    }

    [Fact]
    public async Task Admin_member_can_refund_and_sees_all_brands()
    {
        var s = await NewShop(f);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        await admin.Switch(s.Seller);
        var (_, id, _, _) = await Place(s);
        await Ok(Act(admin, id, "accept")); await Ok(Act(admin, id, "ship", Ship())); await Ok(Act(admin, id, "deliver"));
        Assert.Equal("Refunded", St(await Ok(Act(admin, id, "refunds", new { reason = "goodwill", full = true }))));
    }

    [Fact]
    public async Task Audit_log_records_accept_ship_cancel_refund_and_return_decisions()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s, lid: "RL");
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R", reason = "damaged", lines = new[] { new { marketplaceLineId = "RL", quantity = 1 } } });
        var rid = (string)(await Detail(s.Owner, id))["returns"]![0]!["id"]!;
        await Ok(Act(s.Owner, id, $"returns/{rid}/approve")); await Ok(Act(s.Owner, id, $"returns/{rid}/receive"));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "returned_item", returnId = rid, full = true }));
        var (_, c, _, _) = await Place(s);
        await Ok(Act(s.Owner, c, "cancel", new { reason = "other" }));
        var types = f.WithDb(db => db.AuditEvents.Where(a => a.Detail != null && a.Detail.Contains("order=" + number)).Select(a => a.Type).ToList());
        foreach (var t in new[] { "order.accept", "order.ship", "order.return_approve", "order.return_received", "order.refund" }) Assert.Contains(t, types);
        Assert.Contains("order.cancel", f.WithDb(db => db.AuditEvents.Select(a => a.Type).ToList()));
        // buyer PII is not written to the audit log
        Assert.DoesNotContain(f.WithDb(db => db.AuditEvents.Select(a => a.Detail).ToList()), x => x != null && x.Contains("Jane Buyer"));
    }

    [Fact]
    public async Task Writes_are_rate_limited_per_user()
    {
        using var lf = new OrdersFactory { Extra = { ["RateLimit:OrdersPerMinute"] = "5" } };
        var s = await NewShop(lf);
        var (_, id, _, _) = await Place(s);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) codes.Add((await s.Owner.Get($"/api/v1/seller/orders/{id}")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}

public class OrderRefundLimitTests(LimitFactory f) : IClassFixture<LimitFactory>
{
    [Fact]
    public async Task Manager_can_refund_up_to_the_configured_limit_only()
    {
        var s = await NewShop(f);
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        await mgr.Switch(s.Seller);
        var (_, id, _, _) = await Place(s, qty: 2, price: 1000, shipping: 0);
        await Ok(Act(mgr, id, "accept")); await Ok(Act(mgr, id, "ship", Ship())); await Ok(Act(mgr, id, "deliver"));
        var line = await LineId(mgr, id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(mgr, id, "refunds", new { reason = "goodwill", full = true })).StatusCode);   // 2000 > 1500
        Assert.Equal(HttpStatusCode.OK, (await Act(mgr, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } })).StatusCode);   // 1000
        Assert.True((bool)(await Detail(mgr, id))["actions"]!["refund"]!);
        Assert.Equal(1500, (long)(await Detail(mgr, id))["actions"]!["refundLimitCents"]!);
    }
}
