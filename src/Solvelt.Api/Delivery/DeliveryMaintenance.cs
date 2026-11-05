using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Delivery;

/// <summary>Jobs: 24 h location retention, late alerts, cleanup of deliveries whose order was cancelled, proof deletion when the order's buyer data is redacted.</summary>
public class DeliveryMaintenance(AppDbContext db, TimeProvider time, OrderNotifier notifier, CapacityService capacity, IFileStorage storage, IOptions<DeliveryOptions> opt)
{
    public record Summary(int LocationsDeleted, int LateNotified, int Cancelled, int ProofsDeleted);

    public async Task<Summary> RunAsync(CancellationToken ct = default)
    {
        var now = time.UtcNow();
        var locs = await db.DriverLocations.Where(l => l.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        // an order cancelled/refunded before it left: free the window and take it off the board.
        // The status flip is a conditional UPDATE (id + version + still unassigned/assigned) in the same transaction as the capacity release, and the window is
        // released only when exactly one row changed: overlapping runs (or several instances) can never release the same booking twice.
        var dead = await db.LocalDeliveries.AsNoTracking().Where(d => (d.Status == LocalStatus.Unassigned || d.Status == LocalStatus.Assigned)
            && db.Orders.Any(o => o.Id == d.OrderId && (o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Refunded)))
            .Select(d => new { d.Id, d.Version, d.WindowId, d.Date }).ToListAsync(ct);
        var cancelled = 0;
        foreach (var d in dead)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var next = d.Version + 1;
            var n = await db.LocalDeliveries.Where(x => x.Id == d.Id && x.Version == d.Version && (x.Status == LocalStatus.Unassigned || x.Status == LocalStatus.Assigned))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, LocalStatus.Cancelled).SetProperty(x => x.RouteId, (Guid?)null).SetProperty(x => x.StopSeq, 0)
                    .SetProperty(x => x.Version, next).SetProperty(x => x.UpdatedAt, now), ct);
            if (n == 1 && d.WindowId is { } w) await capacity.ReleaseAsync(w, d.Date);
            await tx.CommitAsync(ct);
            cancelled += n;
        }

        var late = await db.LocalDeliveries.Where(d => d.LateNotifiedAt == null && d.WindowEndUtc < now
            && (d.Status == LocalStatus.Unassigned || d.Status == LocalStatus.Assigned || d.Status == LocalStatus.InTransit || d.Status == LocalStatus.OutForDelivery)).Take(200).ToListAsync(ct);
        foreach (var d in late)
        {
            d.LateNotifiedAt = now;
            var number = await db.Orders.Where(o => o.Id == d.OrderId).Select(o => o.Number).FirstAsync(ct);
            await db.SaveChangesAsync(ct);
            await notifier.DeliveryAlertAsync(d.SellerId, d.BrandId, "delivery.late", $"Delivery is late: {number}", "A delivery is late", $"Order {number} was not delivered within its window.", "/delivery", new { deliveryId = d.Id }, [d.OrderId]);
        }

        // buyer data redacted -> proof photos and signatures go too
        var gone = await db.DeliveryProofs.Where(p => p.DeletedAt == null && db.LocalDeliveries.Any(d => d.Id == p.DeliveryId && db.Orders.Any(o => o.Id == d.OrderId && o.PiiRedactedAt != null))).ToListAsync(ct);
        foreach (var p in gone) { await storage.DeleteAsync(p.StorageKey, ct); p.DeletedAt = now; }
        await db.SaveChangesAsync(ct);

        // buyer data redacted -> the coordinates and free text of the delivery and its attempts go too
        var redacted = db.LocalDeliveries.Where(d => db.Orders.Any(o => o.Id == d.OrderId && o.PiiRedactedAt != null));
        await redacted.Where(d => d.DestLat != null || d.DestLng != null || d.DeliveredLat != null || d.DeliveredLng != null)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.DestLat, (double?)null).SetProperty(d => d.DestLng, (double?)null).SetProperty(d => d.DeliveredLat, (double?)null).SetProperty(d => d.DeliveredLng, (double?)null), ct);
        await db.DeliveryAttempts.Where(a => (a.RecipientName != null || a.Note != null || a.Lat != null || a.Lng != null) && redacted.Any(d => d.Id == a.DeliveryId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.RecipientName, (string?)null).SetProperty(a => a.Note, (string?)null).SetProperty(a => a.Lat, (double?)null).SetProperty(a => a.Lng, (double?)null), ct);

        // location retention also covers the copies in the outbox: a sent order.tracking_updated row lives for months, so the coarse position is blanked after the retention period
        var cutoff = now.AddHours(-opt.Value.LocationRetentionHours);
        var withPosition = await db.Outbox.Where(m => m.EventType == "order.tracking_updated" && m.CreatedAt <= cutoff && m.PayloadJson.Contains("\"position\":{")).Take(500).ToListAsync(ct);
        foreach (var m in withPosition)
        {
            if (JsonNode.Parse(m.PayloadJson) is JsonObject root && root["data"] is JsonObject data && data["position"] is not null) { data["position"] = null; m.PayloadJson = root.ToJsonString(); }
        }
        await db.SaveChangesAsync(ct);
        return new(locs, late.Count, cancelled, gone.Count);
    }
}

public class DeliveryWorker(IServiceScopeFactory scopes, ILogger<DeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<DeliveryMaintenance>().RunAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Local delivery maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stop); } catch (OperationCanceledException) { }
        }
    }
}

public static class DeliveryModule
{
    public static WebApplicationBuilder AddDelivery(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.Section));
        var dev = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        var key = builder.Configuration.GetSection(DeliveryOptions.Section).Get<DeliveryOptions>()?.ProofUrlKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!dev) throw new InvalidOperationException("LocalDelivery:ProofUrlKey must be configured outside Development/Testing (it signs proof-of-delivery links and must not be the Orders secret).");
            builder.Services.PostConfigure<DeliveryOptions>(o => { if (string.IsNullOrWhiteSpace(o.ProofUrlKey)) o.ProofUrlKey = "dev-only-proof-url-key-0123456789abcdef"; });
        }
        else if (key == builder.Configuration["Orders:OutboundSecret"] || key == builder.Configuration["Orders:InboundSecret"])
            throw new InvalidOperationException("LocalDelivery:ProofUrlKey must differ from the Orders secrets.");
        builder.Services.AddScoped<CapacityService>();
        builder.Services.AddSingleton<IRouteOptimizer, NearestNeighbourOptimizer>();
        builder.Services.AddScoped<DeliveryService>();
        builder.Services.AddScoped<DriverService>();
        builder.Services.AddScoped<DeliveryMaintenance>();
        if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<DeliveryWorker>();
        return builder;
    }

    public static void MapDelivery(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapDeliverySeller();
        v1.MapDriver();
        app.MapDeliveryInternal();
        if (AdminDevEndpoints.SimulatorEnabled(app.Environment)) app.MapPost("/api/dev/delivery-maintenance/run", async (DeliveryMaintenance m) => Results.Ok(await m.RunAsync())).AllowAnonymous();
    }
}
