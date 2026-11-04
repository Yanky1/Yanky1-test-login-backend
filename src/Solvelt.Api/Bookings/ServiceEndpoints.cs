using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Bookings;

/// <summary>Publish rules of a service, evaluated through the shared readiness checklist (key/section/severity like the product rules).</summary>
public static class ServiceRules
{
    public static void Check(ServiceDetails? d, Action<bool, int, string, string, bool, string> check)
    {
        if (d is null) { check(false, 10, "service", "pricing", true, "Add the service details."); return; }
        var priced = d.PricingModel switch
        {
            PricingModel.Hourly => d.PriceCents is > 0 && d.IncrementMinutes is 15 or 30 or 60,
            PricingModel.Fixed => d.PriceCents is > 0,
            _ => true
        };
        check(priced, 10, "price_model", "pricing", true, d.PricingModel == PricingModel.Hourly ? "Set an hourly rate and a billing increment." : "Set a price for the job (or choose \"quote required\").");
        check(d.DefaultDurationMinutes > 0, 6, "duration", "timing", true, "Set how long the service takes.");
        var area = BJson.Area(d);
        var hasArea = area.PostalCodes is { Count: > 0 } || (area.Radius is { Distance: > 0, Lat: not null, Lng: not null });
        var location = (d.Online && !string.IsNullOrWhiteSpace(d.OnlineInstructions)) || (d.AtCustomer && hasArea) || (d.AtSeller && !string.IsNullOrWhiteSpace(d.SellerAddress));
        check(location, 10, "location", "location", true, "Choose where the service happens and complete its details (meeting instructions, service area or address).");
        if (d.Online && string.IsNullOrWhiteSpace(d.OnlineInstructions)) check(false, 2, "online_instructions", "location", true, "Add the meeting link or instructions for online bookings.");
        if (d.AtCustomer && !hasArea) check(false, 2, "service_area", "location", true, "Add at least one postal code or a radius with a base location.");
        if (d.AtSeller && string.IsNullOrWhiteSpace(d.SellerAddress)) check(false, 2, "seller_address", "location", true, "Add the address customers come to.");
        check(d.ReadyProviders >= 1, 10, "providers", "providers", true, "Choose at least one team member who provides this service and give them working hours.");
        check(BJson.Intake(d).Count > 0, 2, "intake", "booking", false, "Ask buyers what you need to know before the appointment.");
        check(!string.IsNullOrWhiteSpace(d.Requirements), 2, "requirements", "booking", false, "Tell buyers how to prepare.");
    }
}

public static class ServiceView
{
    public static object Detail(ServiceDetails d) => new
    {
        pricingModel = d.PricingModel.ToString().ToLowerInvariant(), price = Money.FromCents(d.PriceCents), minMinutes = d.MinMinutes, maxMinutes = d.MaxMinutes,
        incrementMinutes = d.IncrementMinutes, defaultDurationMinutes = d.DefaultDurationMinutes, bufferBeforeMinutes = d.BufferBeforeMinutes, bufferAfterMinutes = d.BufferAfterMinutes,
        minNoticeHours = d.MinNoticeHours, maxAdvanceDays = d.MaxAdvanceDays, slotGranularityMinutes = d.SlotGranularityMinutes,
        online = d.Online, onlineInstructions = d.OnlineInstructions, atCustomer = d.AtCustomer, serviceArea = BJson.Area(d), travelFee = Money.FromCents(d.TravelFeeCents),
        travelBufferMinutes = d.TravelBufferMinutes, atSeller = d.AtSeller, sellerAddress = d.SellerAddress,
        bookingMode = d.BookingMode.ToString().ToLowerInvariant(), approvalHours = d.ApprovalHours, intake = BJson.Intake(d), requirements = d.Requirements,
        addOns = BJson.AddOns(d).Select(a => new { id = a.Id, name = a.Name, price = Money.FromCents(a.PriceCents), extraMinutes = a.ExtraMinutes }),
        cancelFreeHours = d.CancelFreeHours, cancelFeePercent = d.CancelFeePercent, maxReschedules = d.MaxReschedules, noShowFeePercent = d.NoShowFeePercent,
        assignmentRule = d.AssignmentRule.ToString().ToLowerInvariant(), readyProviders = d.ReadyProviders
    };
}

public record AddOnWrite(string? Id, string? Name, decimal? Price, int? ExtraMinutes);
public record ServiceWrite(
    int Version, string? PricingModel, decimal? Price, int? MinMinutes, int? MaxMinutes, int? IncrementMinutes, int? DefaultDurationMinutes,
    int? BufferBeforeMinutes, int? BufferAfterMinutes, int? MinNoticeHours, int? MaxAdvanceDays, int? SlotGranularityMinutes,
    bool Online, string? OnlineInstructions, bool AtCustomer, ServiceArea? ServiceArea, decimal? TravelFee, int? TravelBufferMinutes, bool AtSeller, string? SellerAddress,
    string? BookingMode, int? ApprovalHours, List<IntakeQuestion>? Intake, string? Requirements, List<AddOnWrite>? AddOns,
    int? CancelFreeHours, int? CancelFeePercent, int? MaxReschedules, int? NoShowFeePercent, string? AssignmentRule);
public record ProvidersWrite(List<Guid>? MembershipIds);

public static partial class ServiceEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,14}\*?$")] private static partial Regex PostalRe();
    private static readonly int[] Granularities = [5, 10, 15, 20, 30, 60];

    /// <summary>Validates and converts; returns field errors (empty = ok).</summary>
    public static Dictionary<string, string[]> Apply(ServiceDetails d, ServiceWrite w, DateTime now)
    {
        var e = new Dictionary<string, string[]>();
        void Err(string f, string m) => e[f] = [m];
        if (!Enum.TryParse<PricingModel>(w.PricingModel, true, out var pm) || !Enum.IsDefined(pm)) { Err("pricingModel", "Choose hourly, fixed or quote."); pm = d.PricingModel; }
        var price = Money.ToCents(w.Price);
        if (w.Price is { } pr && (pr < 0 || pr > 1_000_000m || decimal.Round(pr, 2) != pr)) Err("price", "Enter an amount between 0 and 1,000,000 with at most 2 decimals.");
        var inc = w.IncrementMinutes ?? 60;
        if (pm == PricingModel.Hourly && inc is not (15 or 30 or 60)) Err("incrementMinutes", "The billing increment must be 15, 30 or 60 minutes.");
        var dur = w.DefaultDurationMinutes ?? 60;
        if (dur is < 5 or > 1440) Err("defaultDurationMinutes", "Enter a duration between 5 minutes and 24 hours.");
        if (w.MinMinutes is < 5 or > 1440) Err("minMinutes", "Enter a minimum between 5 minutes and 24 hours.");
        if (w.MaxMinutes is < 5 or > 1440) Err("maxMinutes", "Enter a maximum between 5 minutes and 24 hours.");
        if (w.MinMinutes is { } mn && w.MaxMinutes is { } mx && mn > mx) Err("maxMinutes", "The maximum can't be below the minimum.");
        if (pm == PricingModel.Hourly)
        {
            if (inc is 15 or 30 or 60 && dur % inc != 0) Err("defaultDurationMinutes", $"Use a multiple of {inc} minutes.");
            if (w.MinMinutes is { } m1 && dur < m1) Err("defaultDurationMinutes", "The default duration is below the minimum.");
            if (w.MaxMinutes is { } m2 && dur > m2) Err("defaultDurationMinutes", "The default duration is above the maximum.");
        }
        int Range(string f, int? v, int def, int min, int max)
        {
            var x = v ?? def;
            if (x < min || x > max) Err(f, $"Enter a value between {min} and {max}.");
            return x;
        }
        var bb = Range("bufferBeforeMinutes", w.BufferBeforeMinutes, 0, 0, 480);
        var ba = Range("bufferAfterMinutes", w.BufferAfterMinutes, 0, 0, 480);
        var notice = Range("minNoticeHours", w.MinNoticeHours, 24, 0, 8760);
        var adv = Range("maxAdvanceDays", w.MaxAdvanceDays, 60, 1, 365);
        var gran = w.SlotGranularityMinutes ?? 30;
        if (!Granularities.Contains(gran)) Err("slotGranularityMinutes", "Choose 5, 10, 15, 20, 30 or 60 minutes.");
        var travelBuf = Range("travelBufferMinutes", w.TravelBufferMinutes, 0, 0, 240);
        var travelFee = Money.ToCents(w.TravelFee) ?? 0;
        if (w.TravelFee is { } tf && (tf < 0 || tf > 100_000m)) Err("travelFee", "Enter an amount between 0 and 100,000.");
        var approval = Range("approvalHours", w.ApprovalHours, 24, 1, 168);
        var cfh = Range("cancelFreeHours", w.CancelFreeHours, 24, 0, 720);
        var cfp = Range("cancelFeePercent", w.CancelFeePercent, 0, 0, 100);
        var mr = Range("maxReschedules", w.MaxReschedules, 2, 0, 10);
        var ns = Range("noShowFeePercent", w.NoShowFeePercent, 100, 0, 100);
        if (!Enum.TryParse<BookingMode>(w.BookingMode ?? "instant", true, out var bm) || !Enum.IsDefined(bm)) { Err("bookingMode", "Choose instant or request."); bm = d.BookingMode; }
        if (!Enum.TryParse<AssignmentRule>(w.AssignmentRule ?? "buyerchoice", true, out var ar) || !Enum.IsDefined(ar)) { Err("assignmentRule", "Choose buyerChoice, leastBusy or roundRobin."); ar = d.AssignmentRule; }
        if (w.OnlineInstructions is { Length: > 2000 }) Err("onlineInstructions", "At most 2000 characters.");
        if (w.SellerAddress is { Length: > 500 }) Err("sellerAddress", "At most 500 characters.");
        if (w.Requirements is { Length: > 2000 }) Err("requirements", "At most 2000 characters.");

        // service area
        var area = w.ServiceArea ?? new ServiceArea(null, null);
        var codes = (area.PostalCodes ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        if (codes.Count > 500) Err("serviceArea", "At most 500 postal codes.");
        else if (codes.Any(c => !PostalRe().IsMatch(c))) Err("serviceArea", "Postal codes can contain letters, digits, spaces and hyphens (end with * for a prefix, e.g. 112*).");
        AreaRadius? radius = null;
        if (area.Radius is { } r && (r.Distance is not null || r.Lat is not null || r.Lng is not null || !string.IsNullOrWhiteSpace(r.BaseAddress)))
        {
            if (r.Distance is not > 0 or > 500) Err("serviceArea", "The radius must be between 0 and 500.");
            else if (r.Lat is not (>= -90 and <= 90) || r.Lng is not (>= -180 and <= 180)) Err("serviceArea", "Set the base location (latitude and longitude).");
            else if (r.Unit is not ("km" or "mi")) Err("serviceArea", "Choose km or mi.");
            else radius = r with { BaseAddress = r.BaseAddress?.Trim() is { Length: > 300 } s ? s[..300] : r.BaseAddress?.Trim() };
        }
        // intake questions
        var intake = new List<IntakeQuestion>();
        if ((w.Intake?.Count ?? 0) > 20) Err("intake", "At most 20 questions.");
        var used = new HashSet<string>();
        foreach (var (q, i) in (w.Intake ?? []).Select((q, i) => (q, i)))
        {
            var label = q.Label?.Trim() ?? "";
            if (label.Length is 0 or > 200) { Err("intake", $"Question {i + 1}: enter the question (up to 200 characters)."); break; }
            var type = q.Type?.ToLowerInvariant();
            if (type is not ("text" or "choice" or "yesno")) { Err("intake", $"Question {i + 1}: choose text, choice or yes/no."); break; }
            var opts = (q.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
            if (type == "choice" && (opts.Count < 2 || opts.Count > 20 || opts.Any(o => o.Length > 100))) { Err("intake", $"Question {i + 1}: give 2-20 options (up to 100 characters each)."); break; }
            var id = string.IsNullOrWhiteSpace(q.Id) || !Regex.IsMatch(q.Id, "^[a-z0-9_-]{1,40}$") ? "q" + (i + 1) : q.Id;
            while (!used.Add(id)) id += "x";
            intake.Add(new IntakeQuestion(id, label, type, type == "choice" ? opts : null, q.Required));
        }
        var addOns = new List<AddOn>();
        if ((w.AddOns?.Count ?? 0) > 20) Err("addOns", "At most 20 add-ons.");
        foreach (var (a, i) in (w.AddOns ?? []).Select((a, i) => (a, i)))
        {
            var name = a.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) { Err("addOns", $"Add-on {i + 1}: enter a name (up to 100 characters)."); break; }
            if (a.Price is < 0 or > 100_000m || (a.ExtraMinutes ?? 0) is < 0 or > 480) { Err("addOns", $"Add-on {i + 1}: the price must be 0-100,000 and extra time 0-480 minutes."); break; }
            addOns.Add(new AddOn(string.IsNullOrWhiteSpace(a.Id) || !Regex.IsMatch(a.Id, "^[a-z0-9_-]{1,40}$") ? "a" + (i + 1) : a.Id, name, Money.ToCents(a.Price) ?? 0, a.ExtraMinutes ?? 0));
        }
        if (addOns.Select(a => a.Id).Distinct().Count() != addOns.Count) Err("addOns", "Add-on ids must be unique.");
        if (e.Count > 0) return e;

        d.PricingModel = pm; d.PriceCents = pm == PricingModel.Quote ? null : price; d.MinMinutes = w.MinMinutes; d.MaxMinutes = w.MaxMinutes;
        d.IncrementMinutes = pm == PricingModel.Hourly ? inc : 60; d.DefaultDurationMinutes = dur;
        d.BufferBeforeMinutes = bb; d.BufferAfterMinutes = ba; d.MinNoticeHours = notice; d.MaxAdvanceDays = adv; d.SlotGranularityMinutes = gran;
        d.Online = w.Online; d.OnlineInstructions = string.IsNullOrWhiteSpace(w.OnlineInstructions) ? null : w.OnlineInstructions.Trim();
        d.AtCustomer = w.AtCustomer; d.ServiceAreaJson = BJson.Ser(new ServiceArea(codes, radius)); d.TravelFeeCents = travelFee; d.TravelBufferMinutes = travelBuf;
        d.AtSeller = w.AtSeller; d.SellerAddress = string.IsNullOrWhiteSpace(w.SellerAddress) ? null : w.SellerAddress.Trim();
        d.BookingMode = bm; d.ApprovalHours = approval; d.IntakeJson = BJson.Ser(intake); d.Requirements = string.IsNullOrWhiteSpace(w.Requirements) ? null : w.Requirements.Trim();
        d.AddOnsJson = BJson.Ser(addOns); d.CancelFreeHours = cfh; d.CancelFeePercent = cfp; d.MaxReschedules = mr; d.NoShowFeePercent = ns; d.AssignmentRule = ar;
        d.UpdatedAt = now;
        return e;
    }

    private static async Task<Product?> LoadService(AppDbContext db, AccountAccess a, Guid id, bool write)
    {
        var p = await db.LoadAsync(a, id, write);
        return p is { Type: ListingType.Service } ? p : null;
    }

    public static void MapServiceEndpoints(this RouteGroupBuilder g)
    {
        var s = g.MapGroup("/seller/services").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        s.MapPut("/{id:guid}/details", async (Guid id, ServiceWrite w, HttpContext ctx, AppDbContext db, CategoryService cats, CalendarService cal, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var p = await LoadService(db, a, id, true);
            if (p is null) return Results.NotFound();
            if (w.Version != p.Version) return ProductEndpoints.Conflict();
            var d = p.Service ??= new ServiceDetails { ProductId = p.Id, SellerId = p.SellerId };
            var errors = Apply(d, w, time.UtcNow());
            if (errors.Count > 0) { db.ChangeTracker.Clear(); return Results.ValidationProblem(errors); }
            var r = await ProductRules.RefreshAsync(db, cats, p);
            if (p.Status == ProductStatus.Active && !r.CanPublish)
            {
                db.ChangeTracker.Clear();
                return ProductEndpoints.Problem(422, "not_publishable", "Active services must stay complete. Fix the items below, or set the listing to inactive first.", r.Issues);
            }
            p.Version++; p.UpdatedAt = time.UtcNow();
            if (!await ProductEndpoints.TrySaveAsync(db)) return ProductEndpoints.Conflict();
            await cal.RefreshReadyProvidersAsync(p.SellerId, [p.Id]);
            db.ChangeTracker.Clear();
            return await ProductEndpoints.OkDetail(db, cats, (await db.LoadAsync(a, id, false))!, time);
        }).RequirePermission(Permission.BrandWorkWrite);

        s.MapGet("/{id:guid}/providers", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            if (await LoadService(db, a, id, false) is null) return Results.NotFound();
            var linked = await db.ServiceProviders.AsNoTracking().Where(l => l.ProductId == id).Select(l => l.MembershipId).ToListAsync();
            var members = await (from m in db.Memberships.AsNoTracking()
                                 join pp in db.ProviderProfiles.AsNoTracking() on m.Id equals pp.MembershipId
                                 join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                                 where m.SellerId == a.SellerId && m.Status == MembershipStatus.Active && pp.IsProvider
                                 select new { m.Id, u.Email, m.Role, pp.TimeZone }).ToListAsync();
            return Results.Json(new { providers = members.Select(m => new { membershipId = m.Id, email = m.Email, role = m.Role.ToString(), timeZone = m.TimeZone, eligible = linked.Contains(m.Id) }) });
        }).RequirePermission(Permission.BrandWorkRead);

        // Seller-side view of the same slot engine the marketplace uses: the times a buyer could book right now (for proposing / reschedule / quote times).
        // Same brand scoping as the rest of the listing; the booking service enforces the window and the per-call day limit.
        s.MapGet("/{id:guid}/slots", async (Guid id, HttpContext ctx, AppDbContext db, BookingService svc, DateTime? from, DateTime? to, Guid? providerId, string? location, string? postalCode, int? minutes) =>
        {
            var a = ctx.GetAccess()!;
            if (await LoadService(db, a, id, false) is null) return Results.NotFound();
            if (from is null || to is null) return Validation.Invalid("from", "from and to are required.");
            var r = await svc.AvailabilityAsync(id, from.Value, to.Value, providerId, location, postalCode, null, null, minutes, null);
            return r.Ok ? Results.Json(r.Data ?? new { ok = true }, BJson.Opts) : ProviderEndpoints.Problem(r.Status, r.Code, r.Message);
        }).RequirePermission(Permission.BrandWorkRead);

        s.MapPut("/{id:guid}/providers", async (Guid id, ProvidersWrite w, HttpContext ctx, AppDbContext db, CategoryService cats, CalendarService cal, IAuditLog audit, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var p = await LoadService(db, a, id, true);
            if (p is null) return Results.NotFound();
            var ids = (w.MembershipIds ?? []).Distinct().ToList();
            if (ids.Count > 100) return Validation.Invalid("membershipIds", "Too many providers.");
            var valid = await (from m in db.Memberships join pp in db.ProviderProfiles on m.Id equals pp.MembershipId
                               where m.SellerId == a.SellerId && m.Status == MembershipStatus.Active && pp.IsProvider && ids.Contains(m.Id) select m.Id).ToListAsync();
            if (valid.Count != ids.Count) return Validation.Invalid("membershipIds", "Choose active team members that are marked as service providers.");
            var existing = await db.ServiceProviders.Where(l => l.ProductId == id).ToListAsync();
            db.ServiceProviders.RemoveRange(existing.Where(l => !ids.Contains(l.MembershipId)));
            foreach (var m in ids.Where(m => existing.All(l => l.MembershipId != m))) db.ServiceProviders.Add(new ServiceProviderLink { ProductId = id, MembershipId = m });
            await db.SaveChangesAsync();
            await cal.RefreshReadyProvidersAsync(a.SellerId, [id]);
            db.ChangeTracker.Clear();
            var fresh = (await db.LoadAsync(a, id, false))!;
            if (fresh.Status == ProductStatus.Active && !fresh.IsReady)
            {
                // a live service must keep at least one provider with working hours: undo
                db.ServiceProviders.RemoveRange(await db.ServiceProviders.Where(l => l.ProductId == id).ToListAsync());
                foreach (var m in existing) db.ServiceProviders.Add(new ServiceProviderLink { ProductId = id, MembershipId = m.MembershipId });
                await db.SaveChangesAsync();
                await cal.RefreshReadyProvidersAsync(a.SellerId, [id]);
                return ProductEndpoints.Problem(422, "not_publishable", "An active service needs at least one provider with working hours.");
            }
            await audit.WriteAsync("service.providers_changed", a.UserId, null, $"service={id};seller={a.SellerId};count={ids.Count}");
            return Results.Ok(new { providers = ids });
        }).RequirePermission(Permission.BrandWorkWrite);
    }
}
