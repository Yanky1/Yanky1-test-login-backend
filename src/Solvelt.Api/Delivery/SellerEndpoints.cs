using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Delivery;

public record ZoneInput(Guid? Id, string? Name, string? Country, double? OriginLat, double? OriginLng, double? RadiusKm, List<string>? Postcodes, List<double[]>? Polygon,
    long? FeeCents, long? FreeOverCents, long? MinOrderCents, int? Priority, bool? Active);
public record WindowInput(Guid? Id, string? Start, string? End, int? Capacity, List<int>? Days, bool? Active);
public record SettingsInput(Guid? BrandId, bool? Enabled, string? TimeZone, string? SameDayCutoff, int? MinLeadMinutes, int? MaxDaysAhead, List<ZoneInput>? Zones, List<WindowInput>? Windows, int? Version);
public record AssignInput(Guid? DriverMembershipId);
public record RouteInput(Guid? DriverMembershipId, DateOnly? Date, List<Guid>? DeliveryIds, bool? Optimize, double? StartLat, double? StartLng);
public record ReorderInput(List<Guid>? DeliveryIds);
public record ConvertInput(Guid? WindowId, DateOnly? Date, double? Lat, double? Lng);

public static class DeliverySellerEndpoints
{
    internal static int? Minutes(string? hhmm)
    {
        if (hhmm is null || !TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm", null, System.Globalization.DateTimeStyles.None, out var t)) return null;
        return t.Hour * 60 + t.Minute;
    }
    internal static string Hm(int m) => $"{m / 60:00}:{m % 60:00}";
    private static int MaskOf(IEnumerable<int> days) => days.Where(d => d is >= 0 and <= 6).Aggregate(0, (m, d) => m | 1 << d);
    private static IEnumerable<int> Days(int mask) => Enumerable.Range(0, 7).Where(d => (mask & 1 << d) != 0);

    internal static object SettingDto(DeliverySetting? s, Guid? brandId) => s is null
        ? new { brandId, enabled = false, timeZone = "UTC", sameDayCutoff = (string?)null, minLeadMinutes = 60, maxDaysAhead = 7, zones = Array.Empty<object>(), windows = Array.Empty<object>(), version = 0, exists = false }
        : new
        {
            brandId = s.BrandId, enabled = s.Enabled, timeZone = s.TimeZone, sameDayCutoff = s.SameDayCutoffMinutes is { } c ? Hm(c) : null, minLeadMinutes = s.MinLeadMinutes, maxDaysAhead = s.MaxDaysAhead, version = s.Version, exists = true,
            zones = s.Zones.OrderBy(z => z.Priority).ThenBy(z => z.Name).Select(z => (object)new
            {
                id = z.Id, name = z.Name, country = z.Country, originLat = z.OriginLat, originLng = z.OriginLng, radiusKm = z.RadiusKm, postcodes = ZoneEngine.Postcodes(z), polygon = ZoneEngine.Polygon(z),
                feeCents = z.FeeCents, freeOverCents = z.FreeOverCents, minOrderCents = z.MinOrderCents, priority = z.Priority, active = z.Active
            }),
            windows = s.Windows.OrderBy(w => w.StartMinutes).Select(w => (object)new { id = w.Id, start = Hm(w.StartMinutes), end = Hm(w.EndMinutes), capacity = w.Capacity, days = Days(w.DaysMask), active = w.Active })
        };

    private static IResult Bad(string field, string msg) => Validation.Invalid(field, msg);

    public static void MapDeliverySeller(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/seller/delivery").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders").RequireActiveAccount();

        // ---- settings ------------------------------------------------------------------------------------------------------
        g.MapGet("/settings", async (Guid? brandId, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            if (brandId is { } b && await db.RequireBrandAccess(a, b) is null) return Results.NotFound();
            var s = await db.DeliverySettings.AsNoTracking().Include(x => x.Zones).Include(x => x.Windows).FirstOrDefaultAsync(x => x.SellerId == a.SellerId && x.ScopeKey == (brandId ?? Guid.Empty));
            return Results.Ok(SettingDto(s, brandId));
        }).RequirePermission(Permission.DeliveryRead);

        g.MapPut("/settings", async (SettingsInput r, HttpContext ctx, AppDbContext db, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            if (r.BrandId is { } b && await db.RequireBrandAccess(a, b) is null) return Results.NotFound();
            var tzId = (r.TimeZone ?? "UTC").Trim();
            if (CalendarServiceZone(tzId) is null) return Bad("timeZone", "Unknown time zone.");
            int? cutoff = null;
            if (!string.IsNullOrWhiteSpace(r.SameDayCutoff)) { cutoff = Minutes(r.SameDayCutoff); if (cutoff is null) return Bad("sameDayCutoff", "Use HH:mm."); }
            if (r.MinLeadMinutes is < 0 or > 1440) return Bad("minLeadMinutes", "Between 0 and 1440.");
            if (r.MaxDaysAhead is < 0 or > 30) return Bad("maxDaysAhead", "Between 0 and 30.");
            var zones = r.Zones ?? []; var windows = r.Windows ?? [];
            if (zones.Count > 20) return Bad("zones", "At most 20 zones.");
            if (windows.Count > 20) return Bad("windows", "At most 20 windows.");
            foreach (var z in zones)
            {
                if (string.IsNullOrWhiteSpace(z.Name) || z.Name.Trim().Length > 80) return Bad("zones", "Every zone needs a name (max 80 characters).");
                if (z.FeeCents is < 0 or > 100_000_000 || z.FreeOverCents is < 0 or > 1_000_000_000 || z.MinOrderCents is < 0 or > 1_000_000_000) return Bad("zones", "Fees and thresholds must be between 0 and a sane maximum.");
                if (z.RadiusKm is < 0 or > 500 || (z.RadiusKm > 0) && !Geo.ValidLatLng(z.OriginLat, z.OriginLng)) return Bad("zones", "A radius needs a valid origin (lat/lng) and at most 500 km.");
                if ((z.Postcodes?.Count ?? 0) > 1000 || z.Postcodes?.Any(p => p.Length > 12) == true) return Bad("zones", "Too many or too long postcodes.");
                if (z.Polygon is { } poly && (poly.Count > 200 || poly.Any(p => p.Length != 2 || !Geo.ValidLatLng(p[0], p[1])) || poly.Count is > 0 and < 3)) return Bad("zones", "A polygon needs 3-200 valid [lat,lng] points.");
                if (z.Country is { } c && c.Trim().Length != 2) return Bad("zones", "country must be a 2-letter code.");
                if (z.Postcodes is not { Count: > 0 } && z.RadiusKm is not > 0 && z.Polygon is not { Count: >= 3 }) return Bad("zones", "A zone needs a radius, postcodes or a polygon.");
            }
            foreach (var w in windows)
            {
                var (s0, e0) = (Minutes(w.Start), Minutes(w.End));
                if (s0 is null || e0 is null || e0 <= s0) return Bad("windows", "Each window needs start < end (HH:mm).");
                if (w.Capacity is not (>= 1 and <= 500)) return Bad("windows", "Capacity must be between 1 and 500.");
            }
            var now = time.UtcNow();
            var s = await db.DeliverySettings.Include(x => x.Zones).Include(x => x.Windows).FirstOrDefaultAsync(x => x.SellerId == a.SellerId && x.ScopeKey == (r.BrandId ?? Guid.Empty));
            if (s is null) { s = new DeliverySetting { SellerId = a.SellerId, BrandId = r.BrandId, ScopeKey = r.BrandId ?? Guid.Empty, CreatedAt = now }; db.DeliverySettings.Add(s); }
            else if (r.Version is { } v && v != s.Version) return OrderEndpoints.Problem(409, "version_conflict", "The settings changed since you loaded them.");
            s.Enabled = r.Enabled ?? false; s.TimeZone = tzId; s.SameDayCutoffMinutes = cutoff; s.MinLeadMinutes = r.MinLeadMinutes ?? 60; s.MaxDaysAhead = r.MaxDaysAhead ?? 7; s.Version++; s.UpdatedAt = now;

            var keepZ = zones.Where(z => z.Id != null).Select(z => z.Id!.Value).ToHashSet();
            foreach (var old in s.Zones.Where(z => !keepZ.Contains(z.Id)).ToList()) { s.Zones.Remove(old); db.DeliveryZones.Remove(old); }
            foreach (var z in zones)
            {
                var row = z.Id is { } zid ? s.Zones.FirstOrDefault(x => x.Id == zid) : null;
                if (row is null) { row = new DeliveryZone { SettingId = s.Id, SellerId = a.SellerId }; db.DeliveryZones.Add(row); }
                row.Name = z.Name!.Trim(); row.Country = (z.Country ?? "US").Trim().ToUpperInvariant(); row.OriginLat = z.OriginLat; row.OriginLng = z.OriginLng; row.RadiusKm = z.RadiusKm;
                row.PostcodesJson = JsonSerializer.Serialize((z.Postcodes ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList());
                row.PolygonJson = z.Polygon is { Count: >= 3 } ? JsonSerializer.Serialize(z.Polygon) : null;
                row.FeeCents = z.FeeCents ?? 0; row.FreeOverCents = z.FreeOverCents; row.MinOrderCents = z.MinOrderCents ?? 0; row.Priority = z.Priority ?? 0; row.Active = z.Active ?? true;
            }
            var keepW = windows.Where(w => w.Id != null).Select(w => w.Id!.Value).ToHashSet();
            foreach (var old in s.Windows.Where(w => !keepW.Contains(w.Id)).ToList())
            {
                // a window that has bookings is switched off instead of deleted: existing deliveries keep their window
                if (await db.LocalDeliveries.AnyAsync(d => d.WindowId == old.Id)) old.Active = false;
                else { s.Windows.Remove(old); db.DeliveryWindows.Remove(old); db.DeliveryWindowUsage.RemoveRange(db.DeliveryWindowUsage.Where(u => u.WindowId == old.Id)); }
            }
            foreach (var w in windows)
            {
                var row = w.Id is { } wid ? s.Windows.FirstOrDefault(x => x.Id == wid) : null;
                if (row is null) { row = new DeliveryWindow { SettingId = s.Id, SellerId = a.SellerId }; db.DeliveryWindows.Add(row); }
                row.StartMinutes = Minutes(w.Start)!.Value; row.EndMinutes = Minutes(w.End)!.Value; row.Capacity = w.Capacity!.Value; row.DaysMask = MaskOf(w.Days ?? [1, 2, 3, 4, 5]); row.Active = w.Active ?? true;
            }
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return OrderEndpoints.Problem(409, "version_conflict", "The settings changed since you loaded them."); }
            await audit.WriteAsync("delivery.settings_changed", a.UserId, null, $"account={a.SellerId};brand={r.BrandId}");
            db.ChangeTracker.Clear();
            var fresh = await db.DeliverySettings.AsNoTracking().Include(x => x.Zones).Include(x => x.Windows).FirstAsync(x => x.Id == s.Id);
            return Results.Ok(SettingDto(fresh, r.BrandId));
        }).RequirePermission(Permission.DeliverySettings);

        // ---- board ----------------------------------------------------------------------------------------------------------
        g.MapGet("/drivers", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var rows = await (from m in db.Memberships.AsNoTracking() join u in db.Users on m.UserId equals u.Id
                              where m.SellerId == a.SellerId && m.Status == MembershipStatus.Active && m.Role == AccountRole.Driver select new { m.Id, u.Email }).ToListAsync();
            return Results.Ok(rows.Select(r => new { membershipId = r.Id, email = r.Email, firstName = DeliveryService.FirstName(r.Email) }));
        }).RequirePermission(Permission.DeliveryRead);

        g.MapGet("/board", async (DateOnly? date, HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var now = time.UtcNow();
            var tzId = await db.DeliverySettings.AsNoTracking().Where(s => s.SellerId == a.SellerId).Select(s => s.TimeZone).FirstOrDefaultAsync() ?? "UTC";
            var day = date ?? WindowEngine.LocalToday(WindowEngine.Zone(tzId), now);
            var q = db.LocalDeliveries.AsNoTracking().Where(d => d.SellerId == a.SellerId && d.Date == day && d.Status != LocalStatus.Cancelled);
            if (!(a.AllBrands || a.Role >= AccountRole.Admin)) { var ids = a.BrandIds.ToList(); q = q.Where(d => ids.Contains(d.BrandId)); }
            var ds = await q.OrderBy(d => d.WindowStartUtc).ThenBy(d => d.StopSeq).ToListAsync();
            var orderIds = ds.Select(d => d.OrderId).ToList();
            var os = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.Number, o.ShipCity, o.ShipPostalCode, o.Status }).ToDictionaryAsync(o => o.Id);
            var drivers = await (from m in db.Memberships.AsNoTracking() join u in db.Users on m.UserId equals u.Id where m.SellerId == a.SellerId select new { m.Id, u.Email }).ToListAsync();
            string? Name(Guid? id) => drivers.Where(x => x.Id == id).Select(x => DeliveryService.FirstName(x.Email)).FirstOrDefault();
            var winIds = ds.Where(d => d.WindowId != null).Select(d => d.WindowId!.Value).Distinct().ToList();
            var wins = await db.DeliveryWindows.AsNoTracking().Where(w => w.SellerId == a.SellerId).ToListAsync();
            var usage = await db.DeliveryWindowUsage.AsNoTracking().Where(u => u.Date == day && wins.Select(w => w.Id).Contains(u.WindowId)).ToListAsync();
            var routes = await db.DeliveryRoutes.AsNoTracking().Where(r => r.SellerId == a.SellerId && r.Date == day && r.Status != RouteStatus.Cancelled).ToListAsync();
            return Results.Ok(new
            {
                date = day, timeZone = tzId,
                windows = wins.Where(w => w.Active || usage.Any(u => u.WindowId == w.Id && u.Booked > 0)).OrderBy(w => w.StartMinutes).Select(w =>
                {
                    var booked = usage.FirstOrDefault(u => u.WindowId == w.Id)?.Booked ?? 0;
                    return new { id = w.Id, start = Hm(w.StartMinutes), end = Hm(w.EndMinutes), capacity = w.Capacity, booked, warning = booked >= w.Capacity ? "full" : booked * 10 >= w.Capacity * 8 ? "nearly_full" : null };
                }),
                deliveries = ds.Select(d => new
                {
                    id = d.Id, orderId = d.OrderId, number = os.GetValueOrDefault(d.OrderId)?.Number, brandId = d.BrandId, status = d.Status.ToString(), trackingStatus = DeliveryService.StatusOf(d.Status), code = d.Code,
                    windowId = d.WindowId, windowStart = d.WindowStartUtc, windowEnd = d.WindowEndUtc, city = os.GetValueOrDefault(d.OrderId)?.ShipCity, postalCode = os.GetValueOrDefault(d.OrderId)?.ShipPostalCode,
                    routeId = d.RouteId, seq = d.StopSeq, driverMembershipId = d.DriverMembershipId, driver = Name(d.DriverMembershipId), feeCents = d.FeeCents, eta = d.EtaUtc,
                    late = DeliveryService.Live.Contains(d.Status) && d.Status != LocalStatus.Unassigned && d.WindowEndUtc < now || d.Status == LocalStatus.Unassigned && d.WindowStartUtc < now,
                    exception = d.Status == LocalStatus.Failed, failReason = d.FailReason, attempts = d.Attempts, hasCoordinates = d.DestLat != null, version = d.Version,
                    orderStatus = os.GetValueOrDefault(d.OrderId)?.Status.ToString()
                }),
                routes = routes.Select(r => new { id = r.Id, status = r.Status.ToString(), driverMembershipId = r.DriverMembershipId, driver = Name(r.DriverMembershipId), startedAt = r.StartedAt, version = r.Version })
            });
        }).RequirePermission(Permission.DeliveryRead);

        // ---- dispatch -------------------------------------------------------------------------------------------------------
        async Task<LocalDelivery?> Mine(AppDbContext db, AccountAccess a, Guid id, bool write)
        {
            var d = await db.LocalDeliveries.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId);
            return d is not null && a.CanWorkOnBrand(d.BrandId, false) && (!write || a.Has(Permission.DeliveryDispatch) && a.CanSeeBrand(d.BrandId)) ? d : null;
        }
        async Task<DeliveryRoute?> MyRoute(AppDbContext db, AccountAccess a, Guid id)
        {
            var r = await db.DeliveryRoutes.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId);
            if (r is null) return null;
            // a scoped manager only touches routes whose stops are all in their brands
            if (!(a.AllBrands || a.Role >= AccountRole.Admin))
            {
                var brands = await db.LocalDeliveries.Where(x => x.RouteId == r.Id).Select(x => x.BrandId).Distinct().ToListAsync();
                if (brands.Any(b => !a.CanSeeBrand(b))) return null;
            }
            return r;
        }

        g.MapPost("/deliveries/{id:guid}/assign", async (Guid id, AssignInput r, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var a = ctx.GetAccess()!;
            var d = await Mine(db, a, id, true);
            if (d is null) return Results.NotFound();
            return r.DriverMembershipId is not { } m ? Bad("driverMembershipId", "Choose a driver.") : OrderEndpoints.ToResult(await svc.AssignAsync(d, m, a.UserId));
        }).RequirePermission(Permission.DeliveryDispatch);

        g.MapPost("/deliveries/{id:guid}/unassign", async (Guid id, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var a = ctx.GetAccess()!;
            var d = await Mine(db, a, id, true);
            return d is null ? Results.NotFound() : OrderEndpoints.ToResult(await svc.UnassignAsync(d, a.UserId));
        }).RequirePermission(Permission.DeliveryDispatch);

        g.MapPost("/routes", async (RouteInput r, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var a = ctx.GetAccess()!;
            if (r.DriverMembershipId is not { } m) return Bad("driverMembershipId", "Choose a driver.");
            if (r.DeliveryIds is not { Count: > 0 and <= 100 }) return Bad("deliveryIds", "Choose 1-100 deliveries.");
            var assigned = new List<Guid>();
            foreach (var id in r.DeliveryIds.Distinct())
            {
                var d = await Mine(db, a, id, true);
                if (d is null) return Results.NotFound();
                var res = await svc.AssignAsync(d, m, a.UserId);
                if (!res.Ok) return OrderEndpoints.ToResult(res);
                assigned.Add(id);
            }
            var routeId = (await db.LocalDeliveries.AsNoTracking().FirstAsync(x => x.Id == assigned[0])).RouteId!.Value;
            if (r.StartLat is { } || r.StartLng is { })
            {
                if (!Geo.ValidLatLng(r.StartLat, r.StartLng)) return Bad("startLat", "Invalid start position.");
                await db.DeliveryRoutes.Where(x => x.Id == routeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartLat, r.StartLat).SetProperty(x => x.StartLng, r.StartLng));
            }
            db.ChangeTracker.Clear();
            if (r.Optimize == true) { var route = await db.DeliveryRoutes.FirstAsync(x => x.Id == routeId); await svc.OptimizeAsync(route); }
            return Results.Ok(new { routeId });
        }).RequirePermission(Permission.DeliveryDispatch);

        g.MapPost("/routes/{id:guid}/optimize", async (Guid id, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var route = await MyRoute(db, ctx.GetAccess()!, id);
            return route is null ? Results.NotFound() : OrderEndpoints.ToResult(await svc.OptimizeAsync(route));
        }).RequirePermission(Permission.DeliveryDispatch);

        g.MapPost("/routes/{id:guid}/reorder", async (Guid id, ReorderInput r, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var route = await MyRoute(db, ctx.GetAccess()!, id);
            return route is null ? Results.NotFound() : OrderEndpoints.ToResult(await svc.ReorderAsync(route, r.DeliveryIds ?? []));
        }).RequirePermission(Permission.DeliveryDispatch);

        g.MapPost("/routes/{id:guid}/reassign", async (Guid id, AssignInput r, HttpContext ctx, AppDbContext db, DeliveryService svc) =>
        {
            var a = ctx.GetAccess()!;
            var route = await MyRoute(db, a, id);
            if (route is null) return Results.NotFound();
            return r.DriverMembershipId is not { } m ? Bad("driverMembershipId", "Choose a driver.") : OrderEndpoints.ToResult(await svc.ReassignRouteAsync(route, m, a.UserId));
        }).RequirePermission(Permission.DeliveryDispatch);

        // printable / shareable route sheet (seller side, full details)
        g.MapGet("/routes/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var route = await MyRoute(db, a, id);
            if (route is null) return Results.NotFound();
            var stops = await db.LocalDeliveries.AsNoTracking().Where(x => x.RouteId == id).OrderBy(x => x.StopSeq).ToListAsync();
            var ids = stops.Select(s => s.OrderId).ToList();
            var os = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
            return Results.Ok(new
            {
                id = route.Id, date = route.Date, status = route.Status.ToString(), version = route.Version,
                stops = stops.Select(s => new
                {
                    id = s.Id, seq = s.StopSeq, status = s.Status.ToString(), code = s.Code, windowStart = s.WindowStartUtc, windowEnd = s.WindowEndUtc, number = os[s.OrderId].Number,
                    name = os[s.OrderId].ShipName, address = string.Join(", ", new[] { os[s.OrderId].ShipLine1, os[s.OrderId].ShipLine2, os[s.OrderId].ShipCity, os[s.OrderId].ShipRegion, os[s.OrderId].ShipPostalCode }.Where(x => !string.IsNullOrWhiteSpace(x)))
                })
            });
        }).RequirePermission(Permission.DeliveryRead);

        // ---- conversion + tracking view + proof media ----------------------------------------------------------------------------------
        var orders = v1.MapGroup("/seller/orders").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("orders").RequireActiveAccount();

        orders.MapPost("/{id:guid}/local-delivery", async (Guid id, ConvertInput r, HttpContext ctx, AppDbContext db, OrderActions act, DeliveryService svc) =>
        {
            var a = ctx.GetAccess()!;
            var o = await act.Full(db.OrdersFor(a)).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, true)) return Results.NotFound();
            if (r.WindowId is not { } w || r.Date is not { } day) return Bad("windowId", "Choose a delivery window and date.");
            if ((r.Lat is not null || r.Lng is not null) && !Geo.ValidLatLng(r.Lat, r.Lng)) return Bad("lat", "Invalid coordinates.");
            return OrderEndpoints.ToResult(await svc.ConvertAsync(o, a.UserId, w, day, r.Lat, r.Lng));
        }).RequirePermission(Permission.DeliveryDispatch);

        orders.MapGet("/{id:guid}/tracking", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var o = await db.OrdersFor(a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (o is null) return Results.NotFound();
            var d = await db.LocalDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == id);
            var events = await db.TrackingEvents.AsNoTracking().Where(e => e.OrderId == id).OrderBy(e => e.OccurredAt).ThenBy(e => e.RecordedAt).ToListAsync();
            var proofs = d is null ? [] : await db.DeliveryProofs.AsNoTracking().Where(p => p.DeliveryId == d.Id && p.DeletedAt == null).ToListAsync();
            var attempts = d is null ? [] : await db.DeliveryAttempts.AsNoTracking().Where(x => x.DeliveryId == d.Id).OrderBy(x => x.Number).ToListAsync();
            return Results.Ok(new
            {
                method = d is null ? DeliveryMethod.Carrier : DeliveryMethod.Local,
                delivery = d is null ? null : new { id = d.Id, code = d.Code, status = d.Status.ToString(), trackingStatus = DeliveryService.StatusOf(d.Status), windowStart = d.WindowStartUtc, windowEnd = d.WindowEndUtc, feeCents = d.FeeCents, attempts = d.Attempts, eta = d.EtaUtc },
                events = events.Select(e => new { status = e.Status, detail = e.Detail, at = e.OccurredAt, method = e.Method }),
                attempts = attempts.Select(x => new { number = x.Number, outcome = x.Outcome, reason = x.Reason, note = x.Note, recipientName = x.RecipientName, placement = x.Placement, at = x.ServerAt }),
                proofs = proofs.Select(p => new { id = p.Id, kind = p.Kind, url = $"/api/v1/seller/delivery/proofs/{p.Id}", at = p.CreatedAt })
            });
        }).RequirePermission(Permission.BrandWorkRead);

        g.MapGet("/proofs/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, IFileStorage storage) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.DeliveryProofs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && x.DeletedAt == null);
            if (p is null) return Results.NotFound();
            var d = await db.LocalDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.DeliveryId);
            if (d is null || !a.CanSeeBrand(d.BrandId)) return Results.NotFound();
            return await ProofFile(p, storage, ctx);
        }).RequirePermission(Permission.DeliveryRead);
    }

    internal static async Task<IResult> ProofFile(DeliveryProof p, IFileStorage storage, HttpContext ctx)
    {
        var s = await storage.OpenReadAsync(p.StorageKey);
        if (s is null) return Results.NotFound();
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Cache-Control"] = "private, no-store";
        ctx.Response.Headers["Content-Disposition"] = "inline";
        return Results.Stream(s, p.ContentType);
    }

    private static TimeZoneInfo? CalendarServiceZone(string id) => Solvelt.Api.Bookings.CalendarService.FindZone(id);
}
