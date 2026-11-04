using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Shipping;

public record AddressInput(string? Label, string? Name, string? Company, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, string? Phone, string? Email, Guid? BrandId, bool? IsDefault);
public record PackageBody(string? Name, string? Kind, int? LengthMm, int? WidthMm, int? HeightMm, int? EmptyWeightGrams, bool? IsDefault);
public record SpeedRate(string? Speed, bool? Enabled, string? Method, long? FlatCents, long? BaseCents, long? PerKgCents);
public record Zone(string? Name, string? Kind, List<string>? Countries, List<SpeedRate>? Speeds);
public record ProfileBody(string? Name, Guid? BrandId, string? Currency, long? FreeShippingThresholdCents, List<Zone>? Zones, bool? IsDefault);
public record SettingsBody(Guid? BrandId, int? HandlingDays, string? LabelFormat, Guid? ReturnAddressId, bool ClearReturnAddress = false, bool ClearHandling = false);
public record ProductPackageBody(Guid? PackageId);

public static class ShippingSettingsEndpoints
{
    private static IResult Bad(string field, string msg) => Validation.Invalid(field, msg);

    private static bool Scope(AccountAccess a, Guid? brandId) => brandId is null || a.CanSeeBrand(brandId.Value);

    // ---- views -----------------------------------------------------------------------------------------------------------------
    public static object AddressView(ShipFromAddress x) => new
    {
        id = x.Id, brandId = x.BrandId, label = x.Label, name = x.Name, company = x.Company, line1 = x.Line1, line2 = x.Line2, city = x.City, region = x.Region, postalCode = x.PostalCode,
        country = x.Country, phone = x.Phone, email = x.Email, isDefault = x.IsDefault, validation = x.Validation, validationMessage = x.ValidationMessage, validatedAt = x.ValidatedAt
    };

    public static object PackageView(PackagePreset p) => new { id = p.Id, name = p.Name, kind = p.Kind, lengthMm = p.LengthMm, widthMm = p.WidthMm, heightMm = p.HeightMm, emptyWeightGrams = p.EmptyWeightGrams, isDefault = p.IsDefault };

    public static object ProfileView(ShippingProfile p) => new
    {
        id = p.Id, brandId = p.BrandId, name = p.Name, isDefault = p.IsDefault, currency = p.Currency, freeShippingThresholdCents = p.FreeShippingThresholdCents,
        zones = JsonSerializer.Deserialize<JsonElement>(p.ZonesJson)
    };

    /// <summary>Validates zones and returns the cleaned list (or an error message).</summary>
    public static (string? Error, List<Zone>? Zones) CleanZones(List<Zone>? zones)
    {
        if (zones is null || zones.Count == 0) return ("Add at least one zone.", null);
        if (zones.Count > 10) return ("At most 10 zones.", null);
        var result = new List<Zone>();
        foreach (var z in zones)
        {
            var name = z.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60) return ("Every zone needs a name (up to 60 characters).", null);
            var kind = z.Kind?.ToLowerInvariant();
            if (kind is not ("domestic" or "international")) return ($"Zone \"{name}\": choose domestic or international.", null);
            var countries = (z.Countries ?? []).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
            if (countries.Count == 0 || countries.Count > 250 || countries.Any(c => c != "*" && !(c.Length == 2 && c.All(char.IsAsciiLetterUpper)))) return ($"Zone \"{name}\": list country codes like US, CA (or * for the rest of the world).", null);
            if (kind == "domestic" && countries.Contains("*")) return ($"Zone \"{name}\": a domestic zone needs real country codes.", null);
            var speeds = new List<SpeedRate>();
            foreach (var s in z.Speeds ?? [])
            {
                var sp = s.Speed?.ToLowerInvariant();
                if (sp is null || !ShippingSupport.Speeds.Contains(sp)) return ($"Zone \"{name}\": speed must be standard, expedited or overnight.", null);
                if (speeds.Any(x => x.Speed == sp)) return ($"Zone \"{name}\": {sp} appears twice.", null);
                var method = s.Method?.ToLowerInvariant();
                static bool Ok(long? v) => v is >= 0 and <= 10_000_000;
                if (method == "flat") { if (!Ok(s.FlatCents)) return ($"Zone \"{name}\": enter a flat rate in cents (0 to 10,000,000).", null); speeds.Add(new SpeedRate(sp, s.Enabled ?? true, "flat", s.FlatCents, null, null)); }
                else if (method == "weight") { if (!Ok(s.BaseCents) || !Ok(s.PerKgCents)) return ($"Zone \"{name}\": enter a base rate and a rate per kg in cents.", null); speeds.Add(new SpeedRate(sp, s.Enabled ?? true, "weight", null, s.BaseCents, s.PerKgCents)); }
                else return ($"Zone \"{name}\": choose a flat or weight-based rate.", null);
            }
            if (speeds.Count == 0 || speeds.Count > 3) return ($"Zone \"{name}\": add 1 to 3 speeds.", null);
            result.Add(new Zone(name, kind, countries, speeds));
        }
        return (null, result);
    }

    private static string? CleanAddress(AddressInput b, out AddressInput c)
    {
        string? T(string? s, int max) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s; }
        c = new AddressInput(T(b.Label, 80), T(b.Name, 120), T(b.Company, 120), T(b.Line1, 200), T(b.Line2, 200), T(b.City, 100), T(b.Region, 100), T(b.PostalCode, 20),
            b.Country?.Trim().ToUpperInvariant(), T(b.Phone, 40), T(b.Email, 200), b.BrandId, b.IsDefault);
        if (c.Name is null) return "name";
        if (c.Line1 is null) return "line1";
        if (c.City is null) return "city";
        if (c.PostalCode is null) return "postalCode";
        if (c.Country is not { Length: 2 } || !c.Country.All(char.IsAsciiLetterUpper)) return "country";
        if (c.Country == "US" && c.Region is null) return "region";
        return null;
    }

    private static async Task<IResult> Full(AppDbContext db, AccountAccess a, IOptions<ShippingOptions> opt, LabelService labels, IShippingProvider provider)
    {
        var brandIds = await db.BrandsFor(a).Select(b => b.Id).ToListAsync();
        bool Vis(Guid? b) => b is null || brandIds.Contains(b.Value);
        var addresses = (await db.ShipFromAddresses.AsNoTracking().Where(x => x.SellerId == a.SellerId && !x.Archived).OrderBy(x => x.CreatedAt).ToListAsync()).Where(x => Vis(x.BrandId)).ToList();
        var packages = await db.PackagePresets.AsNoTracking().Where(x => x.SellerId == a.SellerId && !x.Archived).OrderBy(x => x.CreatedAt).ToListAsync();
        var profiles = (await db.ShippingProfiles.AsNoTracking().Where(x => x.SellerId == a.SellerId && !x.Archived).OrderBy(x => x.CreatedAt).ToListAsync()).Where(x => Vis(x.BrandId)).ToList();
        var settings = (await db.SellerShippingSettings.AsNoTracking().Where(x => x.SellerId == a.SellerId).ToListAsync()).Where(x => Vis(x.BrandId)).ToList();
        return Results.Json(new
        {
            addresses = addresses.Select(AddressView), packages = packages.Select(PackageView), profiles = profiles.Select(ProfileView),
            settings = settings.Select(s => new { brandId = s.BrandId, handlingDays = s.HandlingDays, labelFormat = s.LabelFormat, returnAddressId = s.ReturnAddressId }),
            labelFormats = LabelFormats.All, defaultLabelFormat = opt.Value.LabelFormat, speeds = ShippingSupport.Speeds, provider = provider.Name,
            spend = new { limitCents = opt.Value.SpendLimitCentsPerMonth, spentCents = await labels.SpentThisMonthAsync(a.SellerId) },
            canManage = a.Has(Permission.ShippingManage)
        }, OrderJson.Opts);
    }

    public static void MapShippingSettings(this RouteGroupBuilder g)
    {
        var grp = g.MapGroup("/seller/shipping").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("shipping").RequirePermission(Permission.BrandWorkRead);
        var manage = (RouteHandlerBuilder b) => b.RequirePermission(Permission.ShippingManage);

        grp.MapGet("/settings", async (HttpContext ctx, AppDbContext db, IOptions<ShippingOptions> opt, LabelService labels, IShippingProvider provider) => await Full(db, ctx.GetAccess()!, opt, labels, provider));

        // ---- ship-from addresses -------------------------------------------------------------------------------------------------
        manage(grp.MapPost("/addresses", async (HttpContext ctx, AppDbContext db, IShippingProvider provider, TimeProvider time, IAuditLog audit, AddressInput body) =>
        {
            var a = ctx.GetAccess()!;
            if (CleanAddress(body, out var c) is { } f) return Bad(f, "This field is required.");
            if (!Scope(a, c.BrandId) || (c.BrandId is { } bid && !await db.BrandsFor(a).AnyAsync(x => x.Id == bid))) return Results.NotFound();
            var row = new ShipFromAddress { SellerId = a.SellerId, BrandId = c.BrandId, ScopeKey = c.BrandId ?? Guid.Empty, CreatedAt = time.UtcNow(), UpdatedAt = time.UtcNow() };
            var (err, _) = await Apply(row, c, provider, time);
            if (err is not null) return err;
            var first = !await db.ShipFromAddresses.AnyAsync(x => x.SellerId == a.SellerId && x.ScopeKey == row.ScopeKey && x.IsDefault && !x.Archived);
            db.ShipFromAddresses.Add(row);
            await db.SaveChangesAsync();
            if (first || c.IsDefault == true) await MakeDefault(db, row);
            await audit.WriteAsync("shipping.address_added", a.UserId, null, $"scope={(row.BrandId is null ? "account" : "brand")} country={row.Country}");
            return Results.Json(AddressView(row), OrderJson.Opts, statusCode: 201);
        }));

        manage(grp.MapPut("/addresses/{id:guid}", async (HttpContext ctx, AppDbContext db, IShippingProvider provider, TimeProvider time, IAuditLog audit, Guid id, AddressInput body) =>
        {
            var a = ctx.GetAccess()!;
            var row = await db.ShipFromAddresses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (row is null || !Scope(a, row.BrandId)) return Results.NotFound();
            if (CleanAddress(body with { BrandId = row.BrandId }, out var c) is { } f) return Bad(f, "This field is required.");
            var (err, _) = await Apply(row, c, provider, time);
            if (err is not null) return err;
            row.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            if (c.IsDefault == true) await MakeDefault(db, row);
            await audit.WriteAsync("shipping.address_edited", a.UserId, null, $"address={row.Id}");
            return Results.Json(AddressView(row), OrderJson.Opts);
        }));

        manage(grp.MapPost("/addresses/{id:guid}/default", async (HttpContext ctx, AppDbContext db, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var row = await db.ShipFromAddresses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (row is null || !Scope(a, row.BrandId)) return Results.NotFound();
            await MakeDefault(db, row);
            return Results.Json(AddressView(row), OrderJson.Opts);
        }));

        manage(grp.MapDelete("/addresses/{id:guid}", async (HttpContext ctx, AppDbContext db, IAuditLog audit, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var row = await db.ShipFromAddresses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (row is null || !Scope(a, row.BrandId)) return Results.NotFound();
            row.Archived = true; row.IsDefault = false; row.DefaultKey = null;
            await db.SellerShippingSettings.Where(s => s.SellerId == a.SellerId && s.ReturnAddressId == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ReturnAddressId, (Guid?)null));
            await db.SaveChangesAsync();
            await audit.WriteAsync("shipping.address_archived", a.UserId, null, $"address={id}");
            return Results.NoContent();
        }));

        // ---- package presets ---------------------------------------------------------------------------------------------------------
        manage(grp.MapPost("/packages", async (HttpContext ctx, AppDbContext db, TimeProvider time, PackageBody b) =>
        {
            var a = ctx.GetAccess()!;
            var p = new PackagePreset { SellerId = a.SellerId, CreatedAt = time.UtcNow() };
            if (CleanPackage(p, b) is { } e) return e;
            var first = !await db.PackagePresets.AnyAsync(x => x.SellerId == a.SellerId && x.IsDefault && !x.Archived);
            db.PackagePresets.Add(p);
            if (first || b.IsDefault == true) { await db.PackagePresets.Where(x => x.SellerId == a.SellerId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false)); p.IsDefault = true; }
            await db.SaveChangesAsync();
            return Results.Json(PackageView(p), OrderJson.Opts, statusCode: 201);
        }));

        manage(grp.MapPut("/packages/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id, PackageBody b) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.PackagePresets.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (p is null) return Results.NotFound();
            if (CleanPackage(p, b) is { } e) return e;
            if (b.IsDefault == true) { await db.PackagePresets.Where(x => x.SellerId == a.SellerId && x.Id != id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false)); p.IsDefault = true; }
            await db.SaveChangesAsync();
            return Results.Json(PackageView(p), OrderJson.Opts);
        }));

        manage(grp.MapDelete("/packages/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.PackagePresets.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (p is null) return Results.NotFound();
            p.Archived = true; p.IsDefault = false;
            await db.Products.Where(x => x.SellerId == a.SellerId && x.DefaultPackageId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.DefaultPackageId, (Guid?)null));
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        // a default package per product (Managers can set it for their brands)
        grp.MapPut("/products/{productId:guid}/package", async (HttpContext ctx, AppDbContext db, Guid productId, ProductPackageBody b) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.SellerId == a.SellerId);
            if (p is null || !a.CanWorkOnBrand(p.BrandId, true)) return Results.NotFound();
            if (b.PackageId is { } pid && !await db.PackagePresets.AnyAsync(x => x.Id == pid && x.SellerId == a.SellerId && !x.Archived)) return Bad("packageId", "That package doesn't exist.");
            p.DefaultPackageId = b.PackageId;
            await db.SaveChangesAsync();
            return Results.Ok(new { productId, packageId = b.PackageId });
        });

        // ---- profiles ------------------------------------------------------------------------------------------------------------------
        manage(grp.MapPost("/profiles", async (HttpContext ctx, AppDbContext db, TimeProvider time, ProfileBody b) =>
        {
            var a = ctx.GetAccess()!;
            if (b.BrandId is { } bid && !await db.BrandsFor(a).AnyAsync(x => x.Id == bid)) return Results.NotFound();
            var p = new ShippingProfile { SellerId = a.SellerId, BrandId = b.BrandId, CreatedAt = time.UtcNow(), UpdatedAt = time.UtcNow() };
            if (CleanProfile(p, b) is { } e) return e;
            if (!await db.ShippingProfiles.AnyAsync(x => x.SellerId == a.SellerId && x.BrandId == b.BrandId && x.IsDefault && !x.Archived) || b.IsDefault == true)
            { await db.ShippingProfiles.Where(x => x.SellerId == a.SellerId && x.BrandId == b.BrandId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false)); p.IsDefault = true; }
            db.ShippingProfiles.Add(p);
            await db.SaveChangesAsync();
            return Results.Json(ProfileView(p), OrderJson.Opts, statusCode: 201);
        }));

        manage(grp.MapPut("/profiles/{id:guid}", async (HttpContext ctx, AppDbContext db, TimeProvider time, Guid id, ProfileBody b) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.ShippingProfiles.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (p is null || !Scope(a, p.BrandId)) return Results.NotFound();
            if (CleanProfile(p, b) is { } e) return e;
            if (b.IsDefault == true) { await db.ShippingProfiles.Where(x => x.SellerId == a.SellerId && x.BrandId == p.BrandId && x.Id != id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false)); p.IsDefault = true; }
            p.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            return Results.Json(ProfileView(p), OrderJson.Opts);
        }));

        manage(grp.MapDelete("/profiles/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var p = await db.ShippingProfiles.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId && !x.Archived);
            if (p is null || !Scope(a, p.BrandId)) return Results.NotFound();
            p.Archived = true; p.IsDefault = false;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }));

        // ---- handling time, label format, return address ------------------------------------------------------------------------------
        manage(grp.MapPut("/settings", async (HttpContext ctx, AppDbContext db, TimeProvider time, IAuditLog audit, SettingsBody b) =>
        {
            var a = ctx.GetAccess()!;
            if (b.BrandId is { } bid && !await db.BrandsFor(a).AnyAsync(x => x.Id == bid)) return Results.NotFound();
            if (b.HandlingDays is { } h && (h < 0 || h > 30)) return Bad("handlingDays", "Use 0 to 30 days.");
            if (b.LabelFormat is { } lf && !LabelFormats.All.Contains(lf)) return Bad("labelFormat", "Use PDF_4x6, PNG or ZPLII.");
            if (b.ReturnAddressId is { } ra && !await db.ShipFromAddresses.AnyAsync(x => x.Id == ra && x.SellerId == a.SellerId && !x.Archived)) return Bad("returnAddressId", "That address doesn't exist.");
            var scope = b.BrandId ?? Guid.Empty;
            var s = await db.SellerShippingSettings.FirstOrDefaultAsync(x => x.SellerId == a.SellerId && x.ScopeKey == scope);
            if (s is null) { s = new SellerShippingSettings { SellerId = a.SellerId, BrandId = b.BrandId, ScopeKey = scope }; db.SellerShippingSettings.Add(s); }
            if (b.HandlingDays is not null) s.HandlingDays = b.HandlingDays; else if (b.ClearHandling) s.HandlingDays = null;
            if (b.LabelFormat is not null) s.LabelFormat = b.LabelFormat;
            if (b.ReturnAddressId is not null) s.ReturnAddressId = b.ReturnAddressId; else if (b.ClearReturnAddress) s.ReturnAddressId = null;
            s.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("shipping.settings_changed", a.UserId, null, $"scope={(b.BrandId is null ? "account" : "brand")}");
            return Results.Ok(new { brandId = s.BrandId, handlingDays = s.HandlingDays, labelFormat = s.LabelFormat, returnAddressId = s.ReturnAddressId });
        }));
    }

    private static async Task<(IResult? Err, bool Ok)> Apply(ShipFromAddress row, AddressInput c, IShippingProvider provider, TimeProvider time)
    {
        row.Label = c.Label ?? ""; row.Name = c.Name!; row.Company = c.Company; row.Line1 = c.Line1!; row.Line2 = c.Line2; row.City = c.City!; row.Region = c.Region;
        row.PostalCode = c.PostalCode!; row.Country = c.Country!; row.Phone = c.Phone; row.Email = c.Email;
        try
        {
            var check = await provider.ValidateAddressAsync(row.ToShip());
            if (check.Status == "invalid") return (Results.Json(new { type = "about:blank", title = check.Message ?? "We couldn't verify this address.", status = 422, code = "address_invalid" }, statusCode: 422), false);
            row.Validation = check.Status; row.ValidationMessage = check.Message;
            if (check.Suggested is { } s && check.Status == "valid") { row.Line1 = s.Line1; row.City = s.City; }
        }
        catch (ShippingProviderException ex) when (ex.Code != "not_configured")
        { row.Validation = "unverified"; row.ValidationMessage = "The address could not be checked right now."; }
        catch (ShippingProviderException) { row.Validation = "unverified"; row.ValidationMessage = "Address checking is not configured."; }
        row.ValidatedAt = time.UtcNow();
        return (null, true);
    }

    private static async Task MakeDefault(AppDbContext db, ShipFromAddress row)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ShipFromAddresses.Where(x => x.SellerId == row.SellerId && x.ScopeKey == row.ScopeKey && x.Id != row.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false).SetProperty(x => x.DefaultKey, (Guid?)null));
        row.IsDefault = true; row.DefaultKey = row.ScopeKey;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static IResult? CleanPackage(PackagePreset p, PackageBody b)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80) return Bad("name", "Enter a name (up to 80 characters).");
        var kind = b.Kind?.ToLowerInvariant() ?? "box";
        if (kind is not ("box" or "envelope" or "soft_pack")) return Bad("kind", "Use box, envelope or soft_pack.");
        if (b.LengthMm is not (> 0 and <= 2000) || b.WidthMm is not (> 0 and <= 2000) || b.HeightMm is not (> 0 and <= 2000)) return Bad("dimensions", "Enter the length, width and height in millimetres (1 to 2000).");
        if (b.EmptyWeightGrams is < 0 or > 70000) return Bad("emptyWeightGrams", "Use 0 to 70000 grams.");
        p.Name = name; p.Kind = kind; p.LengthMm = b.LengthMm.Value; p.WidthMm = b.WidthMm.Value; p.HeightMm = b.HeightMm.Value; p.EmptyWeightGrams = b.EmptyWeightGrams ?? 0;
        return null;
    }

    private static IResult? CleanProfile(ShippingProfile p, ProfileBody b)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80) return Bad("name", "Enter a name (up to 80 characters).");
        var cur = (b.Currency ?? "USD").ToUpperInvariant();
        if (cur.Length != 3 || !cur.All(char.IsAsciiLetterUpper)) return Bad("currency", "Use an ISO 4217 code like USD.");
        if (b.FreeShippingThresholdCents is < 0 or > 1_000_000_000) return Bad("freeShippingThresholdCents", "Use a positive number of cents.");
        var (err, zones) = CleanZones(b.Zones);
        if (err is not null) return Bad("zones", err);
        p.Name = name; p.Currency = cur; p.FreeShippingThresholdCents = b.FreeShippingThresholdCents;
        p.ZonesJson = JsonSerializer.Serialize(zones, OrderJson.Opts);
        return null;
    }
}
