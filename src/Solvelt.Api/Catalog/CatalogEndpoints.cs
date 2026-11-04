using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Catalog;

public record IngestQuestion(string? QuestionId, string? Text, string? BuyerDisplayName);

/// <summary>
/// The marketplace catalog contract (/api/internal/v1/catalog/**, POST /products/{id}/questions). Every endpoint is signed exactly like the rest of the internal API
/// (InboundVerifier: headers, timestamp window, HMAC, single-use nonce). See docs/CATALOG.md.
/// </summary>
public static class CatalogEndpoints
{
    public const int DefaultLimit = 50, MaxLimit = 200;

    public static WebApplicationBuilder AddCatalog(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<CatalogReader>();
        var cfg = builder.Configuration;
        var feed = cfg.GetValue("RateLimit:InternalCatalogPerMinute", 1200);
        builder.Services.AddSingleton(new MarketplaceWriteLimits(
            QuestionsPerListing: cfg.GetValue("RateLimit:InternalQuestionsPerListingPerMinute", 60),
            QuestionsPerCaller: cfg.GetValue("RateLimit:InternalQuestionsPerCallerPerMinute", 2000),
            ReportsPerTarget: cfg.GetValue("RateLimit:InternalReportsPerTargetPerMinute", 60),
            ReportsPerCaller: cfg.GetValue("RateLimit:InternalReportsPerCallerPerMinute", 2000),
            FailedPerIp: cfg.GetValue("RateLimit:InternalUnsignedPerMinute", 60)));
        builder.Services.Configure<RateLimiterOptions>(r =>
        {
            static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            r.AddPolicy("internal-catalog", c => RateLimitPartition.GetFixedWindowLimiter("icat:" + Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = feed, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    private static IResult Bad(string code, string message) => InternalEndpoints.Err(400, code, message);

    public static void MapCatalogInternal(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1/catalog").RequireRateLimiting("internal-catalog").AllowAnonymous();

        // ---- the feed -------------------------------------------------------------------------------------------------------------
        g.MapGet("/listings", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, CatalogReader reader, string? since, int? limit) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            long cursor = 0;
            if (!string.IsNullOrEmpty(since) && (!long.TryParse(since, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cursor) || cursor < 0))
                return Bad("invalid_cursor", "since must be a cursor returned by this endpoint (a non-negative integer) or omitted.");
            if (limit is { } l && (l < 1 || l > MaxLimit)) return Bad("invalid_limit", $"limit must be between 1 and {MaxLimit}.");
            var take = limit ?? DefaultLimit;

            // the version order IS the commit order (see CatalogTracking), so everything with a version above the cursor is either in this page or in a later one
            var rows = await db.Products.AsNoTracking().Where(p => p.CatalogVersion > cursor && (p.CatalogListed || p.CatalogEverListed))
                .OrderBy(p => p.CatalogVersion).Take(take + 1).ToListAsync(ctx.RequestAborted);
            var more = rows.Count > take;
            if (more) rows.RemoveAt(take);
            var docs = (await reader.ListingsAsync(rows.Where(r => r.CatalogListed).ToList(), ctx.RequestAborted)).ToDictionary(d => d.Id, d => d.Doc);
            var items = rows.Select(r => r.CatalogListed ? docs[r.Id] : reader.Tombstone(r)).ToList();
            return Results.Json(new { items, nextCursor = (rows.Count > 0 ? rows[^1].CatalogVersion : cursor).ToString(), hasMore = more }, OrderJson.Opts);
        });

        g.MapGet("/listings/{id:guid}", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, CatalogReader reader, Guid id) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CatalogListed, ctx.RequestAborted);
            if (p is null) return InternalEndpoints.Err(404, "not_found", "This listing is not available.");   // unknown, draft, hidden, moderated and unlisted look the same
            return Results.Json((await reader.ListingsAsync([p], ctx.RequestAborted))[0].Doc, OrderJson.Opts);
        });

        g.MapGet("/categories", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            var cats = await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ctx.RequestAborted);
            var attrs = (await db.CategoryAttributes.AsNoTracking().OrderBy(a => a.SortOrder).ToListAsync(ctx.RequestAborted)).ToLookup(a => a.CategoryId);
            return Results.Json(new
            {
                items = cats.Select(c => new
                {
                    categoryId = c.Id, parentId = c.ParentId, name = c.Name, slug = c.Slug, sortOrder = c.SortOrder,
                    attributes = attrs[c.Id].Select(a => new { key = a.Key, label = a.Label, type = a.Type, options = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(a.OptionsJson), unit = a.Unit, group = a.Group }).ToList()
                }).ToList()
            }, OrderJson.Opts);
        });

        g.MapGet("/brands", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, string? since, int? limit) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            Guid after = Guid.Empty;
            if (!string.IsNullOrEmpty(since) && !Guid.TryParse(since, out after)) return Bad("invalid_cursor", "since must be a cursor returned by this endpoint or omitted.");
            if (limit is { } l && (l < 1 || l > MaxLimit)) return Bad("invalid_limit", $"limit must be between 1 and {MaxLimit}.");
            var take = limit ?? DefaultLimit;
            // only brands that currently have a visible listing (an empty, archived or suspended brand is not part of the marketplace)
            var all = (await db.Brands.AsNoTracking().Where(b => b.Status == BrandStatus.Active && db.Products.Any(p => p.BrandId == b.Id && p.CatalogListed))
                .Select(b => new { brandId = b.Id, sellerId = b.SellerId, name = b.Name, slug = b.Slug, description = b.Description, website = b.Website }).ToListAsync(ctx.RequestAborted))
                .OrderBy(b => b.brandId).ToList();       // ordered in memory: Guid order is the same on every database
            var rows = all.Where(b => b.brandId.CompareTo(after) > 0).Take(take + 1).ToList();
            var more = rows.Count > take;
            if (more) rows.RemoveAt(take);
            return Results.Json(new { items = rows, nextCursor = rows.Count > 0 ? rows[^1].brandId.ToString() : since, hasMore = more }, OrderJson.Opts);
        });

        // ---- images: the original bytes of images of LISTED items only; every other id is a plain 404 ------------------------------------
        g.MapGet("/images/{imageId:guid}", async (HttpContext ctx, InboundVerifier verifier, AppDbContext db, IFileStorage storage, Guid imageId) =>
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return InternalEndpoints.Err(v.Status, v.Code, v.Message);
            var img = await (from i in db.ProductImages.AsNoTracking()
                             join p in db.Products.AsNoTracking() on i.ProductId equals p.Id
                             where i.Id == imageId && p.CatalogListed
                                   && (i.VariantId == null || db.Variants.Any(x => x.Id == i.VariantId && x.Enabled))
                             select new { i.StorageKey, i.ContentType, i.Size }).FirstOrDefaultAsync(ctx.RequestAborted);
            if (img is null) return Results.NotFound();
            var s = await storage.OpenReadAsync(img.StorageKey, ctx.RequestAborted);
            if (s is null) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentDisposition = "inline";
            return Results.Stream(s, img.ContentType);
        });

        // ---- buyer questions ----------------------------------------------------------------------------------------------------------
        // limits (docs/CATALOG.md section 8): verified requests are charged to (caller, listing) and to the caller's ceiling; a failed verification only ever
        // touches the small per-IP bucket, so forged floods cannot make the marketplace's own signed requests 429
        app.MapGroup("/api/internal/v1/products").AllowAnonymous()
            .MapPost("/{id:guid}/questions", async (HttpContext ctx, InboundVerifier verifier, MarketplaceWriteLimits limits, AppDbContext db, TimeProvider time, Guid id) =>
            {
                var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
                if (!v.Ok) return limits.Rejected(ctx, v.Status, v.Code, v.Message);
                if (!limits.TryQuestion(id)) return limits.Limited(ctx, "Too many questions for this listing or from this caller.");
                IngestQuestion? req;
                try { req = System.Text.Json.JsonSerializer.Deserialize<IngestQuestion>(v.Body!, OrderJson.Opts); }
                catch (System.Text.Json.JsonException) { req = null; }
                if (req is null) return InternalEndpoints.Err(400, "invalid_json", "The request body is not valid JSON of the expected shape.");
                var qid = req.QuestionId?.Trim() ?? "";
                var text = req.Text?.Trim() ?? "";
                var name = string.IsNullOrWhiteSpace(req.BuyerDisplayName) ? "A shopper" : req.BuyerDisplayName.Trim();
                var errors = new Dictionary<string, string>();
                if (qid.Length is 0 or > 100) errors["questionId"] = "Required, at most 100 characters.";
                if (text.Length is 0 or > 1000) errors["text"] = "Required, 1-1000 characters.";
                if (name.Length > 80) errors["buyerDisplayName"] = "At most 80 characters.";
                if (errors.Count > 0) return InternalEndpoints.Err(422, "invalid_question", "The question was rejected.", errors);

                var p = await db.Products.AsNoTracking().Where(x => x.Id == id && x.CatalogListed).Select(x => new { x.Id, x.SellerId }).FirstOrDefaultAsync(ctx.RequestAborted);
                if (p is null) return InternalEndpoints.Err(404, "not_found", "This listing is not available.");

                async Task<IResult?> Existing()
                {
                    var e = await db.Questions.AsNoTracking().Where(q => q.ExternalId == qid).Select(q => new { q.Id, q.ProductId }).FirstOrDefaultAsync(ctx.RequestAborted);
                    if (e is null) return null;
                    return e.ProductId == id ? Results.Json(new { duplicate = true, questionId = qid, id = e.Id }, OrderJson.Opts)
                                             : InternalEndpoints.Err(409, "question_id_conflict", "This questionId was already used for another listing.");
                }
                if (await Existing() is { } dup) return dup;
                var row = new ProductQuestion { ProductId = p.Id, SellerId = p.SellerId, AskerName = name, Text = text, ExternalId = qid, AskedAt = time.UtcNow() };
                db.Questions.Add(row);
                try { await db.SaveChangesAsync(ctx.RequestAborted); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); if (await Existing() is { } raced) return raced; throw; }   // a parallel delivery of the same questionId won
                return Results.Json(new { duplicate = false, questionId = qid, id = row.Id }, OrderJson.Opts, statusCode: 201);
            });
    }
}

/// <summary>
/// Rate limits of the marketplace write endpoints (questions, content reports). The marketplace is ONE caller for all shoppers, so a per-IP key would be a global cap.
/// Signature-verified requests are charged to (verified caller, subject) and to a generous per-caller ceiling; requests that FAIL verification are charged only to a tight
/// per-IP bucket (same pattern as availability, docs/SERVICES.md), so a flood of forged requests can never drain the signed buckets.
/// The caller is "marketplace": the holder of <c>Orders:InboundSecret</c> is the only verified identity this API knows.
/// </summary>
public sealed class MarketplaceWriteLimits(int QuestionsPerListing, int QuestionsPerCaller, int ReportsPerTarget, int ReportsPerCaller, int FailedPerIp)
{
    public const string Caller = "marketplace";
    private static PartitionedRateLimiter<string> Make(int permits) => PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    private readonly PartitionedRateLimiter<string> _qSubject = Make(QuestionsPerListing), _qCaller = Make(QuestionsPerCaller),
        _rSubject = Make(ReportsPerTarget), _rCaller = Make(ReportsPerCaller), _failed = Make(FailedPerIp);

    private static bool Take(PartitionedRateLimiter<string> l, string key) { using var lease = l.AttemptAcquire(key); return lease.IsAcquired; }

    public bool TryQuestion(Guid listingId) => Take(_qSubject, Caller + "|" + listingId) && Take(_qCaller, Caller);
    public bool TryReport(string? targetId) => Take(_rSubject, Caller + "|" + (targetId ?? "").Trim().ToLowerInvariant()) && Take(_rCaller, Caller);
    public bool TryFailed(string ip) => Take(_failed, ip);

    public IResult Limited(HttpContext ctx, string message)
    {
        ctx.Response.Headers["Retry-After"] = "60";
        return InternalEndpoints.Err(429, "rate_limited", message);
    }

    /// <summary>A request that failed verification: its own status while the per-IP bucket has room, 429 once it is spent.</summary>
    public IResult Rejected(HttpContext ctx, int status, string code, string message) =>
        TryFailed(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") ? InternalEndpoints.Err(status, code, message) : Limited(ctx, "Too many failed requests.");
}
