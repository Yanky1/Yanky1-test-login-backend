using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Delivery;

public record DeliveryAddress(string? PostalCode, string? Country, double? Lat, double? Lng);

public static class Geo
{
    public static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0088;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1); var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Ray casting; polygon vertices are (lat, lng).</summary>
    public static bool InPolygon(double lat, double lng, IReadOnlyList<double[]> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (yi, xi) = (poly[i][0], poly[i][1]); var (yj, xj) = (poly[j][0], poly[j][1]);
            if (yi > lat != yj > lat && lng < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>~100 m: three decimals of a degree. Applied before anything is stored or sent to the buyer.</summary>
    public static double Coarse(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
    public static bool ValidLatLng(double? lat, double? lng) => lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
}

public record ZoneResult(DeliveryZone? Zone, long FeeCents, string? Reason)
{
    public bool Eligible => Zone is not null;
}

public static class ZoneEngine
{
    public static string NormPostcode(string? p) => new string((p ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static List<string> Postcodes(DeliveryZone z)
    {
        try { return JsonSerializer.Deserialize<List<string>>(z.PostcodesJson) ?? []; } catch (JsonException) { return []; }
    }

    public static List<double[]> Polygon(DeliveryZone z)
    {
        if (string.IsNullOrEmpty(z.PolygonJson)) return [];
        try { return JsonSerializer.Deserialize<List<double[]>>(z.PolygonJson)?.Where(p => p.Length == 2).ToList() ?? []; } catch (JsonException) { return []; }
    }

    public static bool Matches(DeliveryZone z, DeliveryAddress a)
    {
        if (!z.Active) return false;
        if (!string.Equals(z.Country, a.Country, StringComparison.OrdinalIgnoreCase)) return false;
        var pc = NormPostcode(a.PostalCode);
        if (pc.Length > 0)
            foreach (var raw in Postcodes(z))
            {
                var want = NormPostcode(raw);
                if (want.Length == 0) continue;
                if (raw.TrimEnd().EndsWith('*') ? pc.StartsWith(want) : pc == want) return true;
            }
        if (a.Lat is { } lat && a.Lng is { } lng && Geo.ValidLatLng(lat, lng))
        {
            if (z.OriginLat is { } olat && z.OriginLng is { } olng && z.RadiusKm is > 0 && Geo.Km(olat, olng, lat, lng) <= z.RadiusKm) return true;
            var poly = Polygon(z);
            if (poly.Count >= 3 && Geo.InPolygon(lat, lng, poly)) return true;
        }
        return false;
    }

    public static long FeeFor(DeliveryZone z, long subtotalCents) => z.FreeOverCents is { } t && subtotalCents >= t ? 0 : z.FeeCents;

    /// <summary>The best matching zone (lowest priority number, then lowest fee) whose minimum order is met.</summary>
    public static ZoneResult Evaluate(IEnumerable<DeliveryZone> zones, DeliveryAddress a, long subtotalCents)
    {
        var hits = zones.Where(z => Matches(z, a)).ToList();
        if (hits.Count == 0) return new(null, 0, "outside_zone");
        var ok = hits.Where(z => subtotalCents >= z.MinOrderCents).OrderBy(z => z.Priority).ThenBy(z => FeeFor(z, subtotalCents)).FirstOrDefault();
        return ok is null ? new(null, 0, "below_minimum") : new(ok, FeeFor(ok, subtotalCents), null);
    }
}

public record OfferedWindow(Guid WindowId, DateOnly Date, DateTime StartUtc, DateTime EndUtc, int Capacity, int Booked)
{
    public int Remaining => Math.Max(0, Capacity - Booked);
}

public static class WindowEngine
{
    public static TimeZoneInfo Zone(string? id)
    {
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static bool DayOn(int mask, DayOfWeek d) => (mask & (1 << (int)d)) != 0;

    /// <summary>UTC instant of a local date + minutes after midnight; null for a time that does not exist (DST gap).</summary>
    public static DateTime? ToUtc(TimeZoneInfo tz, DateOnly d, int minutes)
    {
        var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(minutes);
        if (tz.IsInvalidTime(local)) return null;
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public static DateOnly LocalToday(TimeZoneInfo tz, DateTime nowUtc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));

    /// <summary>Is this window on this date still bookable at <paramref name="nowUtc"/>? Applies days, max-days-ahead, same-day cutoff and lead time.</summary>
    public static OfferedWindow? Offer(DeliverySetting s, DeliveryWindow w, DateOnly date, DateTime nowUtc, int booked)
    {
        if (!s.Enabled || !w.Active || w.Capacity < 1 || w.EndMinutes <= w.StartMinutes) return null;
        var tz = Zone(s.TimeZone);
        var today = LocalToday(tz, nowUtc);
        if (date < today || date > today.AddDays(s.MaxDaysAhead)) return null;
        if (!DayOn(w.DaysMask, date.DayOfWeek)) return null;
        if (date == today)
        {
            if (s.SameDayCutoffMinutes is not { } cutoff) return null;
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            if (nowLocal.TimeOfDay.TotalMinutes >= cutoff) return null;
        }
        var start = ToUtc(tz, date, w.StartMinutes); var end = ToUtc(tz, date, w.EndMinutes);
        if (start is null || end is null || start < nowUtc.AddMinutes(s.MinLeadMinutes)) return null;
        return new(w.Id, date, start.Value, end.Value, w.Capacity, booked);
    }

    public static async Task<List<OfferedWindow>> ListAsync(AppDbContext db, DeliverySetting s, DateTime nowUtc, DateOnly? from = null, int? days = null)
    {
        var tz = Zone(s.TimeZone);
        var start = from ?? LocalToday(tz, nowUtc);
        var n = Math.Min(days ?? s.MaxDaysAhead + 1, 31);
        var ids = s.Windows.Select(w => w.Id).ToList();
        var end = start.AddDays(n);
        var usage = await db.DeliveryWindowUsage.AsNoTracking().Where(u => ids.Contains(u.WindowId) && u.Date >= start && u.Date <= end).ToListAsync();
        var res = new List<OfferedWindow>();
        for (var i = 0; i < n; i++)
        {
            var d = start.AddDays(i);
            foreach (var w in s.Windows.OrderBy(w => w.StartMinutes))
                if (Offer(s, w, d, nowUtc, usage.FirstOrDefault(u => u.WindowId == w.Id && u.Date == d)?.Booked ?? 0) is { } o) res.Add(o);
        }
        return res;
    }
}

/// <summary>Window capacity: a conditional single-statement UPDATE, so parallel bookings can never overbook (also on PostgreSQL).</summary>
public class CapacityService(AppDbContext db)
{
    public async Task<bool> TryBookAsync(Guid windowId, DateOnly date, int capacity)
    {
        if (capacity < 1) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var n = await db.DeliveryWindowUsage.Where(u => u.WindowId == windowId && u.Date == date && u.Booked < capacity)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Booked, u => u.Booked + 1));
            if (n == 1) return true;
            if (await db.DeliveryWindowUsage.AnyAsync(u => u.WindowId == windowId && u.Date == date)) return false;   // the row exists and is full
            // first booking of this window/date: INSERT ... ON CONFLICT DO NOTHING (no exception that could poison a PostgreSQL transaction); a lost race loops back to the UPDATE
            var ins = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO delivery_window_usage (\"WindowId\", \"Date\", \"Booked\") VALUES ({windowId}, {date}, 1) ON CONFLICT (\"WindowId\", \"Date\") DO NOTHING");
            if (ins == 1) return true;
        }
        return false;
    }

    public Task ReleaseAsync(Guid windowId, DateOnly date) =>
        db.DeliveryWindowUsage.Where(u => u.WindowId == windowId && u.Date == date && u.Booked > 0).ExecuteUpdateAsync(s => s.SetProperty(u => u.Booked, u => u.Booked - 1));
}
