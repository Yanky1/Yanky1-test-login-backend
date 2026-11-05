using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Delivery;

public record DriverEventRequest(Guid? ClientEventId, string? Type, DateTime? ClientAt, string? Reason, string? Note, string? RecipientName, string? Placement,
    List<Guid>? ProofIds, double? Lat, double? Lng);
public record LocationRequest(double? Lat, double? Lng, DateTime? At);

public class DriverService(AppDbContext db, DeliveryService svc, OrderActions orders, IFileStorage storage, TimeProvider time, OrderNotifier notifier, Outbox outbox, IOptions<DeliveryOptions> opt)
{
    public static readonly string[] EventTypes = ["out_for_delivery", "delivered", "failed", "reattempt", "return_to_store"];
    public static readonly string[] FailReasons = ["no_one_home", "address_issue", "refused", "unsafe"];
    public static readonly string[] Placements = ["left_at_door", "handed_to"];

    /// <summary>Address and contact details are visible only while the route is planned/active AND the stop is not finished. After completion they are gone for the driver.</summary>
    public static bool Visible(DeliveryRoute? r, LocalDelivery d) =>
        r is { Status: RouteStatus.Planned or RouteStatus.Active } && DeliveryService.Open.Contains(d.Status);

    public Task<LocalDelivery?> MineAsync(AccountAccess a, Guid deliveryId) =>
        db.LocalDeliveries.FirstOrDefaultAsync(x => x.Id == deliveryId && x.SellerId == a.SellerId && x.DriverMembershipId == a.MembershipId && x.RouteId != null);

    // ---- views ------------------------------------------------------------------------------------------------------------------
    public async Task<object> MeAsync(AccountAccess a)
    {
        var c = await db.DriverConsents.AsNoTracking().FirstOrDefaultAsync(x => x.MembershipId == a.MembershipId);
        var routes = await RoutesAsync(a);
        return new { role = a.Role.ToString(), consented = c?.Consented ?? false, paused = c?.Paused ?? false, routes };
    }

    public async Task<List<object>> RoutesAsync(AccountAccess a)
    {
        var now = time.UtcNow();
        var since = DateOnly.FromDateTime(now.AddDays(-1));
        var routes = await db.DeliveryRoutes.AsNoTracking().Where(r => r.SellerId == a.SellerId && r.DriverMembershipId == a.MembershipId && r.Date >= since
            && (r.Status == RouteStatus.Planned || r.Status == RouteStatus.Active || r.Status == RouteStatus.Completed)).OrderBy(r => r.Date).ToListAsync();
        var res = new List<object>();
        foreach (var r in routes)
        {
            var stops = await db.LocalDeliveries.AsNoTracking().Where(d => d.RouteId == r.Id).OrderBy(d => d.StopSeq).ToListAsync();
            var dtos = new List<object>();
            foreach (var d in stops) dtos.Add(await StopDtoAsync(r, d));
            res.Add(new { id = r.Id, date = r.Date, status = r.Status.ToString(), startedAt = r.StartedAt, version = r.Version, stops = dtos });
        }
        return res;
    }

    public async Task<object?> StopAsync(AccountAccess a, Guid id)
    {
        var d = await db.LocalDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && x.DriverMembershipId == a.MembershipId && x.RouteId != null);
        if (d is null) return null;
        var r = await db.DeliveryRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.RouteId);
        return await StopDtoAsync(r!, d);
    }

    private async Task<object> StopDtoAsync(DeliveryRoute r, LocalDelivery d)
    {
        object? to = null, contact = null; string? navigate = null;
        if (Visible(r, d))
        {
            var o = await db.Orders.AsNoTracking().FirstAsync(x => x.Id == d.OrderId);
            var addr = string.Join(", ", new[] { o.ShipLine1, o.ShipLine2, o.ShipCity, o.ShipRegion, o.ShipPostalCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
            to = new { name = FirstName(o.ShipName), line1 = o.ShipLine1, line2 = o.ShipLine2, city = o.ShipCity, region = o.ShipRegion, postalCode = o.ShipPostalCode };
            navigate = "https://maps.google.com/?q=" + Uri.EscapeDataString(addr);
            // masked contact: first name + the delivery phone the marketplace provided for fulfilment; never the buyer's e-mail
            var phone = o.BuyerPhone;
            contact = new { firstName = FirstName(o.ShipName), phone, call = phone is null ? null : "tel:" + new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray()), sms = phone is null ? null : "sms:" + new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray()) };
        }
        var proofs = await db.DeliveryProofs.AsNoTracking().CountAsync(p => p.DeliveryId == d.Id && p.DeletedAt == null);
        return new
        {
            id = d.Id, seq = d.StopSeq, status = d.Status.ToString(), trackingStatus = DeliveryService.StatusOf(d.Status), code = d.Code, window = new { start = d.WindowStartUtc, end = d.WindowEndUtc },
            eta = d.EtaUtc, attempts = d.Attempts, version = d.Version, detailsVisible = to is not null, to, contact, navigateUrl = navigate, proofs
        };
    }

    private static string FirstName(string? n) => (n ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Customer";

    // ---- proofs -----------------------------------------------------------------------------------------------------------------
    public async Task<Res> UploadProofAsync(AccountAccess a, Guid deliveryId, Guid clientId, string kind, byte[] bytes)
    {
        var d = await MineAsync(a, deliveryId);
        if (d is null) return Res.Fail(404, "not_found", "Stop not found.");
        if (kind is not ("photo" or "signature")) return Res.Invalid("kind must be photo or signature.", "kind");
        if (clientId == Guid.Empty) return Res.Invalid("A client id is required.", "client_id");
        var existing = await db.DeliveryProofs.AsNoTracking().FirstOrDefaultAsync(p => p.DeliveryId == d.Id && p.ClientId == clientId);
        if (existing is not null) return Res.Success(new { id = existing.Id, replayed = true });
        var route = await db.DeliveryRoutes.AsNoTracking().FirstAsync(r => r.Id == d.RouteId);
        if (!Visible(route, d)) return Res.Conflict("This stop is finished.", "stop_finished");
        if (bytes.Length == 0 || bytes.Length > opt.Value.MaxProofBytes) return Res.Invalid("The file is empty or too large.", "size");
        var (clean, ct) = ImageSanitizer.Clean(bytes);
        if (clean is null) return Res.Invalid("Only JPEG or PNG images are accepted.", "file_type");
        var key = Guid.NewGuid().ToString("N");
        await storage.SaveAsync(key, new MemoryStream(clean));
        var p = new DeliveryProof { DeliveryId = d.Id, SellerId = d.SellerId, Kind = kind, StorageKey = key, ContentType = ct!, Size = clean.Length, ClientId = clientId, UploadedBy = a.UserId, CreatedAt = time.UtcNow() };
        db.DeliveryProofs.Add(p);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await storage.DeleteAsync(key);
            var won = await db.DeliveryProofs.AsNoTracking().FirstOrDefaultAsync(x => x.DeliveryId == d.Id && x.ClientId == clientId);
            if (won is null) throw;
            return Res.Success(new { id = won.Id, replayed = true });
        }
        return Res.Success(new { id = p.Id, replayed = false });
    }

    // ---- events (the heart of the offline queue) -----------------------------------------------------------------------------------
    public async Task<Res> ApplyAsync(AccountAccess a, Guid deliveryId, DriverEventRequest ev)
    {
        if (ev.ClientEventId is not { } eid || eid == Guid.Empty) return Res.Invalid("clientEventId is required.", "client_event_id");
        var type = ev.Type?.Trim().ToLowerInvariant();
        if (type is null || !EventTypes.Contains(type)) return Res.Invalid("Unknown event type.", "type");
        var prior = await db.DeliveryClientEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid);
        if (prior is not null) return prior.UserId == a.UserId && prior.DeliveryId == deliveryId ? Res.Success(new { applied = prior.Result == "applied", replayed = true, result = prior.Result }) : Res.Fail(404, "not_found", "Stop not found.");
        var d = await MineAsync(a, deliveryId);
        if (d is null) return Res.Fail(404, "not_found", "Stop not found.");
        var route = await db.DeliveryRoutes.FirstAsync(r => r.Id == d.RouteId);
        var now = time.UtcNow();
        var clientSupplied = ev.ClientAt is not null;      // only a time the phone sent is a client time: a server-stamped one must never make a slow phone clock look stale
        var clientAt = ev.ClientAt is { } c ? OrderSupport.Utc(c) : now;
        if (clientAt > now.AddMinutes(5)) clientAt = now;      // a wrong phone clock must not freeze the stop for later events
        // ...and must not travel back in time either: nothing on a route can have happened before the route started / the shipment left. A delivered event dated
        // weeks ago would otherwise give the order a DeliveredAt before it shipped (and let the return window and the payout release fire at once).
        var floor = route.StartedAt ?? now;
        if (d.ShipmentId is { } shipId && await db.Shipments.AsNoTracking().Where(x => x.Id == shipId).Select(x => (DateTime?)x.ShippedAt).FirstOrDefaultAsync() is { } shippedAt && shippedAt > floor) floor = shippedAt;
        if (clientAt < floor) clientAt = floor;

        // claim the event id first: of two parallel replays exactly one applies
        db.DeliveryClientEvents.Add(new DeliveryClientEvent { Id = eid, DeliveryId = d.Id, UserId = a.UserId, Type = type, ClientAt = clientAt, AppliedAt = now, Result = "applied" });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return Res.Success(new { applied = false, replayed = true }); }

        async Task<Res> Undo(Res r) { db.ChangeTracker.Clear(); await db.DeliveryClientEvents.Where(e => e.Id == eid).ExecuteDeleteAsync(); return r; }     // a rejected event can be corrected and sent again
        async Task<Res> Ignore(string why)
        {
            db.ChangeTracker.Clear();
            await db.DeliveryClientEvents.Where(e => e.Id == eid).ExecuteUpdateAsync(s => s.SetProperty(e => e.Result, why));
            return Res.Success(new { applied = false, reason = why });
        }

        if (clientSupplied && d.LastEventClientAt is { } last && clientAt < last) return await Ignore("ignored_stale");
        // "out for delivery" changes the whole route (it demotes the other stop), so it is ordered against the newest event of ANY stop of the route
        if (clientSupplied && type == "out_for_delivery" && await db.LocalDeliveries.Where(x => x.RouteId == route.Id && x.LastEventClientAt != null).MaxAsync(x => x.LastEventClientAt) is { } routeLast && clientAt < routeLast)
            return await Ignore("ignored_stale");
        if (d.Status is LocalStatus.Delivered or LocalStatus.Returned or LocalStatus.Cancelled) return await Ignore("ignored_state");
        if (route.Status != RouteStatus.Active) return await Undo(Res.Conflict("Start the route first.", "route_not_active"));

        var order = await orders.Full(db.Orders.Where(x => x.Id == d.OrderId)).FirstAsync();
        if (clientSupplied) d.LastEventClientAt = clientAt;      // events without a clientAt are ordered by arrival and leave the client-time watermark alone
        d.Version++; d.UpdatedAt = now;
        Res result;
        switch (type)
        {
            case "out_for_delivery":
                if (d.Status != LocalStatus.InTransit) return await Undo(Res.Conflict("This stop can't go out for delivery now.", "invalid_state"));
                foreach (var other in await db.LocalDeliveries.Where(x => x.RouteId == route.Id && x.Id != d.Id && x.Status == LocalStatus.OutForDelivery).ToListAsync()) {
                    other.Status = LocalStatus.InTransit; other.Version++;
                    var otherOrder = await orders.Full(db.Orders.Where(x => x.Id == other.OrderId)).FirstAsync();
                    await svc.RecordAsync(other, otherOrder, TrackingStatus.InTransit, "Another stop is next", clientAt);       // its shipment status and the buyer's event follow
                }
                d.Status = LocalStatus.OutForDelivery;
                await svc.RecordAsync(d, order, TrackingStatus.OutForDelivery, "Out for delivery", clientAt);
                result = await svc.SaveAsync();
                break;
            case "reattempt":
                if (d.Status != LocalStatus.Failed) return await Undo(Res.Conflict("Only a failed stop can be attempted again.", "invalid_state"));
                d.Status = LocalStatus.InTransit;
                await svc.RecordAsync(d, order, TrackingStatus.InTransit, "Another delivery attempt", clientAt);
                result = await svc.SaveAsync();
                break;
            case "return_to_store":
                if (d.Status != LocalStatus.Failed) return await Undo(Res.Conflict("Only a failed stop can be returned to the store.", "invalid_state"));
                d.Status = LocalStatus.Returned;
                db.AddEvent(order.Id, "delivery.returned", Party.System, null, $"Returned to the store ({d.Code})", now);
                await svc.RecordAsync(d, order, TrackingStatus.ReturnToSender, "Returned to the store", clientAt);
                result = await svc.SaveAsync();
                if (result.Ok) await notifier.DeliveryAlertAsync(d.SellerId, d.BrandId, "delivery.returned", $"Delivery returned: {order.Number}", "A delivery came back to the store", $"Order {order.Number} was returned to the store. Refund it or arrange a new delivery.", $"/orders/{order.Id}", orderIds: [order.Id], data: new { orderId = order.Id });
                break;
            case "failed":
            {
                if (d.Status is not (LocalStatus.InTransit or LocalStatus.OutForDelivery)) return await Undo(Res.Conflict("This stop can't be marked failed now.", "invalid_state"));
                var reason = ev.Reason?.Trim().ToLowerInvariant();
                if (reason is null || !FailReasons.Contains(reason)) return await Undo(Res.Invalid("Choose a reason: " + string.Join(", ", FailReasons) + ".", "reason"));
                if (ev.Note is { Length: > 500 }) return await Undo(Res.Invalid("The note is too long.", "note"));
                var proofs = await ClaimProofsAsync(a, d, ev.ProofIds, requirePhoto: false);
                if (proofs.Error is not null) return await Undo(proofs.Error);
                var attempt = new DeliveryAttempt { DeliveryId = d.Id, SellerId = d.SellerId, Number = d.Attempts + 1, Outcome = "failed", Reason = reason, Note = ev.Note?.Trim(), DriverMembershipId = a.MembershipId, ClientAt = clientAt, ServerAt = now };
                db.DeliveryAttempts.Add(attempt);
                foreach (var p in proofs.Items) p.AttemptId = attempt.Id;
                d.Attempts++; d.Status = LocalStatus.Failed; d.FailReason = reason;
                db.AddEvent(order.Id, "delivery.attempt_failed", Party.System, null, $"Delivery attempt {d.Attempts} failed ({reason})", now);
                await svc.RecordAsync(d, order, TrackingStatus.Failure, reason, clientAt);
                outbox.Enqueue("delivery.attempt_failed", d.SellerId, order.MarketplaceOrderId, new { order = DeliveryService.OrderRef(order), shipmentId = d.ShipmentId, method = DeliveryMethod.Local, attempt = d.Attempts, reason, occurredAt = clientAt });
                result = await svc.SaveAsync();
                if (result.Ok) await notifier.DeliveryAlertAsync(d.SellerId, d.BrandId, "delivery.attempt_failed", $"Delivery attempt failed: {order.Number}", "A delivery attempt failed", $"Order {order.Number}: {reason.Replace('_', ' ')}.", "/delivery", new { orderId = order.Id }, [order.Id]);
                break;
            }
            default: // delivered
            {
                if (d.Status is not (LocalStatus.InTransit or LocalStatus.OutForDelivery)) return await Undo(Res.Conflict("This stop can't be marked delivered now.", "invalid_state"));
                if (ev.Placement is null || !Placements.Contains(ev.Placement)) return await Undo(Res.Invalid("Choose left_at_door or handed_to.", "placement"));
                if (ev.RecipientName is { Length: > 120 } || ev.Note is { Length: > 500 }) return await Undo(Res.Invalid("The recipient name or note is too long.", "length"));
                var proofs = await ClaimProofsAsync(a, d, ev.ProofIds, requirePhoto: true);
                if (proofs.Error is not null) return await Undo(proofs.Error);
                var attempt = new DeliveryAttempt
                {
                    DeliveryId = d.Id, SellerId = d.SellerId, Number = d.Attempts + 1, Outcome = "delivered", RecipientName = ev.RecipientName?.Trim(), Placement = ev.Placement, Note = ev.Note?.Trim(),
                    DriverMembershipId = a.MembershipId, ClientAt = clientAt, ServerAt = now
                };
                if (Geo.ValidLatLng(ev.Lat, ev.Lng)) { attempt.Lat = d.DeliveredLat = Math.Round(ev.Lat!.Value, 4); attempt.Lng = d.DeliveredLng = Math.Round(ev.Lng!.Value, 4); }   // the one server-recorded delivery location (~10 m)
                db.DeliveryAttempts.Add(attempt);
                foreach (var p in proofs.Items) p.AttemptId = attempt.Id;
                d.Attempts++; d.Status = LocalStatus.Delivered; d.DeliveredAt = clientAt; d.EtaUtc = null;
                await svc.RecordAsync(d, order, TrackingStatus.Delivered, "Delivered", clientAt);
                var ship = order.Shipments.FirstOrDefault(s => s.Id == d.ShipmentId);
                outbox.Enqueue("shipment.delivered", d.SellerId, order.MarketplaceOrderId, new
                {
                    order = DeliveryService.OrderRef(order), shipmentId = d.ShipmentId, method = DeliveryMethod.Local, deliveredAt = clientAt, placement = ev.Placement, recipientName = attempt.RecipientName,
                    proof = new
                    {
                        photoUrls = proofs.Items.Where(p => p.Kind == "photo").Select(p => svc.SignedProofUrl(p.Id, now)).ToList(),
                        signatureUrl = proofs.Items.Where(p => p.Kind == "signature").Select(p => svc.SignedProofUrl(p.Id, now)).FirstOrDefault(),
                        expiresInMinutes = opt.Value.ProofUrlMinutes
                    }
                });
                // The order moves to Delivered through the existing rules (this also saves our changes in the same transaction): payments release works unchanged.
                try
                {
                    result = order.Status == OrderStatus.Shipped ? await orders.MarkDeliveredAsync(order, Guid.Empty, null, Party.System, null, clientAt) : await svc.SaveAsync();
                }
                catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); result = Res.Conflict("Somebody else changed this at the same time. Reload and try again.", "version_conflict"); }
                if (!result.Ok && result.Code != "version_conflict") result = await svc.SaveAsync();   // the order was already delivered/closed some other way: the stop is still delivered
                break;
            }
        }
        if (!result.Ok) return await Undo(result);
        await svc.AdvanceNextAsync(route.Id, now);
        await svc.CompleteIfDoneAsync(route.Id);
        return Res.Success(new { applied = true });
    }

    private async Task<(Res? Error, List<DeliveryProof> Items)> ClaimProofsAsync(AccountAccess a, LocalDelivery d, List<Guid>? ids, bool requirePhoto)
    {
        ids = ids?.Distinct().ToList() ?? [];
        var items = ids.Count == 0 ? [] : await db.DeliveryProofs.Where(p => ids.Contains(p.Id) && p.DeliveryId == d.Id && p.UploadedBy == a.UserId && p.DeletedAt == null && p.AttemptId == null).ToListAsync();
        if (items.Count != ids.Count) return (Res.Invalid("A proof file is missing or doesn't belong to this stop.", "proof"), items);
        if (requirePhoto && !items.Any(p => p.Kind == "photo")) return (Res.Invalid("A photo is required as proof of delivery.", "proof_required"), items);
        return (null, items);
    }

    // ---- start route / consent / location ---------------------------------------------------------------------------------------
    public async Task<Res> StartRouteAsync(AccountAccess a, Guid routeId)
    {
        var r = await db.DeliveryRoutes.FirstOrDefaultAsync(x => x.Id == routeId && x.SellerId == a.SellerId && x.DriverMembershipId == a.MembershipId);
        return r is null ? Res.Fail(404, "not_found", "Route not found.") : await svc.StartRouteAsync(r, a.UserId);
    }

    public async Task<Res> ConsentAsync(AccountAccess a, bool? consent, bool? paused)
    {
        var now = time.UtcNow();
        var c = await db.DriverConsents.FirstOrDefaultAsync(x => x.MembershipId == a.MembershipId);
        if (c is null) db.DriverConsents.Add(c = new DriverConsent { MembershipId = a.MembershipId, SellerId = a.SellerId });
        if (consent is { } v) { c.Consented = v; if (!v) c.Paused = false; }
        if (paused is { } p && c.Consented) c.Paused = p;
        c.UpdatedAt = now;
        // withdrawing consent (or pausing) removes what was collected: nothing lingers for 24 hours
        if (!c.Consented || c.Paused) await db.DriverLocations.Where(l => l.MembershipId == a.MembershipId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        return Res.Success(new { consented = c.Consented, paused = c.Paused });
    }

    public async Task<Res> LocationAsync(AccountAccess a, LocationRequest req)
    {
        if (!Geo.ValidLatLng(req.Lat, req.Lng)) return Res.Invalid("A valid position is required.", "position");
        var c = await db.DriverConsents.AsNoTracking().FirstOrDefaultAsync(x => x.MembershipId == a.MembershipId);
        if (c is not { Consented: true }) return Res.Fail(403, "consent_required", "Location sharing needs your consent.");
        if (c.Paused) return Res.Conflict("Location sharing is paused.", "sharing_paused");
        var route = await db.DeliveryRoutes.FirstOrDefaultAsync(r => r.SellerId == a.SellerId && r.DriverMembershipId == a.MembershipId && r.Status == RouteStatus.Active);
        if (route is null) return Res.Conflict("Locations are only accepted while a route is active.", "no_active_route");
        var now = time.UtcNow();
        var (lat, lng) = (Geo.Coarse(req.Lat!.Value), Geo.Coarse(req.Lng!.Value));      // rounded BEFORE anything is stored
        db.DriverLocations.Add(new DriverLocation { SellerId = a.SellerId, RouteId = route.Id, MembershipId = a.MembershipId, Lat = lat, Lng = lng, At = now, ExpiresAt = now.AddHours(opt.Value.LocationRetentionHours) });
        var stops = await db.LocalDeliveries.Where(x => x.RouteId == route.Id && (x.Status == LocalStatus.InTransit || x.Status == LocalStatus.OutForDelivery)).OrderBy(x => x.StopSeq).ToListAsync();
        // the stop that is out for delivery goes first, then the others in route order
        stops = stops.OrderBy(s => s.Status == LocalStatus.OutForDelivery ? 0 : 1).ThenBy(s => s.StopSeq).ToList();
        var etas = Eta.ForStops(lat, lng, stops.Select(s => ((double?)s.DestLat, (double?)s.DestLng)).ToList(), now);
        for (var i = 0; i < stops.Count; i++) stops[i].EtaUtc = etas[i];
        var next = stops.FirstOrDefault(s => s.Status == LocalStatus.OutForDelivery);
        if (next is not null && (next.LastEtaEventAt is null || (now - next.LastEtaEventAt.Value).TotalSeconds >= opt.Value.EtaEventSeconds))
        {
            var order = await orders.Full(db.Orders.Where(x => x.Id == next.OrderId)).FirstAsync();
            next.LastEtaEventAt = now;
            // the buyer's event carries a coarse position ONLY because this buyer's stop is the next one
            await svc.RecordAsync(next, order, TrackingStatus.OutForDelivery, "On the way", now, (lat, lng), history: false);
        }
        await db.SaveChangesAsync();
        return Res.Success(new { accepted = true });
    }
}
