using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Endpoints;

public record BusinessRequest(string? VendorName, string? ContactName, string? ContactPhone, string? AddressLine1, string? AddressLine2,
    string? City, string? Region, string? PostalCode, string? Country, bool SmsOptIn);
public record KosherRequest(Dictionary<string, string?>? Answers);
public record SubmitRequest(bool AcceptSellerAgreement);
public record StatusRequest(string? Status, string? Note);

public static partial class SellerEndpoints
{
    public const string SmsWording = "I agree to receive text messages from Solvelt about my seller account. Message and data rates may apply. Reply STOP to opt out.";
    public const long MaxCertBytes = 5 * 1024 * 1024;

    [GeneratedRegex(@"^\+?[0-9 ()\-]{7,20}$")] private static partial Regex PhoneRx();

    private static bool Editable(SellerAccount s) => s.Status is SellerStatus.Draft or SellerStatus.NeedsInfo;

    private static async Task<SellerAccount?> Mine(AppDbContext db, ClaimsPrincipal p)
    {
        var id = p.UserId();
        return await db.Sellers.FirstOrDefaultAsync(s => s.UserId == id);
    }

    private static async Task<object> Dto(AppDbContext db, SellerAccount s, IOptions<AppOptions> app)
    {
        var cert = await db.SellerFiles.AsNoTracking().Where(f => f.SellerId == s.Id && f.FieldKey == "kosher_certificate").FirstOrDefaultAsync();
        return new
        {
            sellerId = s.Id, status = s.Status.ToString(), statusNote = s.StatusNote, draftStep = s.DraftStep, editable = Editable(s),
            business = new
            {
                s.VendorName, s.ContactName, s.ContactPhone, s.AddressLine1, s.AddressLine2, s.City, s.Region, s.PostalCode, s.Country,
                s.SmsOptIn
            },
            kosher = new
            {
                answers = KosherService.Parse(s.KosherAnswersJson),
                certificate = cert is null ? null : new { id = cert.Id, name = cert.OriginalName, size = cert.Size, contentType = cert.ContentType }
            },
            legal = new { s.TermsVersion, s.TermsAcceptedAt, s.SellerAgreementVersion, s.SellerAgreementAcceptedAt },
            marketplaceUrl = app.Value.MarketplaceUrl
        };
    }

    public static void MapSellerEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/public-config", (IOptions<AppOptions> app, IOptions<TurnstileOptions> ts) => Results.Ok(new
        {
            marketplaceUrl = app.Value.MarketplaceUrl, termsVersion = app.Value.TermsVersion,
            sellerAgreementVersion = app.Value.SellerAgreementVersion, smsWordingVersion = app.Value.SmsWordingVersion,
            smsWording = SmsWording, turnstileSiteKey = ts.Value.SiteKey
        }));

        var s = g.MapGroup("/seller").RequireAuthorization("Seller");

        s.MapGet("/field-config", async (KosherService k) => Results.Ok((await k.ConfigAsync()).Select(f => new
        {
            f.Key, f.Label, f.Type, f.ParentKey, f.ParentValue, f.Required, f.SortOrder,
            options = f.OptionsJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(f.OptionsJson)
        })));

        s.MapGet("/application", async (ClaimsPrincipal p, AppDbContext db, IOptions<AppOptions> app) =>
            await Mine(db, p) is { } sel ? Results.Ok(await Dto(db, sel, app)) : Results.NotFound());

        var w = s.MapGroup("/application").RequireAuthorization("VerifiedEmail");

        w.MapPut("/business", async (BusinessRequest r, ClaimsPrincipal p, AppDbContext db, TimeProvider time, IOptions<AppOptions> app) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            if (!Editable(sel)) return Results.Problem("The application can't be edited in its current status.", statusCode: 409);
            var errors = new Dictionary<string, string[]>();
            string? T(string? v, int max, string key, bool required)
            {
                var t = v?.Trim();
                if (string.IsNullOrEmpty(t)) { if (required) errors[key] = ["Required."]; return null; }
                if (t.Length > max) errors[key] = [$"Maximum {max} characters."];
                return t;
            }
            var vendor = T(r.VendorName, 200, "vendorName", true);
            var contact = T(r.ContactName, 120, "contactName", true);
            var phone = T(r.ContactPhone, 20, "contactPhone", true);
            if (phone is not null && !PhoneRx().IsMatch(phone)) errors["contactPhone"] = ["Enter a valid phone number."];
            var a1 = T(r.AddressLine1, 200, "addressLine1", true);
            var a2 = T(r.AddressLine2, 200, "addressLine2", false);
            var city = T(r.City, 100, "city", true);
            var region = T(r.Region, 100, "region", false);
            var postal = T(r.PostalCode, 20, "postalCode", true);
            var country = T(r.Country, 2, "country", true)?.ToUpperInvariant();
            if (country is not null && !(country.Length == 2 && country.All(char.IsAsciiLetterUpper))) errors["country"] = ["Use a 2-letter country code."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            sel.VendorName = vendor; sel.ContactName = contact; sel.ContactPhone = phone; sel.AddressLine1 = a1; sel.AddressLine2 = a2;
            sel.City = city; sel.Region = region; sel.PostalCode = postal; sel.Country = country;
            if (r.SmsOptIn && !sel.SmsOptIn)
            { sel.SmsOptIn = true; sel.SmsConsentAt = time.UtcNow(); sel.SmsConsentWordingVersion = app.Value.SmsWordingVersion; }
            else if (!r.SmsOptIn) { sel.SmsOptIn = false; sel.SmsConsentAt = null; sel.SmsConsentWordingVersion = null; }
            sel.DraftStep = Math.Max(sel.DraftStep, 4); sel.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(await Dto(db, sel, app));
        });

        w.MapPut("/kosher", async (KosherRequest r, ClaimsPrincipal p, AppDbContext db, KosherService k, TimeProvider time, IOptions<AppOptions> app) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            if (!Editable(sel)) return Results.Problem("The application can't be edited in its current status.", statusCode: 409);
            var (_, errors) = await k.ApplyAsync(sel, r.Answers ?? new());
            if (errors.Count > 0) return Results.ValidationProblem(errors.ToDictionary(e => e.Key, e => new[] { e.Value }));
            sel.DraftStep = Math.Max(sel.DraftStep, 5); sel.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(await Dto(db, sel, app));
        });

        w.MapPost("/kosher/certificate", async (IFormFile file, ClaimsPrincipal p, AppDbContext db, KosherService k, IFileStorage storage, TimeProvider time, IAuditLog audit) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            if (!Editable(sel)) return Results.Problem("The application can't be edited in its current status.", statusCode: 409);
            var cfg = await k.ConfigAsync();
            if (!KosherService.VisibleKeys(cfg, KosherService.Parse(sel.KosherAnswersJson)).Contains("kosher_certificate"))
                return Results.Problem("A certificate isn't needed for your answers.", statusCode: 400);
            if (file.Length == 0 || file.Length > MaxCertBytes) return Validation.Invalid("file", "File must be between 1 byte and 5 MB.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg")) return Validation.Invalid("file", "Only PDF, PNG or JPG files are allowed.");
            await using var stream = file.OpenReadStream();
            var head = new byte[16];
            var n = await stream.ReadAsync(head);
            var sniffed = FileSniffer.Sniff(head.AsSpan(0, n));
            var okExt = sniffed switch { "application/pdf" => ext == ".pdf", "image/png" => ext == ".png", "image/jpeg" => ext is ".jpg" or ".jpeg", _ => false };
            if (sniffed is null || !okExt) return Validation.Invalid("file", "The file content doesn't match an allowed type.");
            stream.Position = 0;
            await k.DeleteFilesAsync(sel.Id, "kosher_certificate");
            var key = Guid.NewGuid().ToString("N");
            await storage.SaveAsync(key, stream);
            var safeName = Path.GetFileName(file.FileName);
            if (safeName.Length > 120) safeName = safeName[^120..];
            var rec = new SellerFile
            {
                SellerId = sel.Id, FieldKey = "kosher_certificate", StorageKey = key, ContentType = sniffed,
                OriginalName = safeName, Size = file.Length, CreatedAt = time.UtcNow()
            };
            db.SellerFiles.Add(rec);
            await db.SaveChangesAsync();
            await audit.WriteAsync("seller.certificate_uploaded", sel.UserId);
            return Results.Ok(new { id = rec.Id, name = rec.OriginalName, size = rec.Size, contentType = rec.ContentType });
        }).DisableAntiforgery();

        w.MapGet("/kosher/certificate", async (ClaimsPrincipal p, AppDbContext db, IFileStorage storage) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            var f = await db.SellerFiles.FirstOrDefaultAsync(x => x.SellerId == sel.Id && x.FieldKey == "kosher_certificate");
            if (f is null) return Results.NotFound();
            var stream = await storage.OpenReadAsync(f.StorageKey);
            if (stream is null) return Results.NotFound();
            return Results.File(stream, f.ContentType, f.OriginalName); // attachment; nosniff + no-store set globally
        });

        w.MapDelete("/kosher/certificate", async (ClaimsPrincipal p, AppDbContext db, KosherService k) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            if (!Editable(sel)) return Results.Problem("The application can't be edited in its current status.", statusCode: 409);
            await k.DeleteFilesAsync(sel.Id, "kosher_certificate");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        w.MapPost("/submit", async (SubmitRequest r, ClaimsPrincipal p, AppDbContext db, KosherService k, SellerStatusService status,
            TimeProvider time, IOptions<AppOptions> app) =>
        {
            var sel = await Mine(db, p);
            if (sel is null) return Results.NotFound();
            if (!Editable(sel)) return Results.Problem("The application was already submitted.", statusCode: 409);
            if (!r.AcceptSellerAgreement) return Validation.Invalid("acceptSellerAgreement", "You must accept the Seller Agreement.");
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(sel.VendorName) || string.IsNullOrWhiteSpace(sel.ContactName) || string.IsNullOrWhiteSpace(sel.AddressLine1))
                errors["business"] = ["Complete the business step."];
            foreach (var (key, msg) in await k.ValidateForSubmitAsync(sel)) errors[key] = [msg];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            sel.SellerAgreementVersion = app.Value.SellerAgreementVersion; sel.SellerAgreementAcceptedAt = time.UtcNow();
            var (ok, err) = await status.ChangeAsync(sel, SellerStatus.PendingReview, null, null);
            return ok ? Results.Ok(await Dto(db, sel, app)) : Results.Problem(err, statusCode: 409);
        });
    }

    public static void MapAdminEndpoints(this RouteGroupBuilder g)
    {
        g.MapPost("/admin/sellers/{id:guid}/status", async (Guid id, StatusRequest r, ClaimsPrincipal p, AppDbContext db, SellerStatusService status) =>
        {
            if (!Enum.TryParse<SellerStatus>(r.Status, true, out var to) || !Enum.IsDefined(to))
                return Validation.Invalid("status", "Unknown status.");
            if (r.Note is { Length: > 1000 }) return Validation.Invalid("note", "Maximum 1000 characters.");
            var sel = await db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
            if (sel is null) return Results.NotFound();
            var (ok, err) = await status.ChangeAsync(sel, to, r.Note, p.UserId());
            return ok ? Results.Ok(new { sellerId = sel.Id, status = sel.Status.ToString() }) : Results.Problem(err, statusCode: 409);
        }).RequireAuthorization("Admin");
    }

    public static void MapTestEndpoints(this WebApplication app)
    {
        var t = app.MapGroup("/api/test");
        t.MapGet("/emails", (string? to, string? kind, EmailSink sink) => Results.Ok(sink.All()
            .Where(m => (to is null || m.To.Equals(to, StringComparison.OrdinalIgnoreCase)) && (kind is null || m.Kind == kind))
            .Select(m => new { m.To, m.Kind, m.Subject, m.Html, m.Text })));
        t.MapDelete("/emails", (EmailSink sink) => { sink.Clear(); return Results.NoContent(); });
    }
}
