using Solvelt.Api.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public record SimOrderRequest(string? Scenario, string? BuyerName);
public record SimEventRequest(string? Type, string? Text, string? Reason, string? LineId);
public record SimMessageRequest(Guid? ProductId, string? Text, string? BuyerName);

public static class AdminDevEndpoints
{
    /// <summary>Platform-admin view of the outbox (dead letters) and of messages reported by buyers.</summary>
    public static void MapOrderAdminEndpoints(this RouteGroupBuilder g)
    {
        var a = g.MapGroup("/admin"); // staff-gated route by route (Solvelt.Api.Admin): a seller-kind session, even of an Admin-role user, never reaches these
        a.MapGet("/outbox", async (AppDbContext db, string? status, int? page, int? pageSize) =>
        {
            var q = db.Outbox.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<OutboxStatus>(status, true, out var st)) return Validation.Invalid("status", "Use pending, delivered or dead.");
                q = q.Where(m => m.Status == st);
            }
            var size = Math.Clamp(pageSize ?? 50, 1, 200); var pg = Math.Max(page ?? 1, 1);
            var counts = new { pending = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Pending), delivered = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Delivered), dead = await db.Outbox.CountAsync(m => m.Status == OutboxStatus.Dead) };
            var items = await q.OrderByDescending(m => m.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Json(new
            {
                counts, items = items.Select(m => new { id = m.Id, type = m.EventType, sellerId = m.SellerId, aggregateId = m.AggregateId, status = m.Status.ToString(), attempts = m.Attempts, createdAt = m.CreatedAt, nextAttemptAt = m.NextAttemptAt, deliveredAt = m.DeliveredAt, lastError = m.LastError })
            }, OrderJson.Opts);
        }).RequireStaff(StaffPermission.IntegrationsView, "read");
        a.MapPost("/outbox/{id:guid}/retry", async (HttpContext ctx, ReasonRequest r, AdminAudit audit, AppDbContext db, TimeProvider time, Guid id) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var m = await db.Outbox.FirstOrDefaultAsync(x => x.Id == id && x.Status == OutboxStatus.Dead);
            if (m is null) return Results.NotFound();
            m.Status = OutboxStatus.Pending; m.Attempts = 0; m.NextAttemptAt = time.UtcNow(); m.LastError = null;
            audit.Add(ctx.Staff(), "outbox.retried", "outbox", id, reason, new { status = "Dead" }, new { status = "Pending" });
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireStaff(StaffPermission.IntegrationsAct, "write");
        a.MapGet("/messages/reported", async (HttpContext ctx, AppDbContext db) =>
            Results.Json((await db.Messages.AsNoTracking().Where(m => m.Reported).OrderByDescending(m => m.CreatedAt).Take(100).ToListAsync())
                .Select(m => new { id = m.Id, conversationId = m.ConversationId, sellerId = m.SellerId, direction = m.Direction.ToString(), text = StaffPermissions.Has(ctx.Staff().Role, StaffPermission.ModerationAct) ? m.Text : Mask.Text(m.Text), reason = m.ReportReason, createdAt = m.CreatedAt }), OrderJson.Opts)).RequireStaff(StaffPermission.ModerationView, "read");
        a.MapPost("/messages/{id:guid}/dismiss-report", async (HttpContext ctx, ReasonRequest r, AdminAudit audit, AppDbContext db, Guid id) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == id && x.Reported);
            if (m is null) return Results.NotFound();
            m.Reported = false; m.ReportReason = null;
            if (!await db.Messages.AnyAsync(x => x.ConversationId == m.ConversationId && x.Reported && x.Id != id))
                await db.Conversations.Where(c => c.Id == m.ConversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Flagged, false).SetProperty(c => c.FlagReason, (string?)null));
            audit.Add(ctx.Staff(), "moderation.report_dismissed", "message", id, reason);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireStaff(StaffPermission.ModerationAct, "write");
    }

    public static bool SimulatorEnabled(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Testing");

    /// <summary>Development/Testing only: create realistic orders for the signed-in seller's own products and simulate buyer events. Never mapped in Production.</summary>
    public static void MapSimulator(this WebApplication app, RouteGroupBuilder v1)
    {
        var sim = v1.MapGroup("/seller/dev").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders").RequirePermission(Permission.BrandWorkWrite);

        sim.MapPost("/orders", async (HttpContext ctx, AppDbContext db, OrderIngestService svc, TimeProvider time, SimOrderRequest? req) =>
        {
            var a = ctx.GetAccess()!;
            var scenario = (req?.Scenario ?? "single").ToLowerInvariant();
            var products = await db.ProductsFor(a).AsNoTracking().Include(p => p.Variants).Where(p => p.Status == ProductStatus.Active).OrderBy(p => p.Title).ToListAsync();
            products = products.Where(p => a.CanWorkOnBrand(p.BrandId, true)).ToList();
            if (products.Count == 0) return OrderEndpoints.Problem(422, "no_active_products", "Publish at least one active product first.");
            static long Price(Product p, ProductVariant? v) => v?.PriceCents ?? p.SaleCents ?? p.PriceCents ?? 1999;
            IngestLine Line(Product p, int qty, string tag)
            {
                var v = p.HasVariants ? p.Variants.FirstOrDefault(x => x.Enabled) : null;
                return new($"L-{tag}-{Guid.NewGuid():N}"[..20], p.SellerId, p.BrandId, p.Id, v?.Id, null, qty, Price(p, v), 0, Price(p, v) * qty * 8 / 100);
            }
            var lines = new List<IngestLine>();
            switch (scenario)
            {
                case "single": lines.Add(Line(products[0], 1, "a")); break;
                case "multi": foreach (var p in products.Take(3)) lines.Add(Line(p, 2, "m")); if (lines.Count < 2) lines.Add(Line(products[0], 3, "m")); break;
                case "multi_brand":
                    var perBrand = products.GroupBy(p => p.BrandId).Select(x => x.First()).Take(3).ToList();
                    if (perBrand.Count < 2) return OrderEndpoints.Problem(422, "needs_two_brands", "Create a second brand with an active product to simulate a split order.");
                    foreach (var p in perBrand) lines.Add(Line(p, 1, "b")); break;
                case "variants":
                    var pv = products.FirstOrDefault(p => p.HasVariants);
                    if (pv is null) return OrderEndpoints.Problem(422, "no_variant_product", "No active product with variants.");
                    lines.Add(Line(pv, 2, "v")); break;
                case "out_of_stock":
                    var pick = products[0]; var l0 = Line(pick, 1, "o"); lines.Add(l0 with { Quantity = 9999, TaxCents = 0 }); break;
                default: return Validation.Invalid("scenario", "Use single, multi, multi_brand, variants or out_of_stock.");
            }
            var brandIds = lines.Select(l => l.BrandId).Distinct().ToList();
            var order = new IngestOrder($"MP-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid().ToString("N")[..8]}".ToUpperInvariant(), time.UtcNow(), "USD",
                new IngestBuyer(req?.BuyerName ?? "Test Buyer", "relay-" + Guid.NewGuid().ToString("N")[..8] + "@relay.solvelt.example", "+1 555 010 2030"),
                new IngestAddress(req?.BuyerName ?? "Test Buyer", "10 Market Street", "Apt 4", "Brooklyn", "NY", "11201", "US"), "Standard", scenario == "multi" ? "expedited" : "standard",
                scenario == "single" ? "Happy birthday!" : null, lines, brandIds.Select(b => new IngestShipping(a.SellerId, b, 599)).ToList());
            var r = await svc.CreateAsync(order);
            if (!r.Ok) return Results.Json(new { type = "about:blank", title = "Simulated order rejected", status = 422, code = "invalid_order", errors = r.Errors }, statusCode: 422);
            return Results.Json(new { marketplaceOrderId = order.MarketplaceOrderId, orders = r.Orders.Select(o => new { id = o.Id, number = o.Number, brandId = o.BrandId, oversold = o.Oversold }) }, OrderJson.Opts, statusCode: 201);
        });

        sim.MapPost("/orders/{id:guid}/events", async (HttpContext ctx, AppDbContext db, BuyerEventService buyer, MessageService messages, Guid id, SimEventRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var o = await db.OrdersFor(a).Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) return Results.NotFound();
            db.ChangeTracker.Clear();
            switch (req.Type?.ToLowerInvariant())
            {
                case "cancel_request":
                    var c = await buyer.CancelRequestAsync(o.MarketplaceOrderId, new($"CR-{Guid.NewGuid():N}"[..16], o.Number, req.Reason ?? "changed_mind", req.Text));
                    return Results.Json(new { c.Status, c.Code, c.Orders }, OrderJson.Opts);
                case "return_request":
                    var lines = o.Lines.Where(l => l.ShippedQty > 0 && (req.LineId == null || l.Id.ToString() == req.LineId)).Select(l => new BuyerReturnLine(l.MarketplaceLineId, Math.Max(1, l.ShippedQty - l.ReturnedQty - l.RefundedQty))).ToList();
                    var rr = await buyer.ReturnRequestAsync(o.MarketplaceOrderId, new($"RT-{Guid.NewGuid():N}"[..16], o.Number, req.Reason ?? "not_as_described", req.Text, lines));
                    return Results.Json(new { rr.Status, rr.Code, rr.Message, rr.Orders }, OrderJson.Opts);
                case "delivered":
                    var d = await buyer.DeliveryAsync(o.MarketplaceOrderId, new(o.Number, null, null));
                    return Results.Json(new { d.Status, d.Code, d.Orders }, OrderJson.Opts);
                case "message":
                    var m = await messages.IngestBuyerMessageAsync(new($"MSG-{Guid.NewGuid():N}"[..20], $"CONV-{o.Id:N}"[..20], "order", o.MarketplaceOrderId, o.Number, null, o.BuyerName, req.Text ?? "Hi, where is my order?", null));
                    return m.Status >= 400 ? OrderEndpoints.Problem(m.Status, m.Code, m.Message) : Results.Json(new { conversationId = m.Conversation!.Id, messageId = m.Message2!.Id }, OrderJson.Opts);
                default: return Validation.Invalid("type", "Use cancel_request, return_request, delivered or message.");
            }
        });

        sim.MapPost("/messages", async (HttpContext ctx, AppDbContext db, MessageService messages, SimMessageRequest req) =>
        {
            var a = ctx.GetAccess()!;
            if (req.ProductId is null) return Validation.Invalid("productId", "productId is required.");
            var p = await db.ProductsFor(a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ProductId);
            if (p is null || !a.CanWorkOnBrand(p.BrandId, true)) return Results.NotFound();
            var m = await messages.IngestBuyerMessageAsync(new($"MSG-{Guid.NewGuid():N}"[..20], $"PQ-{Guid.NewGuid():N}"[..20], "product", null, null, p.Id, req.BuyerName ?? "Curious Buyer", req.Text ?? "Does this come in other colours?", null));
            return m.Status >= 400 ? OrderEndpoints.Problem(m.Status, m.Code, m.Message) : Results.Json(new { conversationId = m.Conversation!.Id, messageId = m.Message2!.Id }, OrderJson.Opts, statusCode: 201);
        });

        sim.MapPost("/messages/{messageId:guid}/report", async (HttpContext ctx, AppDbContext db, MessageService messages, Guid messageId, ReportRequest? req) =>
        {
            var a = ctx.GetAccess()!;
            var m = await db.Messages.AsNoTracking().Where(x => x.Id == messageId && x.Direction == MessageDirection.Buyer && x.MarketplaceMessageId != null).FirstOrDefaultAsync();
            if (m is null || await db.ConversationsFor(a).AllAsync(c => c.Id != m.ConversationId)) return Results.NotFound();
            await messages.ReportAsync(m.MarketplaceMessageId!, req?.Reason ?? "abusive");
            return Results.NoContent();
        });

        // delivery of the outbox on demand + a look at what the (dev) marketplace received
        var dev = app.MapGroup("/api/dev").AllowAnonymous();
        dev.MapGet("/outbox", (OutboxSink sink) => Results.Json(sink.All().Select(d => new { d.Id, d.Type, body = d.Body, d.Timestamp, d.Signature, at = d.At }), OrderJson.Opts));
        dev.MapDelete("/outbox", (OutboxSink sink) => { sink.Clear(); return Results.NoContent(); });
        dev.MapPost("/outbox/dispatch", async (OutboxDispatcher d) => Results.Ok(new { delivered = await d.DispatchDueAsync() }));
        dev.MapPost("/maintenance/run", async (OrderMaintenance m) => Results.Ok(await m.RunAsync()));
    }
}
