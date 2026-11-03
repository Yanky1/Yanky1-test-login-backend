using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Delivery;

/// <summary>Jobs: 24 h location retention, late alerts, cleanup of deliveries whose order was cancelled, proof deletion when the order's buyer data is redacted.</summary>
public class DeliveryMaintenance(AppDbContext db, TimeProvider time, OrderNotifier notifier, CapacityService capacity, IFileStorage storage)
{
    public record Summary(int LocationsDeleted, int LateNotified, int Cancelled, int ProofsDeleted);

    public async Task<Summary> RunAsync(CancellationToken ct = default)
    {
        var now = time.UtcNow();
        var locs = await db.DriverLocations.Where(l => l.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        // an order cancelled/refunded before it left: free the window and take it off the board
        var dead = await db.LocalDeliveries.Where(d => (d.Status == LocalStatus.Unassigned || d.Status == LocalStatus.Assigned)
            && db.Orders.Any(o => o.Id == d.OrderId && (o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Refunded))).ToListAsync(ct);
        foreach (var d in dead) { d.Status = LocalStatus.Cancelled; d.RouteId = null; d.StopSeq = 0; d.Version++; d.UpdatedAt = now; if (d.WindowId is { } w) await capacity.ReleaseAsync(w, d.Date); }
        await db.SaveChangesAsync(ct);

        var late = await db.LocalDeliveries.Where(d => d.LateNotifiedAt == null && d.WindowEndUtc < now
            && (d.Status == LocalStatus.Unassigned || d.Status == LocalStatus.Assigned || d.Status == LocalStatus.InTransit || d.Status == LocalStatus.OutForDelivery)).Take(200).ToListAsync(ct);
        foreach (var d in late)
        {
            d.LateNotifiedAt = now;
            var number = await db.Orders.Where(o => o.Id == d.OrderId).Select(o => o.Number).FirstAsync(ct);
            await db.SaveChangesAsync(ct);
            await notifier.DeliveryAlertAsync(d.SellerId, d.BrandId, "delivery.late", $"Delivery is late: {number}", "A delivery is late", $"Order {number} was not delivered within its window.", "/delivery", new { deliveryId = d.Id });
        }

        // buyer data redacted -> proof photos and signatures go too
        var gone = await db.DeliveryProofs.Where(p => p.DeletedAt == null && db.LocalDeliveries.Any(d => d.Id == p.DeliveryId && db.Orders.Any(o => o.Id == d.OrderId && o.PiiRedactedAt != null))).ToListAsync(ct);
        foreach (var p in gone) { await storage.DeleteAsync(p.StorageKey, ct); p.DeletedAt = now; }
        await db.SaveChangesAsync(ct);
        return new(locs, late.Count, dead.Count, gone.Count);
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
