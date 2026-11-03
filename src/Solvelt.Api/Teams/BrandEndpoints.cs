using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Services;

namespace Solvelt.Api.Teams;

public record BrandRequest(string? Name, string? Description, string? ContactEmail, string? Website);

public static class ImageSniffer
{
    /// <summary>JPEG / PNG / WebP only, by magic bytes.</summary>
    public static string? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return "image/png";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "image/webp";
        return null;
    }
}

public static class BrandEndpoints
{
    public const long MaxLogoBytes = 1024 * 1024;

    private static object Dto(Brand b) => new
    {
        id = b.Id, name = b.Name, slug = b.Slug, description = b.Description, contactEmail = b.ContactEmail, website = b.Website,
        status = b.Status.ToString(), isDefault = b.IsDefault, hasLogo = b.LogoKey is not null,
        logoUrl = b.LogoKey is null ? null : $"/api/v1/brands/{b.Id}/logo?v={b.UpdatedAt.Ticks}", createdAt = b.CreatedAt
    };

    private static Dictionary<string, string[]> Validate(BrandRequest r, out string name, out string? desc, out string? email, out string? site)
    {
        var errors = new Dictionary<string, string[]>();
        name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80) errors["name"] = ["Enter a name (1-80 characters)."];
        desc = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (desc is { Length: > 500 }) errors["description"] = ["Maximum 500 characters."];
        email = string.IsNullOrWhiteSpace(r.ContactEmail) ? null : r.ContactEmail.Trim();
        if (email is not null && !Validation.IsEmail(email)) errors["contactEmail"] = ["Enter a valid email address."];
        site = string.IsNullOrWhiteSpace(r.Website) ? null : r.Website.Trim();
        if (site is not null && (site.Length > 200 || !Uri.TryCreate(site, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            errors["website"] = ["Enter a full web address starting with http:// or https://."];
        return errors;
    }

    public static void MapBrandEndpoints(this RouteGroupBuilder g)
    {
        var b = g.MapGroup("/brands").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("team");

        b.MapGet("", async (bool? includeArchived, HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var q = db.BrandsFor(a).AsNoTracking();
            if (includeArchived != true) q = q.Where(x => x.Status == BrandStatus.Active);
            return Results.Ok((await q.OrderBy(x => x.Name).ToListAsync()).Select(Dto));
        }).RequirePermission(Permission.BrandsRead);

        b.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
            await db.BrandsFor(ctx.GetAccess()!).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) is { } br ? Results.Ok(Dto(br)) : Results.NotFound())
            .RequirePermission(Permission.BrandsRead);

        b.MapPost("", async (BrandRequest r, HttpContext ctx, AppDbContext db, TeamService svc, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var errors = Validate(r, out var name, out var desc, out var email, out var site);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (await db.Brands.AnyAsync(x => x.SellerId == a.SellerId && x.Name.ToLower() == name.ToLower()))
                return Validation.Invalid("name", "You already have a brand with this name.");
            var now = time.UtcNow();
            var br = new Brand
            {
                SellerId = a.SellerId, Name = name, Slug = await svc.UniqueSlugAsync(a.SellerId, name), Description = desc,
                ContactEmail = email, Website = site, CreatedAt = now, UpdatedAt = now
            };
            db.Brands.Add(br);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Validation.Invalid("name", "You already have a brand with this name."); } // lost a race on the unique (account, name/slug) index
            await audit.WriteAsync("brand.created", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.Json(Dto(br), statusCode: 201);
        }).RequirePermission(Permission.BrandsManage);

        b.MapPut("/{id:guid}", async (Guid id, BrandRequest r, HttpContext ctx, AppDbContext db, TeamService svc, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var br = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (br is null) return Results.NotFound();
            var errors = Validate(r, out var name, out var desc, out var email, out var site);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (await db.Brands.AnyAsync(x => x.SellerId == a.SellerId && x.Id != id && x.Name.ToLower() == name.ToLower()))
                return Validation.Invalid("name", "You already have a brand with this name.");
            if (br.Name != name) { br.Name = name; br.Slug = await svc.UniqueSlugAsync(a.SellerId, name, br.Id); br.IsDefault = false; }
            br.Description = desc; br.ContactEmail = email; br.Website = site; br.UpdatedAt = time.UtcNow();
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Validation.Invalid("name", "You already have a brand with this name."); }
            await audit.WriteAsync("brand.updated", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.Ok(Dto(br));
        }).RequirePermission(Permission.BrandsManage);

        b.MapPost("/{id:guid}/archive", async (Guid id, HttpContext ctx, AppDbContext db, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var br = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (br is null) return Results.NotFound();
            if (br.Status == BrandStatus.Archived) return Results.Ok(Dto(br));
            if (!await db.Brands.AnyAsync(x => x.SellerId == a.SellerId && x.Id != id && x.Status == BrandStatus.Active))
                return Results.Json(new { type = "about:blank", title = "You can't archive your last active brand.", status = 409, code = "last_brand" }, statusCode: 409);
            br.Status = BrandStatus.Archived; br.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("brand.archived", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.Ok(Dto(br));
        }).RequirePermission(Permission.BrandsManage);

        b.MapPost("/{id:guid}/restore", async (Guid id, HttpContext ctx, AppDbContext db, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var br = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (br is null) return Results.NotFound();
            br.Status = BrandStatus.Active; br.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("brand.restored", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.Ok(Dto(br));
        }).RequirePermission(Permission.BrandsManage);

        b.MapPost("/{id:guid}/logo", async (Guid id, IFormFile file, HttpContext ctx, AppDbContext db, IFileStorage storage, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var br = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (br is null) return Results.NotFound();
            if (file.Length == 0 || file.Length > MaxLogoBytes) return Validation.Invalid("file", "The logo must be between 1 byte and 1 MB.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            await using var stream = file.OpenReadStream();
            var head = new byte[16];
            var n = await stream.ReadAsync(head);
            var type = ImageSniffer.Sniff(head.AsSpan(0, n));
            var okExt = type switch { "image/jpeg" => ext is ".jpg" or ".jpeg", "image/png" => ext == ".png", "image/webp" => ext == ".webp", _ => false };
            if (type is null || !okExt) return Validation.Invalid("file", "Use a JPEG, PNG or WebP image (the file content must match its extension).");
            stream.Position = 0;
            var key = Guid.NewGuid().ToString("N");
            await storage.SaveAsync(key, stream);
            if (br.LogoKey is not null) await storage.DeleteAsync(br.LogoKey);
            br.LogoKey = key; br.LogoContentType = type; br.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("brand.logo_changed", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.Ok(Dto(br));
        }).DisableAntiforgery().RequirePermission(Permission.BrandsManage);

        b.MapGet("/{id:guid}/logo", async (Guid id, HttpContext ctx, AppDbContext db, IFileStorage storage) =>
        {
            var br = await db.BrandsFor(ctx.GetAccess()!).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (br?.LogoKey is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(br.LogoKey);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(s, br.LogoContentType ?? "application/octet-stream"); // image/* only (sniffed on upload); inline
        }).RequirePermission(Permission.BrandsRead);

        b.MapDelete("/{id:guid}/logo", async (Guid id, HttpContext ctx, AppDbContext db, IFileStorage storage, TimeProvider time, IAuditLog audit) =>
        {
            var a = ctx.GetAccess()!;
            var br = await db.BrandsFor(a).FirstOrDefaultAsync(x => x.Id == id);
            if (br is null) return Results.NotFound();
            if (br.LogoKey is not null) await storage.DeleteAsync(br.LogoKey);
            br.LogoKey = null; br.LogoContentType = null; br.UpdatedAt = time.UtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync("brand.logo_removed", a.UserId, null, $"account={a.SellerId};brand={br.Id}");
            return Results.NoContent();
        }).RequirePermission(Permission.BrandsManage);
    }
}
