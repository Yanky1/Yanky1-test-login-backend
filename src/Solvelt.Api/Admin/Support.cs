using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;

namespace Solvelt.Api.Admin;

/// <summary>Read-only support lookups. Personal data is masked by default; "reveal" needs a reason, is rate limited and audited (break-glass). No impersonation.</summary>
public static class SupportEndpoints
{
    private static readonly string[] Kinds = ["orders", "bookings", "quotes", "conversations"];

    public static void MapSupportEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/lookups");

        s.AGet("/{kind}", async (string kind, string? q, AppDbContext db) =>
        {
            if (!Kinds.Contains(kind)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return Validation.Invalid("q", "Enter at least 3 characters (number, id or email).");
            var t = q.Trim(); var lower = t.ToLowerInvariant();
            var gid = Guid.TryParse(t, out var g1) ? g1 : Guid.Empty;
            switch (kind)
            {
                case "orders":
                {
                    var rows = await db.Orders.AsNoTracking().Where(o => o.Id == gid || o.Number.ToLower() == lower || o.MarketplaceOrderId == t || (o.BuyerRelayEmail != null && o.BuyerRelayEmail.ToLower() == lower))
                        .OrderByDescending(o => o.PlacedAt).Take(25).Select(o => new { o.Id, o.Number, o.SellerId, status = o.Status.ToString(), o.PlacedAt, o.TotalCents, o.Currency, o.BuyerName, o.BuyerRelayEmail }).ToListAsync();
                    return Results.Ok(new { items = rows.Select(o => new { o.Id, o.Number, o.SellerId, o.status, o.PlacedAt, o.TotalCents, o.Currency, buyerName = Mask.Name(o.BuyerName), buyerEmail = Mask.EmailAddr(o.BuyerRelayEmail) }) });
                }
                case "bookings":
                {
                    var rows = await db.Bookings.AsNoTracking().Where(b => b.Id == gid || b.Number.ToLower() == lower || b.MarketplaceBookingId == t || (b.BuyerEmail != null && b.BuyerEmail.ToLower() == lower))
                        .OrderByDescending(b => b.CreatedAt).Take(25).Select(b => new { b.Id, b.Number, b.SellerId, status = b.Status.ToString(), b.StartUtc, b.ServiceTitle, b.PriceCents, b.BuyerName, b.BuyerEmail }).ToListAsync();
                    return Results.Ok(new { items = rows.Select(b => new { b.Id, b.Number, b.SellerId, b.status, b.StartUtc, b.ServiceTitle, b.PriceCents, buyerName = Mask.Name(b.BuyerName), buyerEmail = Mask.EmailAddr(b.BuyerEmail) }) });
                }
                case "quotes":
                {
                    var rows = await db.QuoteRequests.AsNoTracking().Where(x => x.Id == gid || x.MarketplaceQuoteId == t || (x.BuyerEmail != null && x.BuyerEmail.ToLower() == lower))
                        .OrderByDescending(x => x.CreatedAt).Take(25).Select(x => new { x.Id, x.SellerId, status = x.Status.ToString(), x.ServiceTitle, x.CreatedAt, x.BuyerName, x.BuyerEmail }).ToListAsync();
                    return Results.Ok(new { items = rows.Select(x => new { x.Id, x.SellerId, x.status, x.ServiceTitle, x.CreatedAt, buyerName = Mask.Name(x.BuyerName), buyerEmail = Mask.EmailAddr(x.BuyerEmail) }) });
                }
                default:
                {
                    var rows = await db.Conversations.AsNoTracking().Where(c => c.Id == gid || c.MarketplaceConversationId == t
                            || (c.OrderId != null && db.Orders.Any(o => o.Id == c.OrderId && (o.Number.ToLower() == lower || (o.BuyerRelayEmail != null && o.BuyerRelayEmail.ToLower() == lower)))))
                        .OrderByDescending(c => c.LastMessageAt).Take(25).Select(c => new { c.Id, c.SellerId, c.Subject, c.BuyerName, c.MessageCount, c.LastMessageAt, c.Flagged }).ToListAsync();
                    return Results.Ok(new { items = rows.Select(c => new { c.Id, c.SellerId, subject = Mask.Text(c.Subject), buyerName = Mask.Name(c.BuyerName), c.MessageCount, c.LastMessageAt, c.Flagged }) });
                }
            }
        }, StaffPermission.LookupsView);

        s.AGet("/{kind}/{id:guid}", async (string kind, Guid id, AppDbContext db) => await DetailAsync(kind, id, db, reveal: false), StaffPermission.LookupsView);

        s.APost("/{kind}/{id:guid}/reveal", async (string kind, Guid id, ReasonRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit) =>
        {
            if (!Kinds.Contains(kind)) return Results.NotFound();
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var res = await DetailAsync(kind, id, db, reveal: true);
            if ((res as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode is 404) return res;
            // audited BEFORE the data is returned; the audit row names the record and fields, never the values
            await audit.WriteAsync(ctx.Staff(), "pii.revealed", kind.TrimEnd('s'), id, reason, extra: kind == "conversations" ? "fields=buyer_name,message_text" : "fields=buyer_name,email,phone,address");
            return res;
        }, StaffPermission.LookupsReveal, "reveal");
    }

    private static async Task<IResult> DetailAsync(string kind, Guid id, AppDbContext db, bool reveal)
    {
        string? N(string? v) => reveal ? v : Mask.Name(v);
        string? E(string? v) => reveal ? v : Mask.EmailAddr(v);
        string? P(string? v) => reveal ? v : Mask.PhoneNo(v);
        string? Z(string? v) => reveal ? v : Mask.Postal(v);
        switch (kind)
        {
            case "orders":
            {
                var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (o is null) return Results.NotFound();
                return Results.Ok(new
                {
                    revealed = reveal, o.Id, o.Number, o.SellerId, o.MarketplaceOrderId, status = o.Status.ToString(), o.PlacedAt, o.Currency, o.ItemsCents, o.ShippingCents, o.TaxCents, o.TotalCents, o.FeeCents, o.RefundedCents,
                    lines = await db.OrderLines.AsNoTracking().CountAsync(l => l.OrderId == id),
                    buyer = new { name = N(o.BuyerName), email = E(o.BuyerRelayEmail), phone = P(o.BuyerPhone) },
                    shipTo = new { name = N(o.ShipName), line1 = reveal ? o.ShipLine1 : null, line2 = reveal ? o.ShipLine2 : null, city = o.ShipCity, region = o.ShipRegion, postalCode = Z(o.ShipPostalCode), country = o.ShipCountry },
                    redacted = o.PiiRedactedAt is not null
                });
            }
            case "bookings":
            {
                var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (b is null) return Results.NotFound();
                return Results.Ok(new
                {
                    revealed = reveal, b.Id, b.Number, b.SellerId, b.MarketplaceBookingId, status = b.Status.ToString(), b.ServiceTitle, b.StartUtc, b.EndUtc, b.PriceCents, b.CreatedAt,
                    buyer = new { name = N(b.BuyerName), email = E(b.BuyerEmail), phone = P(b.BuyerPhone), postalCode = Z(b.PostalCode), address = reveal ? b.AddressJson : null }
                });
            }
            case "quotes":
            {
                var q = await db.QuoteRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (q is null) return Results.NotFound();
                return Results.Ok(new
                {
                    revealed = reveal, q.Id, q.SellerId, q.MarketplaceQuoteId, status = q.Status.ToString(), q.ServiceTitle, q.CreatedAt, q.QuotePriceCents,
                    details = reveal ? q.Details : Mask.Text(q.Details),
                    buyer = new { name = N(q.BuyerName), email = E(q.BuyerEmail), phone = P(q.BuyerPhone), postalCode = Z(q.PostalCode) }
                });
            }
            case "conversations":
            {
                var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (c is null) return Results.NotFound();
                var msgs = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt).Take(200)
                    .Select(m => new { m.Id, direction = m.Direction.ToString(), m.Text, m.CreatedAt, m.ContactWarning, m.Reported }).ToListAsync();
                return Results.Ok(new
                {
                    revealed = reveal, c.Id, c.SellerId, subject = reveal ? c.Subject : Mask.Text(c.Subject), buyerName = N(c.BuyerName), c.MessageCount, c.Flagged, c.FlagReason, c.CreatedAt,
                    messages = msgs.Select(m => new { m.Id, m.direction, text = reveal ? m.Text : Mask.Text(m.Text), m.CreatedAt, m.ContactWarning, m.Reported })
                });
            }
            default: return Results.NotFound();
        }
    }
}
