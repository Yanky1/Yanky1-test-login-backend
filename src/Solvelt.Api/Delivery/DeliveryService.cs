using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Delivery;

public class DeliveryOptions
{
    public const string Section = "LocalDelivery";
    public int ProofUrlMinutes { get; set; } = 60;
    public int LocationRetentionHours { get; set; } = 24;
    public int EtaEventSeconds { get; set; } = 60;
    public int MaxProofBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Key that signs proof-of-delivery URLs. Deliberately NOT the marketplace-shared Orders secret (whoever holds that could mint any expiry). Required outside Development/Testing.</summary>
    public string ProofUrlKey { get; set; } = "";
}

/// <summary>Eligibility, booking, dispatch (assign / routes / optimize / convert), route start and the tracking + outbox events of local deliveries.</summary>
public class DeliveryService(AppDbContext db, OrderActions orders, Outbox outbox, TimeProvider time, CapacityService capacity, IRouteOptimizer optimizer,
    OrderNotifier notifier, IAuditLog audit, IOptions<DeliveryOptions> opt)
{
    public static readonly LocalStatus[] Open = [LocalStatus.Assigned, LocalStatus.InTransit, LocalStatus.OutForDelivery, LocalStatus.Failed];
    public static readonly LocalStatus[] Live = [LocalStatus.Unassigned, LocalStatus.Assigned, LocalStatus.InTransit, LocalStatus.OutForDelivery, LocalStatus.Failed];

    public static string? StatusOf(LocalStatus s) => s switch
    {
        LocalStatus.Assigned => TrackingStatus.PreTransit, LocalStatus.InTransit => TrackingStatus.InTransit, LocalStatus.OutForDelivery => TrackingStatus.OutForDelivery,
        LocalStatus.Delivered => TrackingStatus.Delivered, LocalStatus.Failed => TrackingStatus.Failure, LocalStatus.Returned => TrackingStatus.ReturnToSender, _ => null
    };

    private static string NewCode() => "LD-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));

    // ---- settings + eligibility ---------------------------------------------------------------------------------------------
    /// <summary>The enabled settings that apply to a brand: the brand's own when enabled, else the account-wide ones.</summary>
    public async Task<DeliverySetting?> SettingFor(Guid sellerId, Guid brandId, bool tracking = false)
    {
        var q = db.DeliverySettings.Include(s => s.Zones).Include(s => s.Windows).Where(s => s.SellerId == sellerId && s.Enabled);
        if (!tracking) q = q.AsNoTracking();
        var all = await q.Where(s => s.BrandId == null || s.BrandId == brandId).ToListAsync();
        return all.FirstOrDefault(s => s.BrandId == brandId) ?? all.FirstOrDefault();
    }

    public record Quote(bool Eligible, string? Reason, DeliveryZone? Zone, long FeeCents, List<OfferedWindow> Windows, DeliverySetting? Setting);

    public async Task<Quote> QuoteAsync(Guid sellerId, Guid brandId, DeliveryAddress addr, long subtotalCents)
    {
        var s = await SettingFor(sellerId, brandId);
        if (s is null) return new(false, "not_offered", null, 0, [], null);
        var z = ZoneEngine.Evaluate(s.Zones, addr, subtotalCents);
        if (!z.Eligible) return new(false, z.Reason, null, 0, [], s);
        var windows = await WindowEngine.ListAsync(db, s, time.UtcNow());
        return new(true, null, z.Zone, z.FeeCents, windows, s);
    }

    /// <summary>
    /// Validates zone + window and books capacity, then adds the <see cref="LocalDelivery"/> to the context WITHOUT saving; the caller saves it with the order
    /// and calls <see cref="ReleaseAsync"/> if that save fails. Returns an error code/message instead of throwing.
    /// </summary>
    public async Task<(LocalDelivery? Delivery, string? Code, string? Message)> PrepareAsync(SellerOrder o, Guid windowId, DateOnly date, double? lat, double? lng, bool converted)
    {
        var s = await SettingFor(o.SellerId, o.BrandId);
        if (s is null) return (null, "delivery_not_offered", "This seller does not offer local delivery.");
        var addr = new DeliveryAddress(o.ShipPostalCode, o.ShipCountry, lat ?? o.ShipLat, lng ?? o.ShipLng);
        var subtotal = o.ItemsCents - o.DiscountCents;
        var z = ZoneEngine.Evaluate(s.Zones, addr, subtotal);
        if (!z.Eligible) return (null, "delivery_" + z.Reason, z.Reason == "below_minimum" ? "The order is below the minimum for local delivery." : "The address is outside the local delivery area.");
        var w = s.Windows.FirstOrDefault(x => x.Id == windowId);
        var now = time.UtcNow();
        var offered = w is null ? null : WindowEngine.Offer(s, w, date, now, 0);
        if (offered is null) return (null, "delivery_window_unavailable", "That delivery window is not available.");
        if (!await capacity.TryBookAsync(w!.Id, date, w.Capacity)) return (null, "delivery_window_full", "That delivery window is full.");
        var d = new LocalDelivery
        {
            SellerId = o.SellerId, BrandId = o.BrandId, OrderId = o.Id, ZoneId = z.Zone!.Id, WindowId = w.Id, Date = date, WindowStartUtc = offered.StartUtc, WindowEndUtc = offered.EndUtc,
            FeeCents = z.FeeCents, DestLat = Geo.ValidLatLng(addr.Lat, addr.Lng) ? addr.Lat : null, DestLng = Geo.ValidLatLng(addr.Lat, addr.Lng) ? addr.Lng : null,
            Code = NewCode(), Converted = converted, CreatedAt = now, UpdatedAt = now
        };
        db.LocalDeliveries.Add(d);
        return (d, null, null);
    }

    public Task ReleaseAsync(LocalDelivery d) => d.WindowId is { } w ? capacity.ReleaseAsync(w, d.Date) : Task.CompletedTask;

    // ---- seller-side conversion -----------------------------------------------------------------------------------------------
    public async Task<Res> ConvertAsync(SellerOrder o, Guid actor, Guid windowId, DateOnly date, double? lat, double? lng)
    {
        if (o.Status is not (OrderStatus.New or OrderStatus.Accepted) || o.Lines.Any(l => l.ShippedQty > 0)) return Res.Conflict("Only an order that has not shipped can be switched to local delivery.");
        if (o.CancelRequested) return Res.Conflict("The buyer asked to cancel this order.", "cancel_requested");
        if (await db.LocalDeliveries.AnyAsync(d => d.OrderId == o.Id)) return Res.Conflict("This order already has a local delivery.", "already_local");
        var (d, code, msg) = await PrepareAsync(o, windowId, date, lat, lng, converted: true);
        if (d is null) return Res.Invalid(msg!, code!);
        var now = time.UtcNow();
        o.ShippingMethod = "local";
        db.AddEvent(o.Id, "order.converted_to_local", Party.Seller, actor, $"Switched to local delivery ({d.Code}), window {d.WindowStartUtc:u}", now);
        outbox.Enqueue("order.delivery_method_changed", o.SellerId, o.MarketplaceOrderId, new
        {
            order = OrderRef(o), method = DeliveryMethod.Local, deliveryCode = d.Code, window = new { start = d.WindowStartUtc, end = d.WindowEndUtc }, feeCents = d.FeeCents
        });
        try { await db.SaveChangesAsync(); }
        catch { await ReleaseAsync(d); throw; }
        await audit.WriteAsync("delivery.converted", actor, null, $"order={o.Id};delivery={d.Id}");
        return Res.Success(new { deliveryId = d.Id, code = d.Code });
    }

    internal static object OrderRef(SellerOrder o) => new { sellerOrderId = o.Id, sellerOrderNumber = o.Number, marketplaceOrderId = o.MarketplaceOrderId };

    // ---- dispatch -------------------------------------------------------------------------------------------------------------
    public async Task<AccountMembership?> DriverAsync(Guid sellerId, Guid membershipId)
    {
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == membershipId && x.SellerId == sellerId && x.Status == MembershipStatus.Active);
        return m is not null && Permissions.Has(m.Role, Permission.DeliveryDrive) ? m : null;     // role check in memory: Permissions is not translatable to SQL
    }

    public async Task<Res> AssignAsync(LocalDelivery d, Guid driverMembershipId, Guid actor)
    {
        if (d.Status is not (LocalStatus.Unassigned or LocalStatus.Assigned)) return Res.Conflict("This delivery can't be assigned right now.");
        var driver = await DriverAsync(d.SellerId, driverMembershipId);
        if (driver is null) return Res.Invalid("Choose an active driver.", "driver");
        var order = await orders.Full(db.Orders.Where(x => x.Id == d.OrderId)).FirstOrDefaultAsync();
        if (order is null || order.Status != OrderStatus.Accepted && !(order.Status == OrderStatus.Shipped && d.ShipmentId != null))
            return Res.Conflict("Accept the order before assigning it to a driver.", "order_not_ready");
        var now = time.UtcNow();
        var route = await db.DeliveryRoutes.FirstOrDefaultAsync(r => r.SellerId == d.SellerId && r.Date == d.Date && r.DriverMembershipId == driverMembershipId && r.Status == RouteStatus.Planned);
        if (route is null)
        {
            if (await db.DeliveryRoutes.AnyAsync(r => r.SellerId == d.SellerId && r.Date == d.Date && r.DriverMembershipId == driverMembershipId && r.Status == RouteStatus.Active))
                return Res.Conflict("This driver's route has already started. Assign it to another route.", "route_active");
            route = new DeliveryRoute { SellerId = d.SellerId, Date = d.Date, DriverMembershipId = driverMembershipId, CreatedAt = now, UpdatedAt = now };
            db.DeliveryRoutes.Add(route);
        }
        var oldRoute = d.RouteId;
        d.RouteId = route.Id; d.DriverMembershipId = driverMembershipId; d.Status = LocalStatus.Assigned;
        d.StopSeq = (await db.LocalDeliveries.Where(x => x.RouteId == route.Id && x.Id != d.Id).MaxAsync(x => (int?)x.StopSeq) ?? 0) + 1;
        d.Version++; d.UpdatedAt = now;
        db.AddEvent(d.OrderId, "delivery.assigned", Party.Seller, actor, $"Assigned to a driver ({d.Code})", now);
        await RecordAsync(d, order, TrackingStatus.PreTransit, "Assigned to a driver", now);
        var r = await SaveAsync();
        if (r.Ok && oldRoute is { } prev && prev != route.Id) await ResequenceAsync(prev);
        return r.Ok ? Res.Success(new { routeId = route.Id }) : r;
    }

    public async Task<Res> UnassignAsync(LocalDelivery d, Guid actor)
    {
        if (d.Status is not (LocalStatus.Assigned or LocalStatus.Failed)) return Res.Conflict("Only an assigned or failed stop can be unassigned.");
        var route = d.RouteId;
        var now = time.UtcNow();
        d.Status = LocalStatus.Unassigned; d.RouteId = null; d.DriverMembershipId = null; d.StopSeq = 0; d.EtaUtc = null; d.Version++; d.UpdatedAt = now;
        db.AddEvent(d.OrderId, "delivery.unassigned", Party.Seller, actor, $"Removed from the route ({d.Code})", now);
        var r = await SaveAsync();
        if (r.Ok && route is { } rid) { await ResequenceAsync(rid); await CompleteIfDoneAsync(rid); }
        return r;
    }

    private async Task ResequenceAsync(Guid routeId)
    {
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == routeId).OrderBy(x => x.StopSeq).ToListAsync();
        for (var i = 0; i < stops.Count; i++) if (stops[i].StopSeq != i + 1) stops[i].StopSeq = i + 1;
        await db.SaveChangesAsync();
    }

    public async Task<Res> ReorderAsync(DeliveryRoute route, List<Guid> orderedDeliveryIds)
    {
        if (route.Status != RouteStatus.Planned) return Res.Conflict("Only a route that has not started can be reordered.");
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == route.Id).ToListAsync();
        if (orderedDeliveryIds.Count != stops.Count || !stops.Select(s => s.Id).ToHashSet().SetEquals(orderedDeliveryIds)) return Res.Invalid("List every stop of the route exactly once.", "stops");
        for (var i = 0; i < orderedDeliveryIds.Count; i++) stops.First(s => s.Id == orderedDeliveryIds[i]).StopSeq = i + 1;
        route.Version++; route.UpdatedAt = time.UtcNow();
        return await SaveAsync();
    }

    public async Task<Res> OptimizeAsync(DeliveryRoute route)
    {
        if (route.Status != RouteStatus.Planned) return Res.Conflict("Only a route that has not started can be optimized.");
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == route.Id).OrderBy(x => x.StopSeq).ToListAsync();
        var withCoords = stops.Where(s => s.DestLat != null && s.DestLng != null).ToList();
        var ordered = await optimizer.OrderAsync(route.StartLat, route.StartLng, withCoords.Select(s => new StopPoint(s.Id, s.DestLat!.Value, s.DestLng!.Value)).ToList());
        var seq = 1;
        foreach (var id in ordered) withCoords.First(s => s.Id == id).StopSeq = seq++;
        foreach (var s in stops.Where(s => s.DestLat == null || s.DestLng == null).OrderBy(s => s.StopSeq)) s.StopSeq = seq++;   // stops without coordinates keep their relative order, at the end
        route.Version++; route.UpdatedAt = time.UtcNow();
        var r = await SaveAsync();
        return r.Ok ? Res.Success(new { skippedNoCoordinates = stops.Count - withCoords.Count }) : r;
    }

    public async Task<Res> ReassignRouteAsync(DeliveryRoute route, Guid newDriver, Guid actor)
    {
        if (route.Status != RouteStatus.Planned) return Res.Conflict("Only a route that has not started can be reassigned.");
        if (await DriverAsync(route.SellerId, newDriver) is null) return Res.Invalid("Choose an active driver.", "driver");
        if (await db.DeliveryRoutes.AnyAsync(r => r.Id != route.Id && r.SellerId == route.SellerId && r.Date == route.Date && r.DriverMembershipId == newDriver && r.Status != RouteStatus.Completed && r.Status != RouteStatus.Cancelled))
            return Res.Conflict("That driver already has a route on this day.", "driver_busy");
        route.DriverMembershipId = newDriver; route.Version++; route.UpdatedAt = time.UtcNow();
        foreach (var s in await db.LocalDeliveries.Where(x => x.RouteId == route.Id).ToListAsync()) { s.DriverMembershipId = newDriver; s.Version++; }
        await audit.WriteAsync("delivery.route_reassigned", actor, null, $"route={route.Id}");
        return await SaveAsync();
    }

    // ---- route start ----------------------------------------------------------------------------------------------------------
    public async Task<Res> StartRouteAsync(DeliveryRoute route, Guid userId)
    {
        if (route.Status != RouteStatus.Planned) return Res.Conflict("This route has already started or finished.", "route_state");
        var now = time.UtcNow();
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == route.Id && x.Status == LocalStatus.Assigned).OrderBy(x => x.StopSeq).ToListAsync();
        if (stops.Count == 0) return Res.Conflict("There is nothing to deliver on this route.", "empty_route");
        var setting = await db.DeliverySettings.AsNoTracking().Where(s => s.SellerId == route.SellerId).Select(s => s.TimeZone).FirstOrDefaultAsync();
        if (route.Date > WindowEngine.LocalToday(WindowEngine.Zone(setting), now)) return Res.Conflict("This route is planned for a later day.", "too_early");
        route.Status = RouteStatus.Active; route.StartedAt = now; route.Version++; route.UpdatedAt = now;
        var skipped = new List<string>();
        foreach (var d in stops)
        {
            var order = await orders.Full(db.Orders.Where(x => x.Id == d.OrderId)).FirstAsync();
            if (d.ShipmentId is null)
            {
                var r = await orders.ShipAsync(order, userId, new ShipRequest(null, null, null, null, null, null), d.Code);
                if (!r.Ok)
                {
                    // e.g. the order was put on hold or cancelled meanwhile: the stop leaves the route, the others go
                    d.Status = LocalStatus.Unassigned; d.RouteId = null; d.DriverMembershipId = null; d.StopSeq = 0; d.Version++;
                    skipped.Add(order.Number);
                    db.ChangeTracker.Entries<SellerOrder>().Where(e => e.Entity.Id == order.Id).ToList().ForEach(e => e.State = EntityState.Detached);
                    continue;
                }
                d.ShipmentId = order.Shipments.OrderByDescending(s => s.CreatedAt).First(s => s.TrackingNumber == d.Code).Id;
            }
            d.Status = LocalStatus.InTransit; d.Version++; d.UpdatedAt = now;
            await RecordAsync(d, order, TrackingStatus.InTransit, "Route started", now);
        }
        var r2 = await SaveAsync();
        if (!r2.Ok) return r2;
        await AdvanceNextAsync(route.Id, now);
        await audit.WriteAsync("delivery.route_started", userId, null, $"route={route.Id}");
        return Res.Success(new { skipped });
    }

    /// <summary>When no stop of the route is out for delivery, the lowest-numbered stop in transit becomes the next one.</summary>
    public async Task AdvanceNextAsync(Guid routeId, DateTime now)
    {
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == routeId).OrderBy(x => x.StopSeq).ToListAsync();
        if (stops.Any(s => s.Status == LocalStatus.OutForDelivery)) return;
        var next = stops.FirstOrDefault(s => s.Status == LocalStatus.InTransit);
        if (next is null) return;
        next.Status = LocalStatus.OutForDelivery; next.Version++; next.UpdatedAt = now;
        var order = await orders.Full(db.Orders.Where(x => x.Id == next.OrderId)).FirstAsync();
        await RecordAsync(next, order, TrackingStatus.OutForDelivery, "Out for delivery", now);
        await SaveAsync();
    }

    public async Task CompleteIfDoneAsync(Guid routeId)
    {
        var route = await db.DeliveryRoutes.FirstOrDefaultAsync(r => r.Id == routeId);
        if (route is null || route.Status != RouteStatus.Active) return;
        if (await db.LocalDeliveries.AnyAsync(x => x.RouteId == routeId && Open.Contains(x.Status))) return;
        var now = time.UtcNow();
        route.Status = RouteStatus.Completed; route.CompletedAt = now; route.Version++; route.UpdatedAt = now;
        var brand = await db.LocalDeliveries.Where(x => x.RouteId == routeId).Select(x => x.BrandId).FirstOrDefaultAsync();
        var counts = await db.LocalDeliveries.Where(x => x.RouteId == routeId).GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        await SaveAsync();
        await notifier.DeliveryAlertAsync(route.SellerId, brand, "delivery.route_finished", "Delivery route finished", "A route is finished",
            $"{counts.FirstOrDefault(c => c.Key == LocalStatus.Delivered)?.N ?? 0} delivered, {counts.Where(c => c.Key != LocalStatus.Delivered).Sum(c => c.N)} not delivered.", "/delivery", new { routeId });
    }

    // ---- tracking + outbox ------------------------------------------------------------------------------------------------------
    public static string FirstName(string? email)
    {
        var local = (email ?? "").Split('@')[0];
        var tok = new string(local.TakeWhile(char.IsLetter).ToArray());
        return tok.Length == 0 ? "Your driver" : char.ToUpperInvariant(tok[0]) + tok[1..].ToLowerInvariant();
    }

    private async Task<string> DriverNameAsync(Guid? membershipId)
    {
        if (membershipId is null) return "Your driver";
        var email = await (from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.Id == membershipId select u.Email).FirstOrDefaultAsync();
        // buyer-facing: the first name only, and only when the address reliably holds one (N14); otherwise neutral. Never a guess from a one-token address.
        return Solvelt.Api.Bookings.ServiceOptions.PublicNameOrNull(email)?.Split(' ')[0] ?? "Your driver";
    }

    /// <summary>Writes the tracking history row, updates the shipment's normalized status (an older event never overwrites a newer one) and enqueues order.tracking_updated.</summary>
    public async Task RecordAsync(LocalDelivery d, SellerOrder o, string status, string? detail, DateTime at, (double Lat, double Lng)? position = null, bool history = true)
    {
        var now = time.UtcNow();
        if (history) db.TrackingEvents.Add(new TrackingEvent { SellerId = d.SellerId, OrderId = d.OrderId, ShipmentId = d.ShipmentId, DeliveryId = d.Id, Method = DeliveryMethod.Local, Status = status, Detail = detail, OccurredAt = at, RecordedAt = now });
        if (d.ShipmentId is { } sid)
        {
            var ship = o.Shipments.FirstOrDefault(s => s.Id == sid) ?? await db.Shipments.FirstOrDefaultAsync(s => s.Id == sid);
            // the delivery's own state machine (and the stale-event rule in DriverService) orders local events, so a skewed phone clock can't leave the shipment on an old status
            if (ship is not null) { ship.TrackingStatus = status; ship.TrackingStatusAt = at; ship.Eta = d.EtaUtc; }
        }
        outbox.Enqueue("order.tracking_updated", d.SellerId, o.MarketplaceOrderId, new
        {
            order = OrderRef(o), shipmentId = d.ShipmentId, method = DeliveryMethod.Local, carrier = "Local", trackingNumber = d.Code, status, statusDetail = detail,
            eta = new { windowStart = d.WindowStartUtc, windowEnd = d.WindowEndUtc, estimatedAt = d.EtaUtc },
            driverFirstName = status is TrackingStatus.PreTransit ? null : await DriverNameAsync(d.DriverMembershipId),
            position = position is { } p ? new { lat = Geo.Coarse(p.Lat), lng = Geo.Coarse(p.Lng) } : null, occurredAt = at
        });
    }

    public string SignedProofUrl(Guid proofId, DateTime now)
    {
        var exp = new DateTimeOffset(now.AddMinutes(opt.Value.ProofUrlMinutes)).ToUnixTimeSeconds();
        return $"/api/public/delivery-proof/{proofId}?exp={exp}&sig={ProofSig(proofId, exp)}";
    }

    public string ProofSig(Guid proofId, long exp) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(opt.Value.ProofUrlKey), Encoding.UTF8.GetBytes($"delivery-proof|{proofId:N}|{exp}"))).ToLowerInvariant();

    public bool ProofSigValid(Guid proofId, long exp, string? sig, DateTime now) =>
        exp >= new DateTimeOffset(now).ToUnixTimeSeconds() && sig is { Length: > 0 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ProofSig(proofId, exp)), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));

    public async Task<Res> SaveAsync()
    {
        try { await db.SaveChangesAsync(); return Res.Success(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Res.Conflict("Somebody else changed this at the same time. Reload and try again.", "version_conflict"); }
    }
}
