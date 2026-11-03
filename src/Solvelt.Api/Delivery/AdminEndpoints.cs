using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Delivery;

/// <summary>Read-only staff tools for local delivery. Lists carry no personal data; proof photos need a reason and are audited (like the PII reveal).</summary>
public static class DeliveryAdminEndpoints
{
    public static void MapDeliveryAdminEndpoints(this RouteGroupBuilder g)
    {
        g.AGet("/sellers/{id:guid}/local-delivery", async (Guid id, AppDbContext db) =>
        {
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var settings = await db.DeliverySettings.AsNoTracking().Include(s => s.Zones).Include(s => s.Windows).Where(s => s.SellerId == id).ToListAsync();
            var routes = await db.DeliveryRoutes.AsNoTracking().Where(r => r.SellerId == id).OrderByDescending(r => r.Date).Take(30).ToListAsync();
            var deliveries = await (from d in db.LocalDeliveries.AsNoTracking() join o in db.Orders.AsNoTracking() on d.OrderId equals o.Id
                                    where d.SellerId == id orderby d.CreatedAt descending
                                    select new { d.Id, d.OrderId, o.Number, d.Code, d.Status, d.Date, d.WindowStartUtc, d.WindowEndUtc, d.RouteId, d.Attempts, d.FeeCents, d.DeliveredAt }).Take(100).ToListAsync();
            return Results.Ok(new
            {
                settings = settings.Select(s => DeliverySellerEndpoints.SettingDto(s, s.BrandId)),
                routes = routes.Select(r => new { r.Id, r.Date, status = r.Status.ToString(), r.StartedAt, r.CompletedAt }),
                deliveries = deliveries.Select(d => new { d.Id, d.OrderId, d.Number, d.Code, status = d.Status.ToString(), d.Date, d.WindowStartUtc, d.WindowEndUtc, d.RouteId, d.Attempts, d.FeeCents, d.DeliveredAt })
            });
        }, StaffPermission.DeliveryView);

        g.APost("/local-delivery/deliveries/{id:guid}/proof-access", async (Guid id, ReasonRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, DeliveryService svc, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var d = await db.LocalDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            // audited BEFORE any link is handed out; the audit row names the delivery, never the content
            await audit.WriteAsync(ctx.Staff(), "delivery.proof_viewed", "local_delivery", id, reason, extra: "fields=proof_photo,signature,recipient");
            var now = time.UtcNow();
            var proofs = await db.DeliveryProofs.AsNoTracking().Where(p => p.DeliveryId == id && p.DeletedAt == null).ToListAsync();
            var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.Number).ToListAsync();
            return Results.Ok(new
            {
                expiresInMinutes = 60,
                proofs = proofs.Select(p => new { p.Id, p.Kind, url = svc.SignedProofUrl(p.Id, now) }),
                attempts = attempts.Select(a => new { a.Number, a.Outcome, a.Reason, a.Note, a.RecipientName, a.Placement, at = a.ServerAt })
            });
        }, StaffPermission.LookupsReveal, "reveal");
    }
}
