using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>Inventory that fails on the Nth reservation of the process-wide switch (simulates a crash after some stock was already held).</summary>
public class FlakyInventory(IInventoryService inner) : IInventoryService
{
    public static volatile int FailOnReserveNumber;   // 0 = never
    public static int Counter;
    public async Task<InventoryResult> ReserveAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default)
    {
        if (FailOnReserveNumber > 0 && Interlocked.Increment(ref Counter) == FailOnReserveNumber) throw new InvalidOperationException("boom");
        return await inner.ReserveAsync(s, p, v, q, ct);
    }
    public Task<InventoryResult> ReleaseAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default) => inner.ReleaseAsync(s, p, v, q, ct);
    public Task<InventoryResult> CommitAsync(Guid s, Guid p, Guid? v, int q, CancellationToken ct = default) => inner.CommitAsync(s, p, v, q, ct);
    public Task<InventoryResult> AdjustAsync(Guid s, Guid p, Guid? v, int d, CancellationToken ct = default) => inner.AdjustAsync(s, p, v, d, ct);
    public Task<InventoryResult> SetOnHandAsync(Guid s, Guid p, Guid? v, int o, CancellationToken ct = default) => inner.SetOnHandAsync(s, p, v, o, ct);
    public Task<(Guid ProductId, Guid? VariantId)?> FindBySkuAsync(Guid s, string sku, CancellationToken ct = default) => inner.FindBySkuAsync(s, sku, ct);
}

public class FlakyFactory : OrdersFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IInventoryService>();
            s.AddScoped<IInventoryService>(sp => new FlakyInventory(ActivatorUtilities.CreateInstance<InventoryService>(sp)));
        });
    }
}

public class OrderReviewFixTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private const string Path = "/api/internal/v1/orders";

    // 9: unbounded money
    [Fact]
    public async Task Absurd_amounts_are_a_422_not_a_500_and_reserve_nothing()
    {
        var s = await NewShop(f);
        var r = await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 2, 4_000_000_000_000_000_000), Line(s.Seller, s.Brand, s.Product, 2, 4_000_000_000_000_000_000)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal((10L, 0L), Stock(f, s.Product));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1999, tax: long.MaxValue)]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)], [new { sellerId = s.Seller, brandId = s.Brand, amountCents = long.MaxValue }]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Inbound(f, Path, OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 1, 1_000_000_000)]))).StatusCode);   // the cap itself is fine
    }

    // 10: crafted timestamps
    [Fact]
    public async Task Extreme_timestamps_are_a_401_before_any_arithmetic()
    {
        var s = await NewShop(f);
        var body = OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product)]);
        foreach (var ts in new[] { long.MinValue, long.MaxValue, -1L, 4_102_444_801L })
        {
            var r = await Inbound(f, Path, body, ts: ts);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    // 3: conversations never cross accounts
    [Fact]
    public async Task The_same_marketplace_thread_id_for_two_sellers_is_two_separate_conversations()
    {
        var a = await NewShop(f); var b = await NewShop(f);
        var (midA, _, numA, _) = await Place(a); var (midB, _, numB, _) = await Place(b);
        var (_, _, convA, _) = await Msg.BuyerMessage(f, "order", midA, numA, conv: "SHARED-THREAD", text: "A-secret-text");
        var (_, _, convB, _) = await Msg.BuyerMessage(f, "order", midB, numB, conv: "SHARED-THREAD", text: "B-private-text");
        Assert.NotEqual(convA, convB);
        Assert.DoesNotContain("B-private-text", await (await a.Owner.Get($"/api/v1/seller/messages/{convA}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.Get($"/api/v1/seller/messages/{convB}")).StatusCode);
        Assert.Contains("B-private-text", await (await b.Owner.Get($"/api/v1/seller/messages/{convB}")).Content.ReadAsStringAsync());
        // inside one seller the same thread id cannot be re-pointed at another order
        var (midA2, _, numA2, _) = await Place(a);
        var bad = await Inbound(f, "/api/internal/v1/messages", new { messageId = Msg.Id(), conversationId = "SHARED-THREAD", kind = "order", marketplaceOrderId = midA2, sellerOrderNumber = numA2, buyerName = "X", text = "hi" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode); Assert.Equal("conversation_mismatch", await Code(bad));
    }

    [Fact]
    public async Task A_buyer_reply_joins_the_thread_the_seller_started()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        var started = await Node(await s.Owner.Post($"/api/v1/seller/orders/{id}/messages", new { text = "Quick question about your address." }));
        var conv = Guid.Parse((string)started["conversationId"]!);
        var (_, _, convB, _) = await Msg.BuyerMessage(f, "order", mid, number, text: "Sure, it's correct.");
        Assert.Equal(conv, convB);
        Assert.Equal(2, (await Node(await s.Owner.Get($"/api/v1/seller/messages/{conv}")))["messages"]!.AsArray().Count);
        Assert.Equal(1, f.WithDb(db => db.Conversations.Count(c => c.OrderId == id)));
    }

    // 4: oversold units leave stock when shipped
    [Fact]
    public async Task Shipping_units_that_were_oversold_still_reduces_on_hand_stock()
    {
        var s = await NewShop(f, stock: 1);
        var (_, id, _, _) = await Place(s, qty: 2);
        Assert.True((bool)(await Detail(s.Owner, id))["oversold"]!);
        Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(s.Owner, s.Product, d => d["quantity"] = 5)).StatusCode);   // restocked afterwards
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));
        Assert.Equal((3L, 0L), Stock(f, s.Product));
    }

    // 5: refunds before shipping
    [Fact]
    public async Task A_refund_before_anything_shipped_is_refused_and_unshipped_units_cannot_be_refunded()
    {
        var s = await NewShop(f);
        var (_, id, _, _) = await Place(s, qty: 2);
        await Ok(Act(s.Owner, id, "accept"));
        var r = await Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("refund_before_ship", await Code(r));
        Assert.Equal("Accepted", St(await Detail(s.Owner, id)));
        Assert.Equal((10L, 2L), Stock(f, s.Product));                                   // still held for the order
        Assert.False((bool)(await Detail(s.Owner, id))["actions"]!["refund"]!);
        // partly shipped: only the shipped unit is refundable; the full refund covers it alone
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "ship", Ship(line, 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 2 } } })).StatusCode);
        var full = await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", full = true }));
        Assert.Equal(1999 + 500, (long)full["money"]!["refundedCents"]!);               // one shipped unit + shipping, not the whole line
        Assert.Equal("PartiallyShipped", St(full));
        Assert.Equal(HttpStatusCode.OK, (await Act(s.Owner, id, "ship", Ship())).StatusCode);   // the other unit can still ship
    }

    // 7: concurrent change vs buyer request
    [Fact]
    public async Task A_buyer_cancel_request_racing_a_seller_change_is_retryable_never_reported_as_duplicate()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<BuyerEventService>();
        svc.BeforeSave = async _ =>
        {
            using var other = f.Services.CreateScope();   // somebody else changes the order right before our save
            await other.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>().Orders.Where(o => o.Id == id).ExecuteUpdateAsync(u => u.SetProperty(o => o.Version, o => o.Version + 1));
            await Task.CompletedTask;
        };
        var first = await svc.CancelRequestAsync(mid, new("CR-RACE", number, "changed_mind", null));
        Assert.Equal(409, first.Status); Assert.Equal("version_conflict", first.Code);
        Assert.Equal("retry", first.Orders[0].Result);
        Assert.Equal(0, f.WithDb(db => db.Cancellations.Count(c => c.OrderId == id)));       // nothing half-stored
        svc.BeforeSave = null;
        scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>().ChangeTracker.Clear();
        var second = await svc.CancelRequestAsync(mid, new("CR-RACE", number, "changed_mind", null));
        Assert.Equal("requested", second.Orders[0].Result);
        Assert.True((bool)(await Detail(s.Owner, id))["cancelRequested"]!);

        // same for a return request
        var (mid2, id2, n2, _) = await Place(s, lid: "RL");
        await Ok(Act(s.Owner, id2, "accept")); await Ok(Act(s.Owner, id2, "ship", Ship())); await Ok(Act(s.Owner, id2, "deliver"));
        using var scope2 = f.Services.CreateScope();
        var svc2 = scope2.ServiceProvider.GetRequiredService<BuyerEventService>();
        svc2.BeforeSave = async _ =>
        {
            using var other = f.Services.CreateScope();
            await other.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>().Orders.Where(o => o.Id == id2).ExecuteUpdateAsync(u => u.SetProperty(o => o.Version, o => o.Version + 1));
            await Task.CompletedTask;
        };
        var ret = await svc2.ReturnRequestAsync(mid2, new("R-RACE", n2, "damaged", null, [new("RL", 1)]));
        Assert.Equal(409, ret.Status); Assert.Equal("retry", ret.Orders[0].Result);
        Assert.Equal(0, f.WithDb(db => db.Returns.Count(r => r.OrderId == id2)));
    }

    // 8: SSE after a scope change
    [Fact]
    public async Task An_open_stream_stops_delivering_a_brand_the_member_lost()
    {
        var s = await NewShop(f);
        var brandY = await TeamHelpers.NewBrand(s.Owner, "Brand Y");
        var py = await ActiveProduct(s.Owner, "Y thing", brandY, "Y-" + Guid.NewGuid().ToString("N")[..6]);
        var (mgr, email) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand, brandY]); await mgr.Switch(s.Seller);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stream = await Msg.Open(mgr, cts.Token);
        await Msg.BuyerMessage(f, "product", product: py);
        Assert.True(await stream.Gets("message.new"));                                          // allowed while the scope includes Y
        var uid = f.WithDb(db => db.Users.First(u => u.Email == email).Id);
        f.WithDb(db => { var m = db.Memberships.First(x => x.UserId == uid && x.SellerId == s.Seller); db.MembershipBrands.Where(b => b.MembershipId == m.Id && b.BrandId == brandY).ExecuteDelete(); return 0; });
        await Task.Delay(2500);                                                                 // > StreamRecheckSeconds (1)
        await Msg.BuyerMessage(f, "product", product: py);
        Assert.False(await stream.Gets("message.new", 1500));                                   // brand Y no longer reaches this member
        await Msg.BuyerMessage(f, "product", product: s.Product);
        Assert.True(await stream.Gets("message.new"));                                          // brand X still does
    }

    // 11: PDF name escapes
    [Fact]
    public async Task Pdf_active_content_hidden_behind_name_escapes_is_rejected()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        foreach (var evil in new[] { "/S/J#61vaScript/JS(app.alert(1))", "/#4Aavascript", "/S /#4C#61unch" })
        {
            var bad = await Msg.Upload(s.Owner, conv, Encoding.ASCII.GetBytes($"%PDF-1.4\n1 0 obj<<{evil}>>endobj\n%%EOF"), "x.pdf", "application/pdf");
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, bad.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Created, (await Msg.Upload(s.Owner, conv, Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Title(Invoice #61)>>endobj\n%%EOF"), "ok.pdf", "application/pdf")).StatusCode);
    }
}

public class OrderIngestCrashTests(FlakyFactory f) : IClassFixture<FlakyFactory>
{
    [Fact]
    public async Task A_failure_after_some_stock_was_reserved_gives_every_hold_back()
    {
        var s = await NewShop(f, stock: 10);
        var p2 = await ActiveProduct(s.Owner, "Second thing", s.Brand, "S2-" + Guid.NewGuid().ToString("N")[..6], 10);
        FlakyInventory.Counter = 0; FlakyInventory.FailOnReserveNumber = 2;     // the second reservation blows up
        try
        {
            var r = await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 3), Line(s.Seller, s.Brand, p2, 2)]));
            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        }
        finally { FlakyInventory.FailOnReserveNumber = 0; }
        Assert.Equal((10L, 0L), Stock(f, s.Product));
        Assert.Equal((10L, 0L), Stock(f, p2));
        Assert.Equal(0, f.WithDb(db => db.Orders.Count(o => o.SellerId == s.Seller)));
        // and the retry of the same order then works and reserves exactly once
        Assert.Equal(HttpStatusCode.Created, (await Inbound(f, "/api/internal/v1/orders", OrderBody(f, Mid(), [Line(s.Seller, s.Brand, s.Product, 3)]))).StatusCode);
        Assert.Equal((10L, 3L), Stock(f, s.Product));
    }
}

public class OrderDispatchRaceTests : IClassFixture<WebhookFactory>
{
    private readonly WebhookFactory _f;
    public OrderDispatchRaceTests(WebhookFactory f) { _f = f; }

    [Fact]
    public async Task Two_dispatchers_with_a_slow_webhook_deliver_each_event_exactly_once()
    {
        var s = await NewShop(_f);
        var (mid, id, _, _) = await Place(s);
        using (var warm = _f.Services.CreateScope()) await warm.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();   // flush other events
        _f.Handler = _ => { Thread.Sleep(400); return new HttpResponseMessage(HttpStatusCode.OK); };
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "hold")); await Ok(Act(s.Owner, id, "resume"));
        lock (_f.Calls) _f.Calls.Clear();
        async Task Run() { using var sc = _f.Services.CreateScope(); await sc.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync(); }
        await Task.WhenAll(Run(), Run(), Run());
        List<string> ids; lock (_f.Calls) ids = _f.Calls.Where(c => c.Body.Contains(mid)).Select(c => c.Req.Headers.GetValues(Signing.EventIdHeader).Single()).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());                                   // one call per event, no duplicates
        Assert.All(OutboxFor(_f, mid), m => { Assert.Equal(OutboxStatus.Delivered, m.Status); Assert.Equal(1, m.Attempts); });
    }
}

public class OrderRedactionFixTests(JobFactory f) : IClassFixture<JobFactory>
{
    private async Task Run() { using var sc = f.Services.CreateScope(); await sc.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync(); }

    [Fact]
    public async Task After_redaction_the_buyer_name_and_free_text_are_in_no_table()
    {
        var s = await NewShop(f);
        const string Name = "Zelda Quixote";
        var (mid, id, number, _) = await Place(s, buyer: Name, lid: "RL");
        await Inbound(f, $"/api/internal/v1/orders/{mid}/cancel-requests", new { requestId = "C1", reason = "changed_mind", note = $"{Name} here, please cancel" });
        var cid = (string)(await Detail(s.Owner, id))["cancellations"]![0]!["id"]!;
        await Ok(Act(s.Owner, id, $"cancellation-requests/{cid}/reject", new { note = $"Sorry {Name}, already packed" }));
        await Ok(Act(s.Owner, id, "notes", new { text = $"Call {Name} before shipping" }));
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        await Inbound(f, $"/api/internal/v1/orders/{mid}/return-requests", new { returnId = "R1", reason = "damaged", comment = $"{Name} says it arrived broken", lines = new[] { new { marketplaceLineId = "RL", quantity = 1 } } });
        var rid = (string)(await Detail(s.Owner, id))["returns"]![0]!["id"]!;
        await Ok(Act(s.Owner, id, $"returns/{rid}/reject", new { reason = "item_used", note = $"{Name} used it" }));
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number, text: $"This is {Name}");
        await Msg.Reply(s.Owner, conv, $"Hello {Name}!");
        await _httpDispatch();
        f.Time.Advance(TimeSpan.FromDays(31)); await Run();        // return window closes -> Completed
        Assert.Equal(OrderStatus.Completed, f.WithDb(db => db.Orders.First(o => o.Id == id).Status));
        f.Time.Advance(TimeSpan.FromDays(91)); await Run();        // retention passed -> redacted

        string dump = f.WithDb(db => string.Join("\n",
            db.Orders.Select(o => o.BuyerName + o.ShipName + o.ShipLine1 + o.GiftMessage).ToList()
            .Concat(db.OrderEvents.Where(e => e.OrderId == id).Select(e => e.Text).ToList())
            .Concat(db.Returns.Where(r => r.OrderId == id).Select(r => r.BuyerComment + r.DecisionReason).ToList())
            .Concat(db.Cancellations.Where(c => c.OrderId == id).Select(c => c.Note + c.DecisionNote).ToList())
            .Concat(db.OrderNotes.Where(n => n.OrderId == id).Select(n => n.Text).ToList())
            .Concat(db.Messages.Where(m => m.ConversationId == conv).Select(m => m.Text).ToList())
            .Concat(db.Conversations.Where(c => c.Id == conv).Select(c => c.BuyerName).ToList())
            .Concat(db.Outbox.Where(o => o.AggregateId == mid || o.PayloadJson.Contains(mid)).Select(o => o.PayloadJson).ToList())));
        Assert.False(dump.Contains("Zelda"), dump);
        Assert.DoesNotContain("Quixote", dump);
        Assert.DoesNotContain(mid.Length > 0 ? "broken" : "", dump);
        Assert.NotNull(f.WithDb(db => db.Orders.First(o => o.Id == id).PiiRedactedAt));
    }

    private async Task _httpDispatch() => await f.NewClient().PostAsync("/api/dev/outbox/dispatch", null);

    [Fact]
    public async Task Draft_attachments_that_were_never_sent_are_cleaned_up()
    {
        var s = await NewShop(f);
        var (mid, _, number, _) = await Place(s);
        var (_, _, conv, _) = await Msg.BuyerMessage(f, "order", mid, number);
        var up = await Node(await Msg.Upload(s.Owner, conv, TestImages.Png()));
        var key = f.WithDb(db => db.MessageAttachments.First(a => a.Id == Guid.Parse((string)up["id"]!)).StorageKey);
        var path = System.IO.Path.Combine(f.Dir, "uploads", key);
        await Run(); Assert.True(File.Exists(path));                                        // still fresh
        f.Time.Advance(TimeSpan.FromDays(2)); await Run();
        Assert.False(File.Exists(path));
        Assert.Empty(f.WithDb(db => db.MessageAttachments.Where(a => a.Id == Guid.Parse((string)up["id"]!)).ToList()));
    }
}
