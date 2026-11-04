using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Shipping;

public static class ShippingLabelEndpoints
{
    private static async Task<SellerOrder?> LoadOrder(AppDbContext db, OrderActions act, AccountAccess a, Guid id, bool write)
    {
        var o = await act.Full(db.OrdersFor(a)).FirstOrDefaultAsync(x => x.Id == id);
        return o is not null && a.CanWorkOnBrand(o.BrandId, write) ? o : null;
    }

    private static async Task<ShippingLabel?> LoadLabel(AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var l = await db.ShippingLabels.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId);
        return l is not null && a.CanWorkOnBrand(l.BrandId, write) ? l : null;
    }

    private static IQueryable<ShippingLabel> LabelsFor(AppDbContext db, AccountAccess a)
    {
        var q = db.ShippingLabels.Where(l => l.SellerId == a.SellerId);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(l => ids.Contains(l.BrandId));
    }

    /// <summary>Like the order endpoints' result mapping, but keeps the structured details (requote, spend limit, customs/address flags) in <c>errors</c>.</summary>
    private static IResult R(Res r) => r.Ok ? OrderEndpoints.ToResult(r) : Results.Json(new { type = "about:blank", title = r.Message, status = r.Status, code = r.Code, errors = r.Data }, OrderJson.Opts, statusCode: r.Status);

    public static void MapShippingLabels(this RouteGroupBuilder g)
    {
        var grp = g.MapGroup("/seller/shipping").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("shipping").RequirePermission(Permission.BrandWorkRead);
        var write = (RouteHandlerBuilder b) => b.RequirePermission(Permission.BrandWorkWrite);

        // buyer address check
        write(grp.MapPost("/orders/{id:guid}/validate-address", async (HttpContext ctx, AppDbContext db, OrderActions act, LabelService svc, Guid id) =>
        {
            var o = await LoadOrder(db, act, ctx.GetAccess()!, id, true);
            if (o is null) return Results.NotFound();
            var (err, check) = await svc.CheckBuyerAddressAsync(o);
            return err is not null ? R(err) : Results.Json(new { status = check!.Status, message = check.Message, suggested = check.Suggested is null ? null : new { line1 = check.Suggested.Line1, city = check.Suggested.City } }, OrderJson.Opts);
        }));

        write(grp.MapPost("/orders/{id:guid}/rates", async (HttpContext ctx, AppDbContext db, OrderActions act, LabelService svc, Guid id, RatesRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var o = await LoadOrder(db, act, a, id, true);
            return o is null ? Results.NotFound() : R(await svc.QuoteAsync(o, a.UserId, req ?? new RatesRequest(null, null, null, null)));
        }));

        write(grp.MapPost("/orders/{id:guid}/labels", async (HttpContext ctx, AppDbContext db, OrderActions act, LabelService svc, Guid id, BuyRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var o = await LoadOrder(db, act, a, id, true);
            if (o is null) return Results.NotFound();
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            req = req with { IdempotencyKey = string.IsNullOrWhiteSpace(key) ? req.IdempotencyKey : key };
            var r = await svc.BuyAsync(o, a.UserId, req);
            return r.Ok ? Results.Json(r.Data, OrderJson.Opts, statusCode: r.Data is { } d && d.GetType().GetProperty("replayed")?.GetValue(d) is true ? 200 : 201) : R(r);
        }));

        write(grp.MapPost("/labels/bulk", async (HttpContext ctx, LabelService svc, BulkRequest req) => R(await svc.BulkAsync(ctx.GetAccess()!, req))));

        write(grp.MapPost("/orders/{id:guid}/returns/{returnId:guid}/label", async (HttpContext ctx, AppDbContext db, OrderActions act, ReturnLabelService svc, Guid id, Guid returnId, ReturnLabelRequest req) =>
        {
            var a = ctx.GetAccess()!;
            var o = await LoadOrder(db, act, a, id, true);
            if (o is null) return Results.NotFound();
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var r = await svc.BuyAsync(o, returnId, a.UserId, (req ?? new ReturnLabelRequest(null, null, null, null, null)) with { IdempotencyKey = string.IsNullOrWhiteSpace(key) ? req?.IdempotencyKey : key });
            return r.Ok ? Results.Json(r.Data, OrderJson.Opts, statusCode: 201) : R(r);
        }));

        grp.MapGet("/orders/{id:guid}/labels", async (HttpContext ctx, AppDbContext db, LabelService svc, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            if (!await db.OrdersFor(a).AnyAsync(o => o.Id == id)) return Results.NotFound();
            var rows = await LabelsFor(db, a).AsNoTracking().Where(l => l.OrderId == id && l.Status != LabelStatus.Failed && l.Status != LabelStatus.Purchasing).OrderBy(l => l.CreatedAt).ToListAsync();
            return Results.Json(new { items = rows.Select(svc.View) }, OrderJson.Opts);
        });

        grp.MapGet("/labels", async (HttpContext ctx, AppDbContext db, LabelService svc, string? status, string? kind, bool? unprinted, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var q = LabelsFor(db, a).AsNoTracking().Where(l => l.Status != LabelStatus.Failed && l.Status != LabelStatus.Purchasing);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LabelStatus>(status, true, out var st)) q = q.Where(l => l.Status == st);
            if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<LabelKind>(kind, true, out var k)) q = q.Where(l => l.Kind == k);
            if (unprinted == true) q = q.Where(l => l.PrintedAt == null && l.Status == LabelStatus.Active);
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var rows = await q.OrderByDescending(l => l.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            var oids = rows.Select(l => l.OrderId).Distinct().ToList();
            var nums = await db.Orders.AsNoTracking().Where(o => oids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number);
            return Results.Json(new { total = await q.CountAsync(), page = p, pageSize = size, items = rows.Select(l => new { label = svc.View(l), orderNumber = nums.GetValueOrDefault(l.OrderId) }) }, OrderJson.Opts);
        });

        grp.MapGet("/labels/{id:guid}", async (HttpContext ctx, AppDbContext db, LabelService svc, Guid id) =>
        {
            var l = await LoadLabel(db, ctx.GetAccess()!, id, false);
            return l is null ? Results.NotFound() : Results.Json(svc.View(l), OrderJson.Opts);
        });

        // only through this authorised endpoint (or the signed return-label link): labels are never public
        grp.MapGet("/labels/{id:guid}/file", async (HttpContext ctx, AppDbContext db, IFileStorage storage, Guid id, bool? download) =>
        {
            var l = await LoadLabel(db, ctx.GetAccess()!, id, false);
            if (l?.LabelKey is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(l.LabelKey);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers.ContentDisposition = $"{(download == true ? "attachment" : "inline")}; filename=\"label-{l.TrackingNumber}.{LabelFormats.Extension(l.LabelFormat)}\"";
            return Results.Stream(s, l.LabelContentType ?? "application/octet-stream");
        });

        write(grp.MapPost("/labels/{id:guid}/printed", async (HttpContext ctx, AppDbContext db, TimeProvider time, Guid id) =>
        {
            var l = await LoadLabel(db, ctx.GetAccess()!, id, true);
            if (l is null) return Results.NotFound();
            l.PrintedAt ??= time.UtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(new { printedAt = l.PrintedAt });
        }));

        write(grp.MapPost("/labels/{id:guid}/void", async (HttpContext ctx, AppDbContext db, LabelService svc, Guid id, VoidRequest? req) =>
        {
            var a = ctx.GetAccess()!;
            var l = await LoadLabel(db, a, id, true);
            return l is null ? Results.NotFound() : R(await svc.VoidAsync(l, a.UserId, req?.Reason));
        }));

        // tracking timeline of an order: carrier shipments, return parcels and labels
        grp.MapGet("/orders/{id:guid}/tracking", async (HttpContext ctx, AppDbContext db, LabelService svc, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var o = await db.OrdersFor(a).AsNoTracking().Include(x => x.Shipments).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null) return Results.NotFound();
            var sids = o.Shipments.Select(s => s.Id).ToList();
            var rets = await db.ReturnShipments.AsNoTracking().Where(r => r.OrderId == id).ToListAsync();
            var rids = rets.Select(r => r.Id).ToList();
            var events = await db.TrackingEvents.AsNoTracking().Where(e => (e.ShipmentId != null && sids.Contains(e.ShipmentId.Value)) || (e.ReturnShipmentId != null && rids.Contains(e.ReturnShipmentId.Value))).OrderBy(e => e.OccurredAt).ToListAsync();
            var labels = await LabelsFor(db, a).AsNoTracking().Where(l => l.OrderId == id && l.Status != LabelStatus.Failed && l.Status != LabelStatus.Purchasing).ToListAsync();
            object Ev(Guid? sid, Guid? rid) => events.Where(e => (sid != null && e.ShipmentId == sid) || (rid != null && e.ReturnShipmentId == rid)).Select(e => new { status = e.Status, label = TrackingStatuses.Label(e.Status), detail = e.Detail, location = e.Location, at = e.OccurredAt });
            return Results.Json(new
            {
                needsAttention = o.NeedsAttention, attentionReason = o.AttentionReason?.Replace("tracking:", ""),
                shipments = o.Shipments.OrderBy(s => s.ShippedAt).Select(s => new
                {
                    id = s.Id, method = s.Method, carrier = s.Carrier, service = s.Service, trackingNumber = s.TrackingNumber, trackingUrl = Orders.Tracking.Url(s.Carrier, s.TrackingNumber),
                    status = s.TrackingStatus ?? "unknown", statusLabel = TrackingStatuses.Label(s.TrackingStatus), statusDetail = s.TrackingStatusDetail, eta = s.Eta, deliveredAt = s.DeliveredAt,
                    live = s.TrackingRegisteredAt != null && s.TrackingStoppedAt == null, labelId = s.LabelId, events = Ev(s.Id, null)
                }),
                returns = rets.Select(r => new
                {
                    id = r.Id, returnId = r.ReturnId, carrier = r.Carrier, service = r.Service, trackingNumber = r.TrackingNumber, trackingUrl = Orders.Tracking.Url(r.Carrier, r.TrackingNumber),
                    status = r.TrackingStatus ?? "unknown", statusLabel = TrackingStatuses.Label(r.TrackingStatus), eta = r.Eta, deliveredAt = r.DeliveredAt, labelId = r.LabelId, events = Ev(null, r.Id)
                }),
                labels = labels.Select(svc.View)
            }, OrderJson.Opts);
        });

        write(grp.MapPost("/orders/{id:guid}/attention/dismiss", async (HttpContext ctx, AppDbContext db, TimeProvider time, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var o = await db.OrdersFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) return Results.NotFound();
            if (o.NeedsAttention) { o.NeedsAttention = false; o.AttentionReason = null; o.AttentionAt = null; db.AddEvent(o.Id, "shipment.attention_dismissed", Party.Seller, a.UserId, "Marked as handled", time.UtcNow()); await db.SaveChangesAsync(); }
            return Results.Ok(new { needsAttention = false });
        }));

        grp.MapGet("/summary", async (HttpContext ctx, AppDbContext db, IOptions<ShippingOptions> opt, LabelService svc) =>
        {
            var a = ctx.GetAccess()!;
            var labels = LabelsFor(db, a);
            return Results.Json(new
            {
                labelsToPrint = await labels.CountAsync(l => l.Status == LabelStatus.Active && l.PrintedAt == null && l.Kind == LabelKind.Outbound),
                exceptions = await db.OrdersFor(a).CountAsync(o => o.NeedsAttention),
                refundsPending = await labels.CountAsync(l => l.Status == LabelStatus.Voiding),
                refundsFailed = await labels.CountAsync(l => l.Status == LabelStatus.RefundFailed),
                spend = new { limitCents = opt.Value.SpendLimitCentsPerMonth, spentCents = await svc.SpentThisMonthAsync(a.SellerId) }
            }, OrderJson.Opts);
        });

        // signed, short-lived link (return labels handed to the marketplace): no login, nothing else is reachable with it
        g.MapGet("/shipping/public/labels/{id:guid}", async (HttpContext ctx, AppDbContext db, IFileStorage storage, LabelLinks links, Guid id, long exp, string? sig) =>
        {
            if (!links.Valid(id, exp, sig)) return Results.NotFound();
            var l = await db.ShippingLabels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Status != LabelStatus.Voided && x.Status != LabelStatus.Failed);
            if (l?.LabelKey is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(l.LabelKey);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"return-label.{LabelFormats.Extension(l.LabelFormat)}\"";
            return Results.Stream(s, l.LabelContentType ?? "application/octet-stream");
        }).AllowAnonymous().RequireRateLimiting("shipping-webhooks");
    }
}

public static class ShippingAdminEndpoints
{
    public static void MapShippingAdmin(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/admin/shipping").RequireAllowedIp();

        object LabelRow(ShippingLabel l) => new
        {
            l.Id, l.SellerId, l.OrderId, kind = l.Kind.ToString(), status = l.Status.ToString(), l.Carrier, l.Service, l.TrackingNumber, costCents = l.CostCents, l.Currency, l.CreatedAt, l.PurchasedAt,
            l.RefundStatus, l.RefundError, l.RefundAttempts, l.RefundRequestedAt, l.NextRefundCheckAt, l.ProviderTransactionId, l.ProviderRefundId, l.Error
        };

        p.AGet("/sellers/{id:guid}/shipments", async (Guid id, int? page, int? pageSize, AppDbContext db) =>
        {
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = from s in db.Shipments.AsNoTracking() join o in db.Orders.AsNoTracking() on s.OrderId equals o.Id where o.SellerId == id select new { s, o.Number };
            var rows = await q.OrderByDescending(x => x.s.ShippedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new
            {
                total = await q.CountAsync(), page = pg, pageSize = size,
                items = rows.Select(x => new { x.s.Id, x.s.OrderId, orderNumber = x.Number, x.s.Carrier, x.s.Service, x.s.TrackingNumber, x.s.Method, status = x.s.TrackingStatus, x.s.ShippedAt, x.s.DeliveredAt, x.s.LabelId, x.s.LastTrackingAt, x.s.TrackingRegisteredAt, x.s.TrackingStoppedAt })
            });
        }, StaffPermission.ShippingView);

        p.AGet("/sellers/{id:guid}/labels", async (Guid id, string? status, int? page, int? pageSize, AppDbContext db) =>
        {
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.ShippingLabels.AsNoTracking().Where(l => l.SellerId == id);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LabelStatus>(status, true, out var st)) q = q.Where(l => l.Status == st);
            var rows = await q.OrderByDescending(l => l.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(LabelRow) });
        }, StaffPermission.ShippingView);

        // refunds that are pending or failed, across all sellers
        p.AGet("/refunds", async (string? state, int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.ShippingLabels.AsNoTracking().Where(l => l.Status == LabelStatus.Voiding || l.Status == LabelStatus.RefundFailed);
            if (state == "pending") q = q.Where(l => l.Status == LabelStatus.Voiding);
            else if (state == "failed") q = q.Where(l => l.Status == LabelStatus.RefundFailed);
            else if (!string.IsNullOrEmpty(state)) return Validation.Invalid("state", "Use pending or failed.");
            var rows = await q.OrderBy(l => l.RefundRequestedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(LabelRow) });
        }, StaffPermission.ShippingView);

        p.AGet("/exceptions", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = from o in db.Orders.AsNoTracking() where o.NeedsAttention select new { o.Id, o.SellerId, o.Number, o.AttentionReason, o.AttentionAt };
            var rows = await q.OrderByDescending(x => x.AttentionAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(x => new { orderId = x.Id, x.SellerId, orderNumber = x.Number, reason = x.AttentionReason?.Replace("tracking:", ""), since = x.AttentionAt }) });
        }, StaffPermission.ShippingView);

        p.AGet("/webhooks/failures", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.ShippingWebhookEvents.AsNoTracking().Where(w => w.Outcome != "applied" && w.Outcome != "duplicate" && w.Outcome != "stale" && w.Outcome != "received");
            var rows = await q.OrderByDescending(w => w.ReceivedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(w => new { w.Id, w.Provider, w.EventId, w.TrackingNumber, w.Outcome, w.Error, w.ReceivedAt }) });
        }, StaffPermission.ShippingView);

        p.APost("/labels/{id:guid}/refund/retry", async (Guid id, Solvelt.Api.Payments.HoldRequest r, HttpContext ctx, AppDbContext db, LabelService svc, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var l = await db.ShippingLabels.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (l.Status is not (LabelStatus.RefundFailed or LabelStatus.Voiding)) return AdminHttp.Err(409, "not_retryable", "Only labels with a pending or failed refund can be retried.");
            if (l.Status == LabelStatus.RefundFailed) { l.Status = LabelStatus.Active; l.RefundStatus = "pending"; }
            var res = await svc.RequestRefundAsync(l, "Label refund retried by Solvelt staff: " + reason);
            await audit.WriteAsync(ctx.Staff(), "shipping.refund_retried", "label", id, reason, null, new { result = res.Code });
            return res.Ok ? Results.Ok(new { status = l.Status.ToString(), refundStatus = l.RefundStatus }) : AdminHttp.Err(res.Status, res.Code, res.Message);
        }, StaffPermission.ShippingRetry);
    }
}

public static class ShippingInternalEndpoints
{
    private static IResult Err(int status, string code, string message) => Results.Json(new { type = "about:blank", title = message, status, code }, statusCode: status);

    public static void MapShippingInternal(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1").RequireRateLimiting("internal").AllowAnonymous();

        // read-only shipping settings for checkout (signed GET): profiles, zones, rates, thresholds, handling time. No street addresses leave the portal.
        g.MapGet("/shipping/settings", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, IOptions<OrderOptions> orders, Guid sellerId, Guid? brandId) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return Err(v.Status, v.Code, v.Message);
            if (!await db.Sellers.AnyAsync(s => s.Id == sellerId)) return Results.NotFound();
            if (brandId is { } b && !await db.Brands.AnyAsync(x => x.Id == b && x.SellerId == sellerId)) return Results.NotFound();
            var scopes = new List<Guid?> { null }; if (brandId is { } b2) scopes.Add(b2);
            var settings = await db.SellerShippingSettings.AsNoTracking().Where(s => s.SellerId == sellerId).ToListAsync();
            var eff = settings.FirstOrDefault(s => brandId != null && s.BrandId == brandId && s.HandlingDays != null) ?? settings.FirstOrDefault(s => s.BrandId == null && s.HandlingDays != null);
            var profiles = (await db.ShippingProfiles.AsNoTracking().Where(p => p.SellerId == sellerId && !p.Archived).OrderBy(p => p.CreatedAt).ToListAsync()).Where(p => scopes.Contains(p.BrandId)).ToList();
            var from = await db.ShipFromAddresses.AsNoTracking().Where(a => a.SellerId == sellerId && !a.Archived && a.IsDefault && (a.ScopeKey == (brandId ?? Guid.Empty) || a.ScopeKey == Guid.Empty)).OrderByDescending(a => a.ScopeKey).FirstOrDefaultAsync();
            return Results.Json(new
            {
                sellerId, brandId, handlingDays = eff?.HandlingDays ?? orders.Value.DefaultHandlingDays,
                shipsFrom = from is null ? null : new { country = from.Country, region = from.Region },
                profiles = profiles.Select(ShippingSettingsEndpoints.ProfileView)
            }, OrderJson.Opts);
        });

        // a fresh short-lived link to a return label (the first one expires)
        g.MapPost("/returns/{marketplaceReturnId}/label-link", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, LabelLinks links, string marketplaceReturnId) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return Err(v.Status, v.Code, v.Message);
            var l = await (from lb in db.ShippingLabels.AsNoTracking() join r in db.Returns.AsNoTracking() on lb.ReturnId equals r.Id
                           where r.MarketplaceReturnId == marketplaceReturnId && lb.Kind == LabelKind.Return && lb.Status == LabelStatus.Active select lb).FirstOrDefaultAsync();
            if (l is null) return Results.NotFound();
            var (url, exp) = links.Create(l.Id);
            return Results.Json(new { labelUrl = url, labelUrlExpiresAt = exp, trackingNumber = l.TrackingNumber, carrier = l.Carrier }, OrderJson.Opts);
        });
    }
}

/// <summary>Development / test helpers (fake provider only): push a tracking update through the real webhook pipeline and run the maintenance job.</summary>
public static class ShippingDevEndpoints
{
    public record DevTracking(string? TrackingNumber, string? Status, DateTime? At, string? Detail, string? EventId);

    public static void MapShippingDev(this WebApplication app)
    {
        var g = app.MapGroup("/api/dev/shipping").AllowAnonymous();

        g.MapPost("/tracking", async (DevTracking b, ShippingWebhookService svc, IOptions<ShippingOptions> opt, TimeProvider time) =>
        {
            if (string.IsNullOrWhiteSpace(b.TrackingNumber) || string.IsNullOrWhiteSpace(b.Status) || !TrackingStatuses.All.Contains(b.Status)) return Results.BadRequest(new { error = "trackingNumber and a normalized status are required" });
            var at = (b.At ?? time.GetUtcNow().UtcDateTime).ToUniversalTime();
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "track_updated", event_id = b.EventId ?? "dev-" + Guid.NewGuid().ToString("N"),
                data = new { carrier = "usps", tracking_number = b.TrackingNumber, tracking_status = new { status = ToShippo(b.Status), status_details = b.Detail ?? TrackingStatuses.Label(b.Status), status_date = at.ToString("O"), substatus = new { code = b.Status } }, tracking_history = Array.Empty<object>() }
            });
            var t = time.GetUtcNow().ToUnixTimeSeconds();
            var (status, outcome) = await svc.HandleAsync(body, ShippingWebhookService.Sign(opt.Value.WebhookSecret, t, body), null);
            return Results.Json(new { outcome }, statusCode: status);
        });

        g.MapPost("/run-maintenance", async (ShippingMaintenance m) => Results.Ok(await m.RunAsync()));
    }

    private static string ToShippo(string s) => s switch
    {
        TrackingStatuses.PreTransit => "PRE_TRANSIT", TrackingStatuses.Delivered => "DELIVERED", TrackingStatuses.Failure => "FAILURE", TrackingStatuses.ReturnToSender => "RETURNED",
        TrackingStatuses.Unknown => "UNKNOWN", _ => "TRANSIT"
    };
}
