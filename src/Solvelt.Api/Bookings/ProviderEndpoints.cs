using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Bookings;

public record ProviderWrite(bool? IsProvider, string? TimeZone, bool? ReminderDayBefore, bool? ReminderHourBefore);
public record HoursInterval(string? Start, string? End);
public record HoursDay(int Day, List<HoursInterval>? Intervals);
public record OverrideDay(string? Date, List<HoursInterval>? Intervals);
public record AvailabilityWrite(List<HoursDay>? Weekly, List<OverrideDay>? Overrides);
public record BlockWrite(string? Kind, DateTime? Start, DateTime? End, bool AllDay, string? Date, string? EndDate, string? Note);

/// <summary>Who may see/act on what in the calendar and bookings area (documented in docs/SERVICES.md section "Permissions").</summary>
public static class BookingAccess
{
    public static bool CanManageProvider(AccountAccess a, Guid membershipId) => a.Has(Permission.ProvidersManage) || a.MembershipId == membershipId;

    public static async Task<bool> IsProviderAsync(AppDbContext db, Guid membershipId) =>
        await db.ProviderProfiles.AsNoTracking().AnyAsync(p => p.MembershipId == membershipId && p.IsProvider);

    /// <summary>Bookings the caller may SEE. Owner/Admin: all. Manager: their brands + bookings assigned to them. Viewer: assigned to them when they are a provider, otherwise their brands.</summary>
    public static async Task<IQueryable<Booking>> BookingsForAsync(this AppDbContext db, AccountAccess a)
    {
        var q = db.Bookings.Where(b => b.SellerId == a.SellerId && b.Status != BookingStatus.Hold);
        if (a.Role >= AccountRole.Admin) return q;
        var all = a.AllBrands;
        var me = a.MembershipId; var ids = a.BrandIds.ToList();
        if (a.Role == AccountRole.Manager) return all ? q : q.Where(b => ids.Contains(b.BrandId) || b.ProviderId == me);
        if (await IsProviderAsync(db, me)) return q.Where(b => b.ProviderId == me);
        return all ? q : q.Where(b => ids.Contains(b.BrandId));
    }

    /// <summary>May the caller act on (write to) this booking? Owner/Admin; a Manager for their brands; any assigned provider for their own bookings.</summary>
    public static bool CanAct(AccountAccess a, Booking b) =>
        a.Role >= AccountRole.Admin || (a.Role == AccountRole.Manager && a.CanSeeBrand(b.BrandId)) || b.ProviderId == a.MembershipId;

    /// <summary>Customer address/phone/name: assigned provider and Owner/Admin only, from confirmation until redaction.</summary>
    public static bool SeesCustomerData(AccountAccess a, Booking b) =>
        b.PiiRedactedAt is null && BookingStateMachine.CustomerDataVisibleStatus(b.Status) && (a.Role >= AccountRole.Admin || b.ProviderId == a.MembershipId);
}

public static class ProviderEndpoints
{
    internal static IResult Problem(int status, string code, string title, object? extra = null) =>
        Results.Json(new { type = "about:blank", title, status, code, extra }, statusCode: status);

    private static bool Time(string? s, out int minutes)
    {
        minutes = 0;
        if (s is null) return false;
        if (s == "24:00") { minutes = 1440; return true; }
        if (!TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        minutes = t.Hour * 60 + t.Minute; return true;
    }

    private static string Fmt(int m) => m == 1440 ? "24:00" : $"{m / 60:D2}:{m % 60:D2}";

    /// <summary>Parses and validates a day's intervals (start &lt; end, no overlaps, at most 8).</summary>
    public static string? ParseIntervals(List<HoursInterval>? input, out List<(int S, int E)> result)
    {
        result = [];
        foreach (var i in input ?? [])
        {
            if (!Time(i.Start, out var s) || !Time(i.End, out var e) || s >= 1440) return "Use times like 09:00 (24-hour).";
            if (e <= s) return "Each interval must end after it starts.";
            result.Add((s, e));
        }
        if (result.Count > 8) return "At most 8 intervals per day.";
        var sorted = result.OrderBy(r => r.S).ToList();
        for (var k = 1; k < sorted.Count; k++) if (sorted[k].S < sorted[k - 1].E) return "Intervals can't overlap.";
        result = sorted;
        return null;
    }

    private static async Task<(AccountMembership? M, ProviderProfile? P)> MemberAsync(AppDbContext db, AccountAccess a, Guid membershipId)
    {
        var m = await db.Memberships.AsNoTracking().FirstOrDefaultAsync(x => x.Id == membershipId && x.SellerId == a.SellerId && x.Status == MembershipStatus.Active);
        if (m is null) return (null, null);
        var p = await db.ProviderProfiles.FirstOrDefaultAsync(x => x.MembershipId == membershipId);
        return (m, p);
    }

    /// <summary>Read access to one provider's calendar data: Owner/Admin any, everybody else only their own (others answer 404).</summary>
    private static bool CanSeeProvider(AccountAccess a, Guid membershipId) => a.Role >= AccountRole.Admin || a.MembershipId == membershipId;

    private static string NewToken() => "ics_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static void MapProviderEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/seller").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("bookings");

        // ---- providers ------------------------------------------------------------------------------------------------------
        s.MapGet("/providers", async (HttpContext ctx, AppDbContext db, CalendarService cal) =>
        {
            var a = ctx.GetAccess()!;
            var q = from m in db.Memberships.AsNoTracking()
                    join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                    where m.SellerId == a.SellerId && m.Status == MembershipStatus.Active
                    select new { m.Id, u.Email, m.Role };
            if (a.Role < AccountRole.Admin) q = q.Where(x => x.Id == a.MembershipId);
            var rows = await q.ToListAsync();
            var ids = rows.Select(r => r.Id).ToList();
            var profiles = await db.ProviderProfiles.AsNoTracking().Where(p => ids.Contains(p.MembershipId)).ToListAsync();
            var acct = await cal.AccountZoneIdAsync(a.SellerId);
            return Results.Json(new
            {
                accountTimeZone = acct,
                providers = rows.Select(r =>
                {
                    var p = profiles.FirstOrDefault(x => x.MembershipId == r.Id);
                    return new
                    {
                        membershipId = r.Id, email = r.Email, role = r.Role.ToString(), isProvider = p?.IsProvider ?? false, timeZone = p?.TimeZone, effectiveTimeZone = p?.TimeZone ?? acct,
                        reminderDayBefore = p?.ReminderDayBefore ?? true, reminderHourBefore = p?.ReminderHourBefore ?? true, icsActive = p?.IcsTokenHash != null, isMe = r.Id == a.MembershipId
                    };
                })
            });
        }).RequireActiveAccount();

        s.MapPut("/providers/{id:guid}", async (Guid id, ProviderWrite w, HttpContext ctx, AppDbContext db, CalendarService cal, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id)) return Results.NotFound();
            var (m, p) = await MemberAsync(db, a, id);
            if (m is null) return Results.NotFound();
            if (w.IsProvider is not null && !a.Has(Permission.ProvidersManage)) return AccessExtensions.Forbidden();
            if (w.TimeZone is { Length: > 0 } && CalendarService.FindZone(w.TimeZone) is null) return Validation.Invalid("timeZone", "Unknown time zone (use an IANA name such as America/New_York).");
            p ??= db.ProviderProfiles.Add(new ProviderProfile { MembershipId = id, SellerId = a.SellerId, UpdatedAt = time.UtcNow() }).Entity;
            if (w.IsProvider is { } on && on != p.IsProvider)
            {
                if (!on)
                {
                    var now = time.UtcNow();
                    if (await db.Bookings.AnyAsync(b => b.ProviderId == id && b.EndUtc > now && (CalendarService.BlockingStatuses.Contains(b.Status))))
                        return Problem(409, "has_bookings", "This person still has upcoming bookings. Reassign or cancel them first.");
                    db.ServiceProviders.RemoveRange(await db.ServiceProviders.Where(l => l.MembershipId == id).ToListAsync());
                }
                p.IsProvider = on;
                await audit.WriteAsync(on ? "provider.enabled" : "provider.disabled", a.UserId, null, $"membership={id};seller={a.SellerId}");
            }
            if (w.TimeZone is not null) p.TimeZone = string.IsNullOrWhiteSpace(w.TimeZone) ? null : w.TimeZone;
            if (w.ReminderDayBefore is { } d1) p.ReminderDayBefore = d1;
            if (w.ReminderHourBefore is { } h1) p.ReminderHourBefore = h1;
            p.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await cal.RefreshReadyProvidersAsync(a.SellerId);
            return Results.Ok(new { membershipId = id, isProvider = p.IsProvider, timeZone = p.TimeZone, reminderDayBefore = p.ReminderDayBefore, reminderHourBefore = p.ReminderHourBefore });
        }).RequireActiveAccount();

        s.MapPut("/booking-settings", async (BookingSettingsWrite w, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            if (CalendarService.FindZone(w.TimeZone) is null) return Validation.Invalid("timeZone", "Unknown time zone (use an IANA name such as America/New_York).");
            var row = await db.BookingSettings.FirstOrDefaultAsync(x => x.SellerId == a.SellerId) ?? db.BookingSettings.Add(new BookingSettings { SellerId = a.SellerId }).Entity;
            row.TimeZone = w.TimeZone;
            await db.SaveChangesAsync();
            return Results.Ok(new { timeZone = row.TimeZone });
        }).RequirePermission(Permission.ProvidersManage);

        // ---- working hours ------------------------------------------------------------------------------------------------------
        s.MapGet("/providers/{id:guid}/availability", async (Guid id, HttpContext ctx, AppDbContext db, CalendarService cal) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id) || (await MemberAsync(db, a, id)).M is null) return Results.NotFound();
            var hours = await db.WorkingHours.AsNoTracking().Where(h => h.MembershipId == id).ToListAsync();
            var overrides = await db.DateOverrides.AsNoTracking().Where(o => o.MembershipId == id).OrderBy(o => o.Date).ToListAsync();
            var zone = await cal.ZoneAsync(a.SellerId, id);
            return Results.Json(new
            {
                timeZone = zone.Id,
                weekly = hours.GroupBy(h => h.Day).OrderBy(x => x.Key).Select(gr => new { day = gr.Key, intervals = gr.OrderBy(h => h.StartMinute).Select(h => new { start = Fmt(h.StartMinute), end = Fmt(h.EndMinute) }) }),
                overrides = overrides.Select(o => new { date = o.Date.ToString("yyyy-MM-dd"), intervals = (BJson.From<List<int[]>>(o.IntervalsJson) ?? []).Select(x => new { start = Fmt(x[0]), end = Fmt(x[1]) }) })
            });
        }).RequireActiveAccount();

        s.MapPut("/providers/{id:guid}/availability", async (Guid id, AvailabilityWrite w, HttpContext ctx, AppDbContext db, CalendarService cal, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id)) return Results.NotFound();
            var (m, p) = await MemberAsync(db, a, id);
            if (m is null) return Results.NotFound();
            if (!BookingAccess.CanManageProvider(a, id)) return AccessExtensions.Forbidden();
            if (p is null || !p.IsProvider) return Problem(409, "not_a_provider", "This team member isn't set up as a service provider yet.");
            var weekly = new List<WorkingHour>(); var errors = new Dictionary<string, string[]>();
            foreach (var day in w.Weekly ?? [])
            {
                if (day.Day is < 0 or > 6) { errors["weekly"] = ["Day must be 0 (Sunday) to 6 (Saturday)."]; break; }
                if (ParseIntervals(day.Intervals, out var iv) is { } err) { errors["weekly"] = [$"{(DayOfWeek)day.Day}: {err}"]; break; }
                weekly.AddRange(iv.Select(i => new WorkingHour { MembershipId = id, Day = day.Day, StartMinute = i.S, EndMinute = i.E }));
            }
            if ((w.Weekly ?? []).GroupBy(d => d.Day).Any(gr => gr.Count() > 1)) errors["weekly"] = ["Each day may appear only once."];
            var overrides = new List<DateOverride>();
            if ((w.Overrides?.Count ?? 0) > 400) errors["overrides"] = ["At most 400 date overrides."];
            foreach (var o in w.Overrides ?? [])
            {
                if (!DateOnly.TryParseExact(o.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { errors["overrides"] = ["Use dates like 2026-12-24."]; break; }
                if (ParseIntervals(o.Intervals, out var iv) is { } err) { errors["overrides"] = [$"{o.Date}: {err}"]; break; }
                if (overrides.Any(x => x.Date == date)) { errors["overrides"] = [$"{o.Date} appears twice."]; break; }
                overrides.Add(new DateOverride { MembershipId = id, Date = date, IntervalsJson = BJson.Ser(iv.Select(i => new[] { i.S, i.E })) });
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            db.WorkingHours.RemoveRange(await db.WorkingHours.Where(h => h.MembershipId == id).ToListAsync());
            db.DateOverrides.RemoveRange(await db.DateOverrides.Where(o => o.MembershipId == id).ToListAsync());
            db.WorkingHours.AddRange(weekly); db.DateOverrides.AddRange(overrides);
            await db.SaveChangesAsync();
            await cal.RefreshReadyProvidersAsync(a.SellerId);
            await audit.WriteAsync("provider.hours_changed", a.UserId, null, $"membership={id};seller={a.SellerId}");
            return Results.Ok(new { ok = true });
        }).RequireActiveAccount();

        // ---- time off + manual blocks -----------------------------------------------------------------------------------------
        s.MapGet("/providers/{id:guid}/blocks", async (Guid id, DateTime? from, DateTime? to, HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id) || (await MemberAsync(db, a, id)).M is null) return Results.NotFound();
            var f = BJson.Utc(from ?? time.UtcNow().AddDays(-1)); var t = BJson.Utc(to ?? f.AddDays(400));
            var rows = await db.CalendarBlocks.AsNoTracking().Where(b => b.MembershipId == id && b.EndUtc > f && b.StartUtc < t).OrderBy(b => b.StartUtc).Take(500).ToListAsync();
            return Results.Json(new { blocks = rows.Select(BlockDto) });
        }).RequireActiveAccount();

        s.MapPost("/providers/{id:guid}/blocks", async (Guid id, BlockWrite w, HttpContext ctx, AppDbContext db, CalendarService cal, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id)) return Results.NotFound();
            var (m, p) = await MemberAsync(db, a, id);
            if (m is null) return Results.NotFound();
            if (!BookingAccess.CanManageProvider(a, id)) return AccessExtensions.Forbidden();
            if (p is null || !p.IsProvider) return Problem(409, "not_a_provider", "This team member isn't set up as a service provider yet.");
            var kind = w.Kind?.ToLowerInvariant() switch { "timeoff" => BlockKind.TimeOff, "busy" => BlockKind.Busy, _ => (BlockKind?)null };
            if (kind is null) return Validation.Invalid("kind", "Choose timeoff or busy.");
            if (w.Note is { Length: > 500 }) return Validation.Invalid("note", "At most 500 characters.");
            DateTime start, end;
            if (w.AllDay)
            {
                var zone = await cal.ZoneAsync(a.SellerId, id);
                if (!DateOnly.TryParseExact(w.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return Validation.Invalid("date", "Use a date like 2026-12-24.");
                var d2 = d1;
                if (w.EndDate is not null && !DateOnly.TryParseExact(w.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d2)) return Validation.Invalid("endDate", "Use a date like 2026-12-31.");
                if (d2 < d1 || d2.DayNumber - d1.DayNumber > 365) return Validation.Invalid("endDate", "The end date must be on or after the start date (at most a year).");
                start = SlotEngine.LocalToUtc(zone, d1, 0); end = SlotEngine.LocalToUtc(zone, d2.AddDays(1), 0);
            }
            else
            {
                if (w.Start is null || w.End is null) return Validation.Invalid("start", "Enter a start and end.");
                start = BJson.Utc(w.Start.Value); end = BJson.Utc(w.End.Value);
                if (end <= start) return Validation.Invalid("end", "The end must be after the start.");
                if ((end - start).TotalDays > 366) return Validation.Invalid("end", "A block can be at most a year long.");
            }
            if (await db.CalendarBlocks.CountAsync(b => b.MembershipId == id) >= 1000) return Problem(409, "too_many_blocks", "Remove some old blocks first.");
            var block = new CalendarBlock { SellerId = a.SellerId, MembershipId = id, Kind = kind.Value, StartUtc = start, EndUtc = end, AllDay = w.AllDay, Note = string.IsNullOrWhiteSpace(w.Note) ? null : w.Note.Trim(), CreatedAt = time.UtcNow() };
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
            var affected = await db.Bookings.CountAsync(b => b.ProviderId == id && b.BlockEndUtc > start && b.BlockStartUtc < end && CalendarService.BlockingStatuses.Contains(b.Status));
            return Results.Json(new { block = BlockDto(block), affectedBookings = affected }, statusCode: 201);
        }).RequireActiveAccount();

        s.MapDelete("/providers/{id:guid}/blocks/{blockId:guid}", async (Guid id, Guid blockId, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id) || (await MemberAsync(db, a, id)).M is null) return Results.NotFound();
            if (!BookingAccess.CanManageProvider(a, id)) return AccessExtensions.Forbidden();
            var n = await db.CalendarBlocks.Where(b => b.Id == blockId && b.MembershipId == id).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireActiveAccount();

        // ---- calendar view ---------------------------------------------------------------------------------------------------
        s.MapGet("/calendar", async (HttpContext ctx, AppDbContext db, CalendarService cal, TimeProvider time, IOptions<BookingOptions> opt, DateTime? from, DateTime? to, Guid? providerId, Guid? brandId, Guid? serviceId) =>
        {
            var a = ctx.GetAccess()!;
            var f = BJson.Utc(from ?? time.UtcNow().Date); var t = BJson.Utc(to ?? f.AddDays(7));
            if (t <= f) return Validation.Invalid("to", "'to' must be after 'from'.");
            if ((t - f).TotalDays > opt.Value.MaxAvailabilityDays) return Validation.Invalid("to", $"Ask for at most {opt.Value.MaxAvailabilityDays} days at a time.");
            var q = (await db.BookingsForAsync(a)).AsNoTracking().Where(b => b.EndUtc > f && b.StartUtc < t && b.Status != BookingStatus.Expired);
            if (providerId is { } pid) q = q.Where(b => b.ProviderId == pid);
            if (brandId is { } bid) q = q.Where(b => b.BrandId == bid);
            if (serviceId is { } sid) q = q.Where(b => b.ProductId == sid);
            var bookings = await q.OrderBy(b => b.StartUtc).Take(1000).ToListAsync();
            // providers visible in the view: Owner/Admin all, everybody else only themselves
            var provQ = from m in db.Memberships.AsNoTracking() join pp in db.ProviderProfiles.AsNoTracking() on m.Id equals pp.MembershipId join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                        where m.SellerId == a.SellerId && m.Status == MembershipStatus.Active && pp.IsProvider select new { m.Id, u.Email };
            if (a.Role < AccountRole.Admin) provQ = provQ.Where(x => x.Id == a.MembershipId || bookings.Select(b => b.ProviderId).Contains(x.Id));
            var provs = await provQ.ToListAsync();
            if (providerId is { } only) provs = provs.Where(x => x.Id == only).ToList();
            var outProviders = new List<object>(); var blocks = new List<object>(); var hours = new List<object>();
            var acct = await cal.AccountZoneIdAsync(a.SellerId);
            foreach (var p in provs)
            {
                var c = await cal.LoadAsync(a.SellerId, p.Id, f.AddDays(-2), t.AddDays(2), time.UtcNow());
                outProviders.Add(new { membershipId = p.Id, email = p.Email, timeZone = c.Zone.Id, isMe = p.Id == a.MembershipId });
                var own = a.Role >= AccountRole.Admin || p.Id == a.MembershipId;
                if (own)
                    foreach (var b in await db.CalendarBlocks.AsNoTracking().Where(x => x.MembershipId == p.Id && x.EndUtc > f && x.StartUtc < t).ToListAsync()) blocks.Add(BlockDto(b));
                for (var d = SlotEngine.LocalDate(c.Zone, f).AddDays(-1); d <= SlotEngine.LocalDate(c.Zone, t).AddDays(1); d = d.AddDays(1))
                    foreach (var w in SlotEngine.WorkingSpans(c, d)) if (w.EndUtc > f && w.StartUtc < t) hours.Add(new { providerId = p.Id, start = w.StartUtc, end = w.EndUtc });
            }
            return Results.Json(new
            {
                from = f, to = t, accountTimeZone = acct, combined = a.Role >= AccountRole.Admin && providerId is null,
                providers = outProviders, blocks, workingHours = hours,
                bookings = bookings.Select(b => new
                {
                    id = b.Id, number = b.Number, status = b.Status.ToString(), title = b.ServiceTitle, serviceId = b.ProductId, brandId = b.BrandId, providerId = b.ProviderId,
                    startAt = BJson.Utc(b.StartUtc), endAt = BJson.Utc(b.EndUtc), location = b.Location.ToString().ToLowerInvariant(),
                    buyerName = BookingAccess.SeesCustomerData(a, b) || b.Status == BookingStatus.Requested ? b.BuyerName : null, canAct = BookingAccess.CanAct(a, b)
                })
            });
        }).RequireActiveAccount();

        // ---- ICS feeds (private, revocable) ------------------------------------------------------------------------------------
        s.MapPost("/providers/{id:guid}/ics", async (Guid id, HttpContext ctx, AppDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id)) return Results.NotFound();
            var (m, p) = await MemberAsync(db, a, id);
            if (m is null) return Results.NotFound();
            if (!BookingAccess.CanManageProvider(a, id)) return AccessExtensions.Forbidden();
            if (p is null || !p.IsProvider) return Problem(409, "not_a_provider", "This team member isn't set up as a service provider yet.");
            var token = NewToken();
            p.IcsTokenHash = HashToken(token); p.IcsTokenCreatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("ics.provider_feed_created", a.UserId, null, $"membership={id}");
            return Results.Json(new { token, path = $"/api/calendar/feeds/{token}.ics", note = "Shown once. Generating a new link revokes the old one." });
        }).RequireActiveAccount();

        s.MapDelete("/providers/{id:guid}/ics", async (Guid id, HttpContext ctx, AppDbContext db, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            if (!CanSeeProvider(a, id)) return Results.NotFound();
            var (m, p) = await MemberAsync(db, a, id);
            if (m is null) return Results.NotFound();
            if (!BookingAccess.CanManageProvider(a, id)) return AccessExtensions.Forbidden();
            if (p is not null) { p.IcsTokenHash = null; p.IcsTokenCreatedAt = null; await db.SaveChangesAsync(); }
            await audit.WriteAsync("ics.provider_feed_revoked", a.UserId, null, $"membership={id}");
            return Results.NoContent();
        }).RequireActiveAccount();

        s.MapPost("/calendar/ics", async (HttpContext ctx, AppDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var row = await db.BookingSettings.FirstOrDefaultAsync(x => x.SellerId == a.SellerId) ?? db.BookingSettings.Add(new BookingSettings { SellerId = a.SellerId }).Entity;
            var token = NewToken();
            row.IcsTokenHash = HashToken(token); row.IcsTokenCreatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("ics.account_feed_created", a.UserId, null, $"seller={a.SellerId}");
            return Results.Json(new { token, path = $"/api/calendar/feeds/{token}.ics", note = "Shown once. Generating a new link revokes the old one." });
        }).RequirePermission(Permission.ProvidersManage);

        s.MapDelete("/calendar/ics", async (HttpContext ctx, AppDbContext db, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            await db.BookingSettings.Where(x => x.SellerId == a.SellerId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IcsTokenHash, (string?)null).SetProperty(x => x.IcsTokenCreatedAt, (DateTime?)null));
            await audit.WriteAsync("ics.account_feed_revoked", a.UserId, null, $"seller={a.SellerId}");
            return Results.NoContent();
        }).RequirePermission(Permission.ProvidersManage);

        s.MapGet("/calendar/ics", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var row = await db.BookingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == a.SellerId);
            return Results.Json(new { active = row?.IcsTokenHash != null, createdAt = row?.IcsTokenCreatedAt });
        }).RequirePermission(Permission.ProvidersManage);
    }

    private static object BlockDto(CalendarBlock b) => new
    {
        id = b.Id, providerId = b.MembershipId, kind = b.Kind == BlockKind.TimeOff ? "timeoff" : "busy", startAt = BJson.Utc(b.StartUtc), endAt = BJson.Utc(b.EndUtc), allDay = b.AllDay, note = b.Note
    };

    // ---- public ICS feed ---------------------------------------------------------------------------------------------------------
    public static void MapIcsFeed(this WebApplication app)
    {
        app.MapGet("/api/calendar/feeds/{token}", async (string token, AppDbContext db, TimeProvider time, IOptions<BookingOptions> opt, HttpContext ctx) =>
        {
            ctx.Response.Headers["Cache-Control"] = "private, no-store"; ctx.Response.Headers["X-Robots-Tag"] = "noindex"; ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) token = token[..^4];
            if (token.Length is < 20 or > 80 || !token.StartsWith("ics_")) return Results.NotFound();
            var hash = HashToken(token);
            var now = time.UtcNow();
            IQueryable<Booking> q;
            string name;
            var prov = await db.ProviderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.IcsTokenHash == hash && p.IsProvider);
            if (prov is not null) { q = db.Bookings.Where(b => b.ProviderId == prov.MembershipId); name = "Solvelt bookings"; }
            else
            {
                var acc = await db.BookingSettings.AsNoTracking().FirstOrDefaultAsync(p => p.IcsTokenHash == hash);
                if (acc is null) return Results.NotFound();
                q = db.Bookings.Where(b => b.SellerId == acc.SellerId); name = "Solvelt bookings (all providers)";
            }
            var list = await q.AsNoTracking().Where(b => b.EndUtc > now.AddDays(-30) && b.EndUtc < now.AddDays(400)
                && (b.Status == BookingStatus.Requested || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Rescheduled || b.Status == BookingStatus.InProgress))
                .OrderBy(b => b.StartUtc).Take(2000).ToListAsync();
            var sb = new StringBuilder();
            void L(string line) => sb.Append(Fold(line)).Append("\r\n");
            L("BEGIN:VCALENDAR"); L("VERSION:2.0"); L("PRODID:-//Solvelt//Seller bookings//EN"); L("CALSCALE:GREGORIAN"); L("METHOD:PUBLISH"); L("X-WR-CALNAME:" + Esc(name));
            foreach (var b in list)
            {
                L("BEGIN:VEVENT");
                L($"UID:{b.Id}@solvelt");
                L("DTSTAMP:" + Stamp(BJson.Utc(b.UpdatedAt))); L("DTSTART:" + Stamp(BJson.Utc(b.StartUtc))); L("DTEND:" + Stamp(BJson.Utc(b.EndUtc)));
                L("SEQUENCE:" + b.Version);
                L("SUMMARY:" + Esc(b.ServiceTitle + " (" + b.Number + ")" + (opt.Value.IcsIncludeBuyerName && b.PiiRedactedAt is null ? " - " + b.BuyerName : "")));
                L("LOCATION:" + Esc(b.Location switch { LocationKind.Online => "Online", LocationKind.Customer => "At the customer's location", _ => "At your location" }));
                L("STATUS:" + (b.Status == BookingStatus.Requested ? "TENTATIVE" : "CONFIRMED"));
                L("END:VEVENT");
            }
            L("END:VCALENDAR");
            return Results.Text(sb.ToString(), "text/calendar; charset=utf-8");
        }).RequireRateLimiting("internal").AllowAnonymous();
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>RFC 5545 line folding at 75 octets.</summary>
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var sb = new StringBuilder(); var cur = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (cur + n > 75) { sb.Append("\r\n "); cur = 1; }
            sb.Append(rune.ToString()); cur += n;
        }
        return sb.ToString();
    }
}

public record BookingSettingsWrite(string? TimeZone);
