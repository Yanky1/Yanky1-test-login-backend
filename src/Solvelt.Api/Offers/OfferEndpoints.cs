using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Offers;

public record EndOfferRequest(int? Version);

/// <summary>C18 quantity offers: the seller API (/api/v1/seller/offers) and the signed catalog feed of offers (/api/internal/v1/catalog/offers). See docs/CONTRACT.md.</summary>
public static class OfferEndpoints
{
    public static WebApplicationBuilder AddOffers(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<OfferOptions>(builder.Configuration.GetSection(OfferOptions.Section));
        builder.Services.AddScoped<OfferService>();
        builder.Services.AddScoped<OfferIntake>();
        return builder;
    }

    private static IResult Problem(int status, string code, string title, object? extra = null) =>
        Results.Json(new { type = "about:blank", title, status, code, extra }, statusCode: status);

    private static IResult Fail(OfferResult r) => Problem(r.Status, r.Code, r.Message, r.Extra);

    /// <summary>The offers the caller may see: their account, brands in their scope, never removed ones.</summary>
    private static IQueryable<Offer> OffersFor(AppDbContext db, AccountAccess a)
    {
        var q = db.Offers.AsNoTracking().Where(o => o.SellerId == a.SellerId && o.DeletedAt == null);
        if (a.AllBrands || a.Role >= AccountRole.Admin) return q;
        var ids = a.BrandIds.ToList();
        return q.Where(o => ids.Contains(o.BrandId));
    }

    private static async Task<(Guid? BrandId, IResult? Error)> BrandAsync(AppDbContext db, AccountAccess a, Guid? requested)
    {
        if (requested is { } id)
        {
            var b = await db.RequireBrandAccess(a, id, write: true);
            return b is null || b.Status != BrandStatus.Active ? (null, Results.NotFound()) : (b.Id, null);
        }
        var all = (await db.BrandsFor(a).Where(b => b.Status == BrandStatus.Active).OrderBy(b => b.Name).ToListAsync()).Where(b => a.CanWorkOnBrand(b.Id, true)).ToList();
        var pick = all.Count == 1 ? all[0] : all.FirstOrDefault(b => b.IsDefault);
        return pick is null ? (null, Solvelt.Api.Endpoints.Validation.Invalid("brandId", "Choose a brand for this offer.")) : (pick.Id, null);
    }

    private static async Task<object> DetailAsync(AppDbContext db, OfferService svc, TimeProvider time, Offer o)
    {
        var now = time.UtcNow();
        var versions = await svc.VersionsAsync(o.Id);
        var cur = versions.LastOrDefault();
        var ids = new HashSet<Guid>();
        if (cur is not null) { foreach (var g in cur.Gifts) ids.Add(g.ProductId); if (cur.Row.Scope == OfferScope.Products) foreach (var p in cur.ScopeIds) ids.Add(p); }
        var idList = ids.ToList();
        var titles = await db.Products.AsNoTracking().Where(p => idList.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title);
        var catIds = cur is not null && cur.Row.Scope == OfferScope.Category ? cur.ScopeIds : new List<Guid>();
        var prodIds = cur is not null && cur.Row.Scope == OfferScope.Products ? cur.ScopeIds : new List<Guid>();
        var cats = await db.Categories.AsNoTracking().Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        var brand = await db.Brands.AsNoTracking().Where(b => b.Id == o.BrandId).Select(b => b.Name).FirstOrDefaultAsync();
        var demo = await db.DemoRecords.AsNoTracking().AnyAsync(r => r.EntityId == o.Id);
        return new
        {
            id = o.Id, brandId = o.BrandId, brandName = brand, name = o.Name, status = OfferPricing.StatusName(o.Status), state = OfferService.State(o, cur?.Row, now),
            version = o.CurrentVersion, createdAt = o.CreatedAt, updatedAt = o.UpdatedAt, endedAt = o.EndedAt, demo,
            scope = cur is null ? "all" : OfferPricing.ScopeName(cur.Row.Scope),
            categoryIds = catIds, categories = catIds.Select(c => new { id = c, name = cats.GetValueOrDefault(c) }),
            productIds = prodIds,
            products = prodIds.Select(p => new { id = p, title = titles.GetValueOrDefault(p) }).ToList(),
            tiers = cur?.Tiers.Select(t => new { minQuantity = t.MinQuantity, unitPriceCents = t.UnitPriceCents }),
            gifts = cur?.Gifts.Select(g => new { minQuantity = g.MinQuantity, productId = g.ProductId, quantity = g.Quantity, title = titles.GetValueOrDefault(g.ProductId) }),
            activeFrom = cur?.Row.ActiveFrom, activeTo = cur?.Row.ActiveTo,
            versions = versions.Select(v => new { version = v.Row.Version, status = OfferPricing.StatusName(v.Row.Status), createdAt = v.Row.CreatedAt, supersededAt = v.Row.SupersededAt }).Reverse()
        };
    }

    public static void MapOfferEndpoints(this RouteGroupBuilder g)
    {
        var q = g.MapGroup("/seller/offers").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        q.MapGet("", async (HttpContext ctx, AppDbContext db, TimeProvider time, Guid? brandId, string? status) =>
        {
            var a = ctx.GetAccess()!;
            var rows = OffersFor(db, a);
            if (brandId is { } bid) rows = rows.Where(o => o.BrandId == bid);
            switch (status?.ToLowerInvariant())
            {
                case null or "" or "all": break;
                case "draft": rows = rows.Where(o => o.Status == OfferStatus.Draft); break;
                case "active": rows = rows.Where(o => o.Status == OfferStatus.Active); break;
                case "ended": rows = rows.Where(o => o.Status == OfferStatus.Ended); break;
                default: return Problem(422, "invalid_status", "Use draft, active, ended or all.");
            }
            var list = await rows.OrderByDescending(o => o.UpdatedAt).ThenBy(o => o.Id).Take(200).ToListAsync();
            var ids = list.Select(o => o.Id).ToList();
            var cur = (await db.OfferVersions.AsNoTracking().Where(v => ids.Contains(v.OfferId) && v.SupersededAt == null).ToListAsync()).ToDictionary(v => v.OfferId, OfferVersionView.Of);
            var demo = (await db.DemoRecords.AsNoTracking().Where(r => ids.Contains(r.EntityId)).Select(r => r.EntityId).ToListAsync()).ToHashSet();
            var brands = await db.Brands.AsNoTracking().Where(b => b.SellerId == a.SellerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var now = time.UtcNow();
            return Results.Json(new
            {
                items = list.Select(o =>
                {
                    var v = cur.GetValueOrDefault(o.Id);
                    return new
                    {
                        id = o.Id, name = o.Name, brandId = o.BrandId, brandName = brands.GetValueOrDefault(o.BrandId), status = OfferPricing.StatusName(o.Status),
                        state = OfferService.State(o, v?.Row, now), version = o.CurrentVersion, scope = v is null ? "all" : OfferPricing.ScopeName(v.Row.Scope),
                        tiers = v?.Tiers.Select(t => new { minQuantity = t.MinQuantity, unitPriceCents = t.UnitPriceCents }), giftCount = v?.Gifts.Count ?? 0,
                        activeFrom = v?.Row.ActiveFrom, activeTo = v?.Row.ActiveTo, updatedAt = o.UpdatedAt, demo = demo.Contains(o.Id)
                    };
                })
            }, OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        q.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, OfferService svc, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var o = await OffersFor(db, a).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null) return Results.NotFound();   // another account's, outside the brand scope, removed or unknown: all 404
            return Results.Json(await DetailAsync(db, svc, time, o), OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        q.MapPost("", async (OfferWrite w, HttpContext ctx, AppDbContext db, OfferService svc, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var (brandId, err) = await BrandAsync(db, a, w.BrandId);
            if (err is not null) return err;
            var (draft, errors) = await svc.ValidateAsync(a.SellerId, brandId!.Value, w);
            if (draft is null) return Results.ValidationProblem(errors);
            var r = await svc.CreateAsync(a, brandId.Value, draft);
            if (r.Status >= 400) return Fail(r);
            return Results.Json(await DetailAsync(db, svc, time, r.Offer!), OrderJson.Opts, statusCode: 201);
        }).RequirePermission(Permission.BrandWorkWrite);

        q.MapPut("/{id:guid}", async (Guid id, OfferWrite w, HttpContext ctx, AppDbContext db, OfferService svc, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var o = await OffersFor(db, a).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, write: true)) return Results.NotFound();
            if (w.BrandId is { } b && b != o.BrandId) return Solvelt.Api.Endpoints.Validation.Invalid("brandId", "The brand of an offer can't be changed.");
            if (w.Version is null) return Solvelt.Api.Endpoints.Validation.Invalid("version", "Send the version you edited.");
            var (draft, errors) = await svc.ValidateAsync(a.SellerId, o.BrandId, w);
            if (draft is null) return Results.ValidationProblem(errors);
            var r = await svc.UpdateAsync(a, o, w.Version.Value, draft);
            if (r.Status >= 400) return Fail(r);
            return Results.Json(await DetailAsync(db, svc, time, r.Offer!), OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite);

        q.MapPost("/{id:guid}/end", async (Guid id, EndOfferRequest? body, HttpContext ctx, AppDbContext db, OfferService svc, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var o = await OffersFor(db, a).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null || !a.CanWorkOnBrand(o.BrandId, write: true)) return Results.NotFound();
            var r = await svc.EndAsync(a, o, body?.Version);
            if (r.Status >= 400) return Fail(r);
            return Results.Json(await DetailAsync(db, svc, time, r.Offer!), OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkWrite);

        // the live preview of the editor: all money is computed here (the UI only formats what it gets)
        q.MapPost("/preview", async (OfferWrite w, HttpContext ctx, AppDbContext db, OfferService svc) =>
        {
            var a = ctx.GetAccess()!;
            Guid brandId;
            if (w.BrandId is { } b)
            {
                var br = await db.RequireBrandAccess(a, b, write: false);
                if (br is null) return Results.NotFound();
                brandId = br.Id;
            }
            else
            {
                // read access is enough to preview: the default (or only) brand the caller can see
                var seen = (await db.BrandsFor(a).Where(x => x.Status == BrandStatus.Active).OrderBy(x => x.Name).ToListAsync()).Where(x => a.CanWorkOnBrand(x.Id, false)).ToList();
                var pick = seen.Count == 1 ? seen[0] : seen.FirstOrDefault(x => x.IsDefault) ?? seen.FirstOrDefault();
                if (pick is null) return Solvelt.Api.Endpoints.Validation.Invalid("brandId", "Choose a brand.");
                brandId = pick.Id;
            }
            var (draft, errors) = await svc.ValidateAsync(a.SellerId, brandId, w, preview: true);
            if (draft is null) return Results.ValidationProblem(errors);
            return Results.Json(await svc.PreviewAsync(a.SellerId, brandId, draft, w.SampleProductId, w.Quantities), OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);
    }

    // ---- marketplace: signed feed --------------------------------------------------------------------------------------------------
    public static void MapOffersInternal(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1/catalog/offers").RequireRateLimiting("internal-catalog").AllowAnonymous();

        g.MapGet("", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, OfferService svc, string? since, int? limit) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            long cursor = 0;
            if (!string.IsNullOrEmpty(since) && (!long.TryParse(since, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cursor) || cursor < 0))
                return InternalEndpoints.Err(400, "invalid_cursor", "since must be a cursor returned by this endpoint (a non-negative integer) or omitted.");
            if (limit is { } l && (l < 1 || l > Solvelt.Api.Catalog.CatalogEndpoints.MaxLimit)) return InternalEndpoints.Err(400, "invalid_limit", $"limit must be between 1 and {Solvelt.Api.Catalog.CatalogEndpoints.MaxLimit}.");
            var take = limit ?? Solvelt.Api.Catalog.CatalogEndpoints.DefaultLimit;
            // feed versions come from the catalog counter, taken under its row lock: the version order is the commit order, so the cursor never skips a change
            var rows = await db.Offers.AsNoTracking().Where(o => o.EverListed && o.FeedVersion > cursor).OrderBy(o => o.FeedVersion).Take(take + 1).ToListAsync(ctx.RequestAborted);
            var more = rows.Count > take;
            if (more) rows.RemoveAt(take);
            var items = await svc.FeedDocsAsync(rows, ctx.RequestAborted);
            return Results.Json(new { items, nextCursor = (rows.Count > 0 ? rows[^1].FeedVersion : cursor).ToString(System.Globalization.CultureInfo.InvariantCulture), hasMore = more }, OrderJson.Opts);
        });

        g.MapGet("/{offerId:guid}", async (Guid offerId, HttpContext ctx, InboundVerifier verifier, AppDbContext db, OfferService svc) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            var o = await db.Offers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == offerId && x.EverListed);
            if (o is null) return InternalEndpoints.Err(404, "not_found", "Unknown offer.");
            return Results.Json((await svc.FeedDocsAsync([o], ctx.RequestAborted))[0], OrderJson.Opts);
        });
    }
}
