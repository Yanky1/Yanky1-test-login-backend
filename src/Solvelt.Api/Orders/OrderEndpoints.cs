using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Orders;

public record IdsRequest(List<Guid>? Ids);
public record NoteRequest(string? Text);
public record HoldRequest(int? Version, string? Note);
public record VersionRequest(int? Version);
public record EditTrackingRequest(int? Version, string? Carrier, string? TrackingNumber, string? Service);
public record DeliverRequest(int? Version);

public static class OrderEndpoints
{
    internal static IResult Problem(int status, string code, string title) =>
        Results.Json(new { type = "about:blank", title, status, code }, statusCode: status);

    internal static IResult ToResult(Res r) => r.Ok ? Results.Ok(r.Data ?? new { ok = true }) : Problem(r.Status, r.Code, r.Message);

    private static async Task<SellerOrder?> Load(AppDbContext db, OrderActions act, AccountAccess a, Guid id, bool write)
    {
        var o = await act.Full(db.OrdersFor(a)).FirstOrDefaultAsync(x => x.Id == id);
        return o is not null && (!write || a.CanWorkOnBrand(o.BrandId, true)) ? o : null;
    }

    private static async Task<IResult> Detail(AppDbContext db, AccountAccess a, Guid id, TimeProvider time, IOptions<OrderOptions> opt, int status = 200)
    {
        db.ChangeTracker.Clear();
        var d = await OrderViews.DetailAsync(db, a, id, time.UtcNow(), opt.Value.ManagerRefundLimitCents);
        return d is null ? Results.NotFound() : Results.Json(d, OrderJson.Opts, statusCode: status);
    }

    /// <summary>Runs one action on one order: 404 if it's not in scope, then the action, then answers with the fresh detail.</summary>
    private static async Task<IResult> Act(HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider time, IOptions<OrderOptions> opt, Guid id,
        Func<SellerOrder, AccountAccess, Task<Res>> run)
    {
        var a = ctx.GetAccess()!;
        var o = await Load(db, act, a, id, write: true);
        if (o is null) return Results.NotFound();
        var r = await run(o, a);
        return r.Ok ? await Detail(db, a, id, time, opt) : Problem(r.Status, r.Code, r.Message);
    }

    // ---- filters ------------------------------------------------------------------------------------------------------------
    private static readonly OrderStatus[] Unshipped = [OrderStatus.New, OrderStatus.Accepted, OrderStatus.PartiallyShipped, OrderStatus.OnHold];

    internal static IQueryable<SellerOrder> Tab(IQueryable<SellerOrder> q, string? tab) => tab?.ToLowerInvariant() switch
    {
        "needs_action" => q.Where(o => o.Status == OrderStatus.New || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.PartiallyShipped
            || o.Status == OrderStatus.ReturnRequested || o.Status == OrderStatus.ReturnReceived || (o.CancelRequested && o.Status != OrderStatus.Cancelled)),
        "pending" => q.Where(o => o.Status == OrderStatus.New),
        "shipped" => q.Where(o => o.Status == OrderStatus.Shipped || o.Status == OrderStatus.PartiallyShipped),
        "delivered" => q.Where(o => o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Completed),
        "cancelled" => q.Where(o => o.Status == OrderStatus.Cancelled),
        "returns" => q.Where(o => o.Status == OrderStatus.ReturnRequested || o.Status == OrderStatus.ReturnApproved || o.Status == OrderStatus.ReturnRejected
            || o.Status == OrderStatus.ReturnReceived || o.Status == OrderStatus.Refunded),
        _ => q
    };

    internal static (IQueryable<SellerOrder>? Q, IResult? Err) Filter(AppDbContext db, AccountAccess a, TimeProvider time, string? tab, string? status, Guid? brandId, DateTime? from,
        DateTime? to, bool? late, bool? oversold, string? speed, string? q, string? sort, string? tracking = null)
    {
        var rows = Tab(db.OrdersFor(a), tab);
        if (!string.IsNullOrEmpty(tracking))
        {
            // shipping chips: in transit / exception / delivered (docs/SHIPPING.md)
            switch (tracking.ToLowerInvariant())
            {
                case "in_transit": rows = rows.Where(o => db.Shipments.Any(s => s.OrderId == o.Id && s.DeliveredAt == null && (s.TrackingStatus == "in_transit" || s.TrackingStatus == "out_for_delivery" || s.TrackingStatus == "pre_transit" || s.TrackingStatus == "available_for_pickup"))); break;
                case "exception": rows = rows.Where(o => o.NeedsAttention || db.Shipments.Any(s => s.OrderId == o.Id && (s.TrackingStatus == "failure" || s.TrackingStatus == "return_to_sender"))); break;
                case "delivered": rows = rows.Where(o => db.Shipments.Any(s => s.OrderId == o.Id) && !db.Shipments.Any(s => s.OrderId == o.Id && s.DeliveredAt == null)); break;
                default: return (null, Validation.Invalid("tracking", "Use in_transit, exception or delivered."));
            }
        }
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<OrderStatus>(status, true, out var st)) return (null, Validation.Invalid("status", "Unknown status."));
            rows = rows.Where(o => o.Status == st);
        }
        if (brandId is { } b) rows = rows.Where(o => o.BrandId == b);
        if (from is { } f0) { var f = OrderSupport.Utc(f0); rows = rows.Where(o => o.PlacedAt >= f); }
        if (to is { } t0) { var t = OrderSupport.Utc(t0); rows = rows.Where(o => o.PlacedAt < t); }
        var now = time.UtcNow();
        if (late == true) rows = rows.Where(o => o.ShipByAt < now && Unshipped.Contains(o.Status));
        if (oversold == true) rows = rows.Where(o => o.Oversold);
        if (!string.IsNullOrEmpty(speed)) rows = rows.Where(o => o.ShippingSpeed == speed);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = q.Trim().ToLower();
            var tn = Tracking.Normalize(q);
            rows = rows.Where(o => o.Number.ToLower().Contains(s) || o.BuyerName.ToLower().Contains(s) || o.MarketplaceOrderId.ToLower().Contains(s)
                || db.OrderLines.Any(l => l.OrderId == o.Id && l.Sku != null && l.Sku.ToLower().Contains(s))
                || (tn.Length >= 3 && db.Shipments.Any(sh => sh.OrderId == o.Id && sh.TrackingNumber.Contains(tn))));
        }
        rows = sort switch
        {
            "placed_asc" => rows.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id),
            "shipby_asc" => rows.OrderBy(o => o.ShipByAt).ThenBy(o => o.Id),
            "total_desc" => rows.OrderByDescending(o => o.TotalCents).ThenBy(o => o.Id),
            "total_asc" => rows.OrderBy(o => o.TotalCents).ThenBy(o => o.Id),
            _ => rows.OrderByDescending(o => o.PlacedAt).ThenBy(o => o.Id)
        };
        return (rows, null);
    }

    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static void MapOrderEndpoints(this RouteGroupBuilder g)
    {
        var grp = g.MapGroup("/seller/orders").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders")
            .RequirePermission(Permission.BrandWorkRead);
        var write = (RouteHandlerBuilder b) => b.RequirePermission(Permission.BrandWorkWrite);

        grp.MapGet("", async (HttpContext ctx, AppDbContext db, TimeProvider time, string? tab, string? status, Guid? brandId, DateTime? from, DateTime? to, bool? late, bool? oversold,
            string? speed, string? q, string? sort, int? page, int? pageSize, string? tracking) =>
        {
            var a = ctx.GetAccess()!;
            var (rows, err) = Filter(db, a, time, tab, status, brandId, from, to, late, oversold, speed, q, sort, tracking);
            if (err is not null) return err;
            var total = await rows!.CountAsync();
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var items = await rows!.AsNoTracking().Include(o => o.Lines).Include(o => o.Shipments).AsSplitQuery().Skip((pg - 1) * size).Take(size).ToListAsync();
            var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var now = time.UtcNow();
            return Results.Json(new { items = items.Select(o => OrderViews.ListItem(o, brands.GetValueOrDefault(o.BrandId, ""), now)), total, page = pg, pageSize = size }, OrderJson.Opts);
        });

        grp.MapGet("/counts", async (HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var all = db.OrdersFor(a);
            return Results.Json(new
            {
                needsAction = await Tab(all, "needs_action").CountAsync(), pending = await Tab(all, "pending").CountAsync(), shipped = await Tab(all, "shipped").CountAsync(),
                delivered = await Tab(all, "delivered").CountAsync(), cancelled = await Tab(all, "cancelled").CountAsync(), returns = await Tab(all, "returns").CountAsync(),
                late = await all.CountAsync(o => o.ShipByAt < now && Unshipped.Contains(o.Status)), all = await all.CountAsync(), exceptions = await all.CountAsync(o => o.NeedsAttention)
            }, OrderJson.Opts);
        });

        grp.MapGet("/dashboard", async (HttpContext ctx, AppDbContext db, TimeProvider time, MessageService messages, Guid? brandId) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var today = now.Date;
            var q = db.OrdersFor(a).AsNoTracking();
            if (brandId is { } b) q = q.Where(o => o.BrandId == b);
            var since = today.AddDays(-29);
            var recent = await q.Where(o => o.PlacedAt >= since && o.Status != OrderStatus.Cancelled).Select(o => new { o.PlacedAt, o.ItemsCents, o.DiscountCents, o.RefundedCents }).ToListAsync();
            var daily = Enumerable.Range(0, 30).Select(i => since.AddDays(i)).Select(d => new
            {
                date = d.ToString("yyyy-MM-dd"), orders = recent.Count(r => r.PlacedAt.Date == d),
                salesCents = recent.Where(r => r.PlacedAt.Date == d).Sum(r => r.ItemsCents - r.DiscountCents - r.RefundedCents)
            }).ToList();
            var shippedWindow = await q.Where(o => o.FirstShippedAt != null && o.FirstShippedAt >= since).Select(o => o.ShippedLate).ToListAsync();
            var placed30 = await q.CountAsync(o => o.PlacedAt >= since);
            var cancelled30 = await q.CountAsync(o => o.PlacedAt >= since && o.Status == OrderStatus.Cancelled);
            var convs = db.ConversationsFor(a);
            if (brandId is { } b2) convs = convs.Where(c => c.BrandId == b2);
            var respSince = now.AddDays(-30);
            var resp = await db.Messages.AsNoTracking().Where(m => m.Direction == MessageDirection.Buyer && m.CreatedAt >= respSince && m.RespondedAt != null
                && convs.Any(c => c.Id == m.ConversationId)).Select(m => new { m.CreatedAt, m.RespondedAt }).ToListAsync();
            var dueH = ctx.RequestServices.GetRequiredService<IOptions<OrderOptions>>().Value.ReplyDueHours;
            return Results.Json(new
            {
                todayOrders = await q.CountAsync(o => o.PlacedAt >= today), toShip = await q.CountAsync(o => o.Status == OrderStatus.Accepted || o.Status == OrderStatus.PartiallyShipped || o.Status == OrderStatus.New),
                late = await q.CountAsync(o => o.ShipByAt < now && Unshipped.Contains(o.Status)), returnsOpen = await q.CountAsync(o => o.Status == OrderStatus.ReturnRequested || o.Status == OrderStatus.ReturnApproved || o.Status == OrderStatus.ReturnReceived),
                shipping = new
                {
                    labelsToPrint = await db.ShippingLabels.CountAsync(l => l.SellerId == a.SellerId && l.Status == LabelStatus.Active && l.PrintedAt == null && l.Kind == LabelKind.Outbound && (brandId == null || l.BrandId == brandId) && (a.AllBrands || a.Role >= AccountRole.Admin || a.BrandIds.Contains(l.BrandId))),
                    exceptions = await q.CountAsync(o => o.NeedsAttention)
                },
                sales30Cents = daily.Sum(d => d.salesCents), orders30 = placed30, daily,
                lateShipmentRate = shippedWindow.Count == 0 ? 0 : Math.Round(100.0 * shippedWindow.Count(x => x) / shippedWindow.Count, 1),
                cancellationRate = placed30 == 0 ? 0 : Math.Round(100.0 * cancelled30 / placed30, 1),
                messages = new
                {
                    unread = await messages.Unread(convs, a.UserId).CountAsync(), needsReply = await messages.NeedsReply(convs).CountAsync(), overdue = await messages.Overdue(convs).CountAsync(),
                    avgResponseHours = resp.Count == 0 ? (double?)null : Math.Round(resp.Average(r => (r.RespondedAt!.Value - r.CreatedAt).TotalHours), 1),
                    repliedInTimePct = resp.Count == 0 ? (double?)null : Math.Round(100.0 * resp.Count(r => (r.RespondedAt!.Value - r.CreatedAt).TotalHours <= dueH) / resp.Count, 1)
                }
            }, OrderJson.Opts);
        });

        grp.MapGet("/export.csv", async (HttpContext ctx, AppDbContext db, TimeProvider time, string? tab, string? status, Guid? brandId, DateTime? from, DateTime? to, bool? late,
            bool? oversold, string? speed, string? q, string? sort) =>
        {
            var a = ctx.GetAccess()!;
            var (rows, err) = Filter(db, a, time, tab, status, brandId, from, to, late, oversold, speed, q, sort);
            if (err is not null) return err;
            var list = await rows!.AsNoTracking().Include(o => o.Lines).Include(o => o.Shipments).AsSplitQuery().Take(10000).ToListAsync();
            var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var sb = new StringBuilder("order_number,marketplace_order_id,placed_at,status,brand,buyer,ship_to_city,ship_to_region,ship_to_country,items,skus,total,fee,net,refunded,ship_by,carrier,tracking_number\r\n");
            foreach (var o in list)
                sb.Append(string.Join(',', new[]
                {
                    o.Number, o.MarketplaceOrderId, o.PlacedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"), o.Status.ToString(), brands.GetValueOrDefault(o.BrandId, ""), o.BuyerName, o.ShipCity, o.ShipRegion,
                    o.ShipCountry, o.Lines.Sum(l => l.Quantity).ToString(), string.Join(' ', o.Lines.Select(l => l.Sku)), Money(o.TotalCents), Money(o.FeeCents), Money(o.NetCents), Money(o.RefundedCents),
                    o.ShipByAt.ToString("yyyy-MM-dd"), string.Join(' ', o.Shipments.Select(s => s.Carrier).Distinct()), string.Join(' ', o.Shipments.Select(s => s.TrackingNumber))
                }.Select(CsvFormat.Cell))).Append("\r\n");
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "orders.csv");
        });

        grp.MapGet("/tracking-template.csv", () => Results.File(Encoding.UTF8.GetBytes("order_number,carrier,tracking_number,ship_date,sku,quantity\r\nSO-2026-000001,UPS,1Z999AA10123456784,2026-01-31,,\r\n"), "text/csv; charset=utf-8", "tracking-template.csv"));

        grp.MapGet("/{id:guid}", async (HttpContext ctx, AppDbContext db, TimeProvider time, IOptions<OrderOptions> opt, Guid id) =>
            await Detail(db, ctx.GetAccess()!, id, time, opt));

        grp.MapGet("/{id:guid}/lines/{lineId:guid}/image", async (HttpContext ctx, AppDbContext db, IFileStorage storage, Guid id, Guid lineId) =>
        {
            var a = ctx.GetAccess()!;
            var line = await db.OrdersFor(a).Where(o => o.Id == id).SelectMany(o => o.Lines).Where(l => l.Id == lineId).Select(l => new { l.ImageKey, l.ImageContentType }).FirstOrDefaultAsync();
            if (line?.ImageKey is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(line.ImageKey);
            return s is null ? Results.NotFound() : Results.Stream(s, line.ImageContentType ?? "application/octet-stream");
        });

        grp.MapGet("/{id:guid}/packing-slip", async (HttpContext ctx, AppDbContext db, Guid id) => PackingSlip(await PackingSlips(db, ctx.GetAccess()!, [id]), single: true));
        grp.MapPost("/bulk/packing-slips", async (HttpContext ctx, AppDbContext db, IdsRequest req) =>
        {
            if (req.Ids is null or { Count: 0 } or { Count: > 50 }) return Validation.Invalid("ids", "Choose between 1 and 50 orders.");
            return PackingSlip(await PackingSlips(db, ctx.GetAccess()!, req.Ids.Distinct().ToList()), single: false);
        });

        write(grp.MapPost("/bulk/accept", async (HttpContext ctx, AppDbContext db, OrderActions act, IdsRequest req) =>
        {
            var a = ctx.GetAccess()!;
            if (req.Ids is null or { Count: 0 } or { Count: > 100 }) return Validation.Invalid("ids", "Choose between 1 and 100 orders.");
            var results = new List<object>();
            foreach (var id in req.Ids.Distinct())
            {
                var o = await Load(db, act, a, id, write: true);
                if (o is null) { results.Add(new { id, ok = false, code = "not_found", message = "Order not found." }); continue; }
                var r = await act.AcceptAsync(o, a.UserId, null);
                db.ChangeTracker.Clear();
                results.Add(new { id, number = o.Number, ok = r.Ok, code = r.Code, message = r.Message });
            }
            return Results.Json(new { results }, OrderJson.Opts);
        }));

        // ---- actions
        write(grp.MapPost("/{id:guid}/accept", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, VersionRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.AcceptAsync(o, a.UserId, r?.Version))));
        write(grp.MapPost("/{id:guid}/hold", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, HoldRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.HoldAsync(o, a.UserId, r?.Version, r?.Note))));
        write(grp.MapPost("/{id:guid}/resume", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, VersionRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.ResumeAsync(o, a.UserId, r?.Version))));
        write(grp.MapPost("/{id:guid}/ship", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, ShipRequest r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.ShipAsync(o, a.UserId, r))));
        write(grp.MapPut("/{id:guid}/shipments/{shipmentId:guid}", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid shipmentId, EditTrackingRequest r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.EditTrackingAsync(o, shipmentId, a.UserId, r.Version, r.Carrier, r.TrackingNumber, r.Service))));
        write(grp.MapPost("/{id:guid}/deliver", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, DeliverRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.MarkDeliveredAsync(o, a.UserId, r?.Version))));
        write(grp.MapPost("/{id:guid}/cancel", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, CancelRequest r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.CancelAsync(o, a.UserId, r))));
        write(grp.MapPost("/{id:guid}/cancellation-requests/{cid:guid}/approve", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid cid, CancelRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.DecideCancellationAsync(o, cid, a.UserId, true, r ?? new(null, null, null, null)))));
        write(grp.MapPost("/{id:guid}/cancellation-requests/{cid:guid}/reject", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid cid, CancelRequest? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.DecideCancellationAsync(o, cid, a.UserId, false, r ?? new(null, null, null, null)))));
        write(grp.MapPost("/{id:guid}/returns/{rid:guid}/approve", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid rid, ReturnDecision? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.DecideReturnAsync(o, rid, a.UserId, true, r ?? new(null, null, null, null)))));
        write(grp.MapPost("/{id:guid}/returns/{rid:guid}/reject", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid rid, ReturnDecision r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.DecideReturnAsync(o, rid, a.UserId, false, r))));
        write(grp.MapPost("/{id:guid}/returns/{rid:guid}/receive", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, Guid rid, ReturnDecision? r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.ReceiveReturnAsync(o, rid, a.UserId, r ?? new(null, null, null, null)))));
        write(grp.MapPost("/{id:guid}/notes", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, NoteRequest r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.AddNoteAsync(o, a.UserId, r.Text))));

        // refunds: Owner/Admin always; Managers only up to the configured limit (default 0 = never); Viewers never (BrandWorkWrite)
        write(grp.MapPost("/{id:guid}/refunds", (HttpContext ctx, AppDbContext db, OrderActions act, TimeProvider t, IOptions<OrderOptions> opt, Guid id, RefundRequest r) =>
            Act(ctx, db, act, t, opt, id, (o, a) => act.RefundAsync(o, a.UserId, r, amount => a.Has(Permission.OrderRefund) || (a.Role == AccountRole.Manager && amount <= opt.Value.ManagerRefundLimitCents))))
        );
        write(grp.MapPost("/{id:guid}/refunds/preview", async (HttpContext ctx, AppDbContext db, OrderActions act, Guid id, RefundRequest r) =>
        {
            var a = ctx.GetAccess()!;
            var o = await Load(db, act, a, id, write: true);
            if (o is null) return Results.NotFound();
            var (err, amount, shipping, shippingTax, lines) = await act.PlanRefundAsync(o, r);
            if (err is not null) return Problem(err.Status, err.Code, err.Message);
            return Results.Json(new { amountCents = amount, shippingCents = shipping, shippingTaxCents = shippingTax, taxCents = lines.Sum(l => l.Tax) + shippingTax, maxCents = o.TotalCents - o.RefundedCents, lines = lines.Select(l => new { lineId = l.Line.Id, quantity = l.Qty, amountCents = l.Cents, taxCents = l.Tax }) }, OrderJson.Opts);
        }));

        // CSV tracking import: apply=false (default) validates only; apply=true ships the valid rows
        write(grp.MapPost("/import-tracking", async (HttpContext ctx, AppDbContext db, OrderActions act, bool? apply) =>
        {
            var a = ctx.GetAccess()!;
            if (!ctx.Request.HasFormContentType) return Validation.Invalid("file", "Upload a CSV file.");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Validation.Invalid("file", "Upload a CSV file.");
            if (file.Length > CsvFormat.MaxBytes) return Validation.Invalid("file", "The file is too large (1 MB max).");
            string text; using (var rd = new StreamReader(file.OpenReadStream(), Encoding.UTF8)) text = await rd.ReadToEndAsync();
            List<List<string>> table;
            try { table = CsvFormat.Parse(text); } catch (FormatException) { return Validation.Invalid("file", "The CSV is malformed (unterminated quote)."); }
            if (table.Count == 0) return Validation.Invalid("file", "The file is empty.");
            var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            string[] required = ["order_number", "carrier", "tracking_number"];
            var missing = required.Where(r => !header.Contains(r)).ToList();
            if (missing.Count > 0) return Validation.Invalid("file", "Missing column(s): " + string.Join(", ", missing));
            if (table.Count - 1 > CsvFormat.MaxRows) return Validation.Invalid("file", $"At most {CsvFormat.MaxRows} rows per upload.");
            string Col(List<string> r, string n) { var i = header.IndexOf(n); return i >= 0 && i < r.Count ? r[i].Trim() : ""; }
            var results = new List<object>(); var validCount = 0;
            for (var i = 1; i < table.Count; i++)
            {
                var row = table[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var num = Col(row, "order_number");
                string? error = null; Guid? orderId = null; ShipRequest? req = null; SellerOrder? o = null;
                o = await act.Full(db.OrdersFor(a)).FirstOrDefaultAsync(x => x.Number == num);
                if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) error = "Order not found.";
                else
                {
                    orderId = o.Id;
                    DateTime? shipped = null;
                    if (Col(row, "ship_date") is { Length: > 0 } sd)
                    { if (DateTime.TryParse(sd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) shipped = d; else error = "Invalid ship_date (use YYYY-MM-DD)."; }
                    List<QtyLine>? lines = null;
                    if (error is null && Col(row, "sku") is { Length: > 0 } sku)
                    {
                        var line = o.Lines.FirstOrDefault(l => string.Equals(l.Sku, sku, StringComparison.OrdinalIgnoreCase));
                        if (line is null) error = $"SKU {sku} is not in this order.";
                        else
                        {
                            var qty = Col(row, "quantity") is { Length: > 0 } qs ? (int.TryParse(qs, out var qn) ? qn : -1) : OrderSupport.Unshipped(line);
                            if (qty < 1) error = "Invalid quantity."; else lines = [new QtyLine(line.Id, qty)];
                        }
                    }
                    if (error is null) req = new ShipRequest(null, lines, Col(row, "carrier"), null, Col(row, "tracking_number"), shipped);
                    if (error is null && apply == true)
                    {
                        var r = await act.ShipAsync(o, a.UserId, req!);
                        db.ChangeTracker.Clear();
                        if (!r.Ok) error = r.Message;
                    }
                    else if (error is null)
                    {
                        // validation preview: same rules as the real action, without applying it
                        error = ValidateShip(o, req!);
                    }
                }
                if (error is null) validCount++;
                results.Add(new { row = i + 1, orderNumber = num, orderId, ok = error is null, error, applied = apply == true && error is null });
            }
            return Results.Json(new { applied = apply == true, total = results.Count, valid = validCount, results }, OrderJson.Opts);
        }));
    }

    private static string? ValidateShip(SellerOrder o, ShipRequest req)
    {
        if (o.Status == OrderStatus.New) return "Accept this order before shipping it.";
        if (!OrderStateMachine.CanShip(o.Status)) return $"The order is {OrderStateMachine.Label(o.Status)} and can't be shipped.";
        if (o.CancelRequested) return "The buyer asked to cancel this order.";
        var carrier = Tracking.Carrier(req.Carrier);
        if (carrier is null) return "Unknown carrier (UPS, USPS, FedEx, DHL or Other).";
        if (!Tracking.Valid(carrier, Tracking.Normalize(req.TrackingNumber))) return $"That doesn't look like a {carrier} tracking number.";
        foreach (var w in req.Lines ?? [])
        {
            var l = o.Lines.First(x => x.Id == w.LineId);
            if (w.Quantity > OrderSupport.Unshipped(l)) return $"Only {OrderSupport.Unshipped(l)} of {l.Sku} left to ship.";
        }
        if (req.Lines is null && o.Lines.All(l => OrderSupport.Unshipped(l) == 0)) return "Everything already shipped.";
        return null;
    }

    private static async Task<List<object>> PackingSlips(AppDbContext db, AccountAccess a, List<Guid> ids)
    {
        var orders = await db.OrdersFor(a).AsNoTracking().Include(o => o.Lines).AsSplitQuery().Where(o => ids.Contains(o.Id)).ToListAsync();
        var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
        return orders.Select(o => (object)new
        {
            id = o.Id, number = o.Number, brandName = brands.GetValueOrDefault(o.BrandId, ""), placedAt = o.PlacedAt, shippingMethod = o.ShippingMethod,
            shipTo = new { name = o.ShipName, line1 = o.ShipLine1, line2 = o.ShipLine2, city = o.ShipCity, region = o.ShipRegion, postalCode = o.ShipPostalCode, country = o.ShipCountry, phone = o.BuyerPhone },
            giftMessage = o.GiftMessage, isGift = !string.IsNullOrEmpty(o.GiftMessage), showPrices = string.IsNullOrEmpty(o.GiftMessage), currency = o.Currency,
            shippingSpeed = o.ShippingSpeed, deliverByAt = o.DeliverByAt,
            shipping = string.IsNullOrEmpty(o.GiftMessage) ? new { shippingCents = o.ShippingCents, shippingTaxCents = o.ShippingTaxCents } : null,   // priced like the lines: hidden on a gift
            lines = o.Lines.Select(l => new { title = l.Title, sku = l.Sku, options = l.OptionsText, quantity = l.Quantity, unshipped = OrderSupport.Unshipped(l), unitPriceCents = l.UnitPriceCents })
        }).ToList();
    }

    private static IResult PackingSlip(List<object> slips, bool single) =>
        single ? (slips.Count == 0 ? Results.NotFound() : Results.Json(slips[0], OrderJson.Opts)) : Results.Json(new { slips }, OrderJson.Opts);
}
