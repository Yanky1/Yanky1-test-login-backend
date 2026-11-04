using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;

namespace Solvelt.Api.Bookings;

public record BRes(bool Ok, int Status = 200, string Code = "ok", string Message = "", object? Data = null)
{
    public static BRes Success(object? data = null) => new(true, 200, "ok", "", data);
    public static BRes Fail(int status, string code, string message) => new(false, status, code, message);
    public static BRes Conflict(string message, string code = "invalid_state") => new(false, 409, code, message);
    public static BRes Invalid(string message, string code = "invalid") => new(false, 422, code, message);
}

public record AreaRadius(string? BaseAddress, double? Lat, double? Lng, double? Distance, string? Unit);
public record ServiceArea(List<string>? PostalCodes, AreaRadius? Radius);
public record IntakeQuestion(string Id, string Label, string Type, List<string>? Options, bool Required);
public record AddOn(string Id, string Name, long PriceCents, int ExtraMinutes);
public record IntakeAnswer(string Id, string? Answer);
/// <summary>The policy of a service as it was when a booking was made (stored as <c>Booking.PolicyJson</c>). <c>ApprovalHours</c> is null for instant-booking services.</summary>
public record BookingPolicy(int CancelFreeHours, int CancelFeePercent, int MaxReschedules, int NoShowFeePercent, int IncrementMinutes, int? ApprovalHours);

/// <summary>The policy object marketplaces read (C11): <c>{cancelFreeHours, cancelFeePercent, noShowFeePercent, maxReschedules, approvalHours}</c>.</summary>
public static class PolicyView
{
    /// <summary>The SNAPSHOT of a booking (never the service's current policy). A value the snapshot does not hold is null.</summary>
    public static object OfSnapshot(string? json)
    {
        int? Get(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
        JsonElement el = default;
        try { if (!string.IsNullOrWhiteSpace(json)) { using var doc = JsonDocument.Parse(json); el = doc.RootElement.Clone(); } } catch (JsonException) { }
        return new { cancelFreeHours = Get(el, "cancelFreeHours"), cancelFeePercent = Get(el, "cancelFeePercent"), noShowFeePercent = Get(el, "noShowFeePercent"), maxReschedules = Get(el, "maxReschedules"), approvalHours = Get(el, "approvalHours") };
    }

    /// <summary>The CURRENT policy of a service (catalog document, options endpoint). <c>approvalHours</c> is null for instant-booking services.</summary>
    public static object Of(ServiceDetails d) => new
    {
        cancelFreeHours = (int?)d.CancelFreeHours, cancelFeePercent = (int?)d.CancelFeePercent, noShowFeePercent = (int?)d.NoShowFeePercent, maxReschedules = (int?)d.MaxReschedules,
        approvalHours = d.BookingMode == BookingMode.Instant ? null : (int?)d.ApprovalHours
    };
}

public static class BJson
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    public static string Ser(object o) => JsonSerializer.Serialize(o, Opts);
    public static T? From<T>(string? json) { try { return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Opts); } catch (JsonException) { return default; } }
    public static ServiceArea Area(ServiceDetails d) => From<ServiceArea>(d.ServiceAreaJson) ?? new(null, null);
    public static List<IntakeQuestion> Intake(ServiceDetails d) => From<List<IntakeQuestion>>(d.IntakeJson) ?? [];
    public static List<AddOn> AddOns(ServiceDetails d) => From<List<AddOn>>(d.AddOnsJson) ?? [];
    public static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
}

/// <summary>The one place that decides which booking status changes are legal. Everything else answers 409.</summary>
public static class BookingStateMachine
{
    public enum Act { ConfirmHold, Approve, Decline, Propose, AcceptProposal, Reschedule, CancelByBuyer, CancelBySeller, Start, Complete, NoShow, ExpireHold, AutoDecline, Reassign }

    private static readonly Dictionary<Act, (BookingStatus[] From, BookingStatus To)> T = new()
    {
        [Act.ConfirmHold] = ([BookingStatus.Hold], BookingStatus.Confirmed), // or Requested for request-to-book (decided by the caller)
        [Act.Approve] = ([BookingStatus.Requested], BookingStatus.Confirmed),
        [Act.Decline] = ([BookingStatus.Requested], BookingStatus.Declined),
        [Act.Propose] = ([BookingStatus.Requested], BookingStatus.Requested),
        [Act.AcceptProposal] = ([BookingStatus.Requested], BookingStatus.Confirmed),
        [Act.Reschedule] = ([BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.Rescheduled),
        [Act.CancelByBuyer] = ([BookingStatus.Hold, BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.CancelledByBuyer),
        [Act.CancelBySeller] = ([BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.CancelledBySeller),
        [Act.Start] = ([BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.InProgress),
        [Act.Complete] = ([BookingStatus.InProgress], BookingStatus.Completed),
        [Act.NoShow] = ([BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.NoShow),
        [Act.ExpireHold] = ([BookingStatus.Hold], BookingStatus.Expired),
        [Act.AutoDecline] = ([BookingStatus.Requested], BookingStatus.Declined),
        [Act.Reassign] = ([BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled], BookingStatus.Requested), // To unused: status stays
    };

    public static bool Can(BookingStatus from, Act a) => T[a].From.Contains(from);
    public static BookingStatus Target(Act a) => T[a].To;
    public static IEnumerable<Act> Allowed(BookingStatus from) => T.Where(kv => kv.Value.From.Contains(from)).Select(kv => kv.Key);
    public static bool IsBlocking(BookingStatus s) => s is BookingStatus.Requested or BookingStatus.Confirmed or BookingStatus.Rescheduled or BookingStatus.InProgress;
    public static bool IsActiveSchedule(BookingStatus s) => IsBlocking(s) || s == BookingStatus.Hold;
    public static bool CustomerDataVisibleStatus(BookingStatus s) => s is BookingStatus.Confirmed or BookingStatus.Rescheduled or BookingStatus.InProgress or BookingStatus.Completed
        or BookingStatus.NoShow or BookingStatus.CancelledByBuyer or BookingStatus.CancelledBySeller;

    public static IResult Illegal(BookingStatus from, Act a) => Results.Json(new { type = "about:blank", title = $"A {from} booking can't do that ({a}).", status = 409, code = "invalid_state" }, statusCode: 409);
}

/// <summary>Loads a provider's calendar into the pure <see cref="ProviderCalendar"/> the slot engine works on.</summary>
public class CalendarService(AppDbContext db, IOptions<BookingOptions> opt)
{
    public static readonly BookingStatus[] BlockingStatuses = [BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled, BookingStatus.InProgress];

    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    public async Task<string> AccountZoneIdAsync(Guid sellerId) =>
        await db.BookingSettings.AsNoTracking().Where(s => s.SellerId == sellerId).Select(s => s.TimeZone).FirstOrDefaultAsync() ?? opt.Value.DefaultTimeZone;

    public async Task<TimeZoneInfo> ZoneAsync(Guid sellerId, Guid membershipId)
    {
        var own = await db.ProviderProfiles.AsNoTracking().Where(p => p.MembershipId == membershipId).Select(p => p.TimeZone).FirstOrDefaultAsync();
        return FindZone(own) ?? FindZone(await AccountZoneIdAsync(sellerId)) ?? TimeZoneInfo.Utc;
    }

    public async Task<ProviderCalendar> LoadAsync(Guid sellerId, Guid membershipId, DateTime fromUtc, DateTime toUtc, DateTime nowUtc, Guid? excludeBookingId = null)
    {
        var zone = await ZoneAsync(sellerId, membershipId);
        var hours = await db.WorkingHours.AsNoTracking().Where(h => h.MembershipId == membershipId).ToListAsync();
        var overrides = await db.DateOverrides.AsNoTracking().Where(o => o.MembershipId == membershipId).ToListAsync();
        var blocks = await db.CalendarBlocks.AsNoTracking().Where(b => b.MembershipId == membershipId && b.EndUtc > fromUtc && b.StartUtc < toUtc).ToListAsync();
        var bookings = await db.Bookings.AsNoTracking().Where(b => b.ProviderId == membershipId && b.BlockEndUtc > fromUtc && b.BlockStartUtc < toUtc && b.Id != excludeBookingId
            && (BlockingStatuses.Contains(b.Status) || (b.Status == BookingStatus.Hold && b.HoldExpiresAt > nowUtc)))
            .Select(b => new { b.BlockStartUtc, b.BlockEndUtc }).ToListAsync();
        var cal = new ProviderCalendar
        {
            Zone = zone,
            Weekly = hours.GroupBy(h => (DayOfWeek)h.Day).ToDictionary(g => g.Key, g => g.Select(h => (h.StartMinute, h.EndMinute)).ToList()),
            Overrides = overrides.ToDictionary(o => o.Date, o => (BJson.From<List<int[]>>(o.IntervalsJson) ?? []).Where(a => a.Length == 2).Select(a => (a[0], a[1])).ToList()),
            Off = blocks.Select(b => new Span(BJson.Utc(b.StartUtc), BJson.Utc(b.EndUtc))).ToList(),
            Busy = bookings.Select(b => new Span(BJson.Utc(b.BlockStartUtc), BJson.Utc(b.BlockEndUtc))).ToList(),
        };
        return cal;
    }

    /// <summary>Recomputes how many eligible providers of each service have working hours (keeps the synchronous readiness check cheap).</summary>
    public async Task RefreshReadyProvidersAsync(Guid sellerId, IEnumerable<Guid>? productIds = null)
    {
        var q = db.ServiceDetails.Where(d => d.SellerId == sellerId);
        if (productIds is not null) { var ids = productIds.ToList(); q = q.Where(d => ids.Contains(d.ProductId)); }
        foreach (var d in await q.ToListAsync())
        {
            d.ReadyProviders = await EligibleQuery(d.ProductId).CountAsync(m => db.WorkingHours.Any(h => h.MembershipId == m.Id) || db.DateOverrides.Any(o => o.MembershipId == m.Id));
            var prod = await db.Products.Include(p => p.Service).FirstAsync(p => p.Id == d.ProductId);
            var r = ProductRules.Evaluate(prod, await new CategoryService(db).AttributesAsync(prod.CategoryId));
            prod.QualityScore = r.Score; prod.IsReady = r.CanPublish;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Active members flagged as providers that are linked to the service.</summary>
    public IQueryable<AccountMembership> EligibleQuery(Guid productId) =>
        from l in db.ServiceProviders
        join m in db.Memberships on l.MembershipId equals m.Id
        join p in db.ProviderProfiles on m.Id equals p.MembershipId
        where l.ProductId == productId && m.Status == MembershipStatus.Active && p.IsProvider
        select m;
}


/// <summary>Seams for tests only; every member is null in production.</summary>
public sealed class BookingTestHooks
{
    /// <summary>Runs right after a confirm took the provider lock, before the decision about the hold is made; gets the request's own context (so a test can change the row on the same connection and transaction).</summary>
    public Func<Solvelt.Api.Data.AppDbContext, Task>? AfterConfirmLock { get; set; }
}
