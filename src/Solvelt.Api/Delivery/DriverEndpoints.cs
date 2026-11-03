using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Delivery;

public record ConsentRequest(bool? Consented, bool? Paused);
public record EligibilityRequest(Guid? SellerId, Guid? BrandId, long? SubtotalCents, EligibilityAddress? Address);
public record EligibilityAddress(string? PostalCode, string? Country, double? Lat, double? Lng);

public static class DeliveryDriverEndpoints
{
    public static void MapDriver(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/driver").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders").RequireActiveAccount().RequirePermission(Permission.DeliveryDrive);

        g.MapGet("/me", async (HttpContext ctx, DriverService svc) => Results.Ok(await svc.MeAsync(ctx.GetAccess()!)));
        g.MapGet("/stops/{id:guid}", async (Guid id, HttpContext ctx, DriverService svc) => await svc.StopAsync(ctx.GetAccess()!, id) is { } s ? Results.Ok(s) : Results.NotFound());
        g.MapPost("/routes/{id:guid}/start", async (Guid id, HttpContext ctx, DriverService svc) => OrderEndpoints.ToResult(await svc.StartRouteAsync(ctx.GetAccess()!, id)));
        g.MapPost("/stops/{id:guid}/events", async (Guid id, DriverEventRequest r, HttpContext ctx, DriverService svc) => OrderEndpoints.ToResult(await svc.ApplyAsync(ctx.GetAccess()!, id, r)));
        g.MapPost("/consent", async (ConsentRequest r, HttpContext ctx, DriverService svc) => OrderEndpoints.ToResult(await svc.ConsentAsync(ctx.GetAccess()!, r.Consented, r.Paused)));
        g.MapPost("/location", async (LocationRequest r, HttpContext ctx, DriverService svc) => OrderEndpoints.ToResult(await svc.LocationAsync(ctx.GetAccess()!, r)));
        g.MapPost("/stops/{id:guid}/proofs", async (Guid id, HttpContext ctx, DriverService svc, IOptions<DeliveryOptions> opt) =>
        {
            if (!ctx.Request.HasFormContentType) return Validation.Invalid("file", "Send multipart form data.");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0 || file.Length > opt.Value.MaxProofBytes) return Validation.Invalid("file", "A file up to 8 MB is required.");
            if (!Guid.TryParse(form["clientId"], out var cid)) return Validation.Invalid("clientId", "clientId must be a GUID.");
            using var ms = new MemoryStream(); await file.CopyToAsync(ms);
            return OrderEndpoints.ToResult(await svc.UploadProofAsync(ctx.GetAccess()!, id, cid, form["kind"].ToString().ToLowerInvariant(), ms.ToArray()));
        }).DisableAntiforgery();
    }

    public static void MapDeliveryInternal(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1/local-delivery").RequireRateLimiting("internal").AllowAnonymous();

        // signed: can this address get local delivery from this seller/brand, at what fee, in which windows?
        g.MapPost("/eligibility", async (HttpContext ctx, InboundVerifier verifier, DeliveryService svc) =>
        {
            var (req, err) = await InternalEndpoints.Signed<EligibilityRequest>(ctx, verifier);
            if (err is not null) return err;
            if (req!.SellerId is not { } sid || req.BrandId is not { } bid || req.Address is null) return InternalEndpoints.Err(422, "invalid_request", "sellerId, brandId and address are required.");
            if (req.SubtotalCents is < 0 or > OrderIngestService.MaxCents) return InternalEndpoints.Err(422, "invalid_request", "subtotalCents is out of range.");
            var q = await svc.QuoteAsync(sid, bid, new DeliveryAddress(req.Address.PostalCode, req.Address.Country, req.Address.Lat, req.Address.Lng), req.SubtotalCents ?? 0);
            return Results.Json(new
            {
                eligible = q.Eligible, reason = q.Reason, method = DeliveryMethod.Local, feeCents = q.FeeCents, zone = q.Zone is null ? null : new { id = q.Zone.Id, name = q.Zone.Name }, timeZone = q.Setting?.TimeZone,
                windows = q.Windows.Where(w => w.Remaining > 0).Select(w => new { windowId = w.WindowId, date = w.Date, start = w.StartUtc, end = w.EndUtc, remaining = w.Remaining })
            }, OrderJson.Opts);
        });

        // short-lived signed link to a proof-of-delivery file (sent to the marketplace in shipment.delivered)
        app.MapGet("/api/public/delivery-proof/{id:guid}", async (Guid id, long exp, string? sig, HttpContext ctx, AppDbContext db, DeliveryService svc, IFileStorage storage, TimeProvider time) =>
        {
            if (!svc.ProofSigValid(id, exp, sig, time.UtcNow())) return Results.NotFound();
            var p = await db.DeliveryProofs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
            return p is null ? Results.NotFound() : await DeliverySellerEndpoints.ProofFile(p, storage, ctx);
        }).AllowAnonymous().RequireRateLimiting("internal");
    }
}
