using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Catalog;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Reviews;

public record ReplyIn(string? Text);

/// <summary>Replies (create or edit) per seller account and hour, counted in memory per instance like the other keyed limits.</summary>
public sealed class ReplyLimiter(IOptions<ReviewOptions> opt)
{
    private readonly PartitionedRateLimiter<Guid> _l = PartitionedRateLimiter.Create<Guid, Guid>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, opt.Value.RepliesPerHour), Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    public bool TryTake(Guid sellerId) { using var lease = _l.AttemptAcquire(sellerId); return lease.IsAcquired; }
}

/// <summary>C17: the marketplace -> portal review endpoints (signed, /api/internal/v1/reviews) and the seller API (/api/v1/seller/reviews). See docs/REVIEWS.md.</summary>
public static class ReviewEndpoints
{
    public static WebApplicationBuilder AddReviews(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ReviewOptions>(builder.Configuration.GetSection(ReviewOptions.Section));
        builder.Services.AddScoped<ReviewEngine>();
        builder.Services.AddSingleton<ReplyLimiter>();
        return builder;
    }

    private static IResult Problem(int status, string code, string title, object? errors = null) =>
        Results.Json(new { type = "about:blank", title, status, code, errors }, statusCode: status);

    /// <summary>The 422 error list in the exact shape of the order ingest: <c>[{index, code, message}]</c>, with index -1 and the field name as code for request-level problems.</summary>
    private static List<LineError> Shape(Dictionary<string, string> errors) => errors.Select(kv => new LineError(-1, kv.Key, kv.Value)).ToList();

    private static string StateName(ReviewState s) => s.ToString().ToLowerInvariant();

    private static IResult Fail(ReviewResult r) => Problem(r.Status, r.Code, r.Message, r.Errors);

    // ---- seller API --------------------------------------------------------------------------------------------------------------
    private static object Dto(Review r, string productTitle, ReviewReply? reply, bool hiddenView = false)
    {
        var reviewReply = reply is null ? null : new { id = reply.Id, text = reply.Text, createdAt = reply.CreatedAt, editedAt = reply.EditedAt };
        var common = new { id = r.Id, reviewId = r.ExternalId, productId = r.ProductId, productTitle, rating = r.Rating, text = r.Text, buyerDisplayName = r.BuyerDisplayName,
            createdAt = r.CreatedAt, editedAt = r.EditedAt, state = StateName(r.State), reply = reviewReply };
        return hiddenView
            ? new { common.id, common.reviewId, common.productId, common.productTitle, common.rating, common.text, common.buyerDisplayName, common.createdAt, common.editedAt, common.state, common.reply, stateReason = r.StateReason, stateChangedAt = r.StateChangedAt }
            : common;
    }

    public static void MapReviewEndpoints(this RouteGroupBuilder g)
    {
        var q = g.MapGroup("/seller/reviews").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("catalog");

        q.MapGet("", async (HttpContext ctx, AppDbContext db, ReviewEngine engine, int? rating, string? replied, Guid? productId, Guid? brandId, string? view, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var hiddenView = false;
            switch (view?.ToLowerInvariant())
            {
                case null or "" or "visible": break;
                case "hidden":
                    if (!a.AtLeast(AccountRole.Admin)) return Problem(403, "forbidden", "Only the Owner and Admins can see reviews hidden by the marketplace.");
                    hiddenView = true; break;
                default: return Problem(422, "invalid_view", "Use visible or hidden.");
            }
            if (rating is { } rt && rt is < 1 or > 5) return Problem(422, "invalid_rating", "rating must be from 1 to 5.");
            bool? wantReplied = null;
            switch (replied?.ToLowerInvariant())
            {
                case null or "" or "all": break;
                case "true" or "replied": wantReplied = true; break;
                case "false" or "unreplied": wantReplied = false; break;
                default: return Problem(422, "invalid_replied", "Use replied, unreplied or all.");
            }

            var rows = engine.RowsFor(a);
            if (productId is { } pid) rows = rows.Where(x => x.R.ProductId == pid);
            if (brandId is { } bid) rows = rows.Where(x => x.BrandId == bid);
            // the summary is about what buyers see: visible reviews of the chosen product / brand (not narrowed by the rating or reply filters, so the star filter can show its counts)
            var perStar = await rows.Where(x => x.R.State == ReviewState.Visible).GroupBy(x => x.R.Rating).Select(x => new { Rating = x.Key, N = x.Count() }).ToListAsync();
            var counts = Enumerable.Range(1, 5).ToDictionary(i => i.ToString(), i => perStar.FirstOrDefault(s => s.Rating == i)?.N ?? 0);
            var total = counts.Values.Sum();
            var summary = new { count = total, average = total == 0 ? (double?)null : Math.Round(perStar.Sum(s => (double)s.Rating * s.N) / total, 1), counts };
            var hiddenCount = a.AtLeast(AccountRole.Admin) ? await rows.CountAsync(x => x.R.State != ReviewState.Visible) : (int?)null;

            rows = hiddenView ? rows.Where(x => x.R.State != ReviewState.Visible) : rows.Where(x => x.R.State == ReviewState.Visible);
            if (rating is { } star) rows = rows.Where(x => x.R.Rating == star);
            if (wantReplied is true) rows = rows.Where(x => db.ReviewReplies.Any(p => p.ReviewId == x.R.Id && p.RemovedAt == null));
            if (wantReplied is false) rows = rows.Where(x => !db.ReviewReplies.Any(p => p.ReviewId == x.R.Id && p.RemovedAt == null));
            var matching = await rows.CountAsync();
            var size = Math.Clamp(pageSize ?? 25, 1, 100); var pg = Math.Max(page ?? 1, 1);
            var items = await rows.OrderByDescending(x => x.R.CreatedAt).ThenBy(x => x.R.Id).Skip((pg - 1) * size).Take(size).ToListAsync();
            var ids = items.Select(x => x.R.Id).ToList();
            var replies = (await db.ReviewReplies.AsNoTracking().Where(x => ids.Contains(x.ReviewId) && x.RemovedAt == null).ToListAsync()).ToDictionary(x => x.ReviewId);
            return Results.Json(new
            {
                items = items.Select(x => Dto(x.R, x.ProductTitle, replies.GetValueOrDefault(x.R.Id), hiddenView)),
                total = matching, page = pg, pageSize = size, summary, hiddenCount
            }, OrderJson.Opts);
        }).RequirePermission(Permission.BrandWorkRead);

        q.MapPut("/{id:guid}/reply", async (Guid id, ReplyIn body, HttpContext ctx, AppDbContext db, ReviewEngine engine, ReplyLimiter limiter) =>
        {
            var a = ctx.GetAccess()!;
            var row = await engine.RowsFor(a).FirstOrDefaultAsync(x => x.R.Id == id);
            if (row is null || !a.CanWorkOnBrand(row.BrandId, write: true)) return Results.NotFound();     // another account's, out of brand scope, or unknown: all 404
            var text = body.Text?.Trim() ?? "";
            if (text.Length is 0 or > ReviewEngine.MaxReplyLength) return Problem(422, "invalid_reply", $"Enter a reply (1-{ReviewEngine.MaxReplyLength} characters).");
            if (ContactDetector.Detect(text).Intersect(ReviewEngine.ContactKinds).Any())
                return Problem(422, "contact_details_not_allowed", "Replies can't include email addresses, phone numbers or links. Keep the conversation on Solvelt.");
            if (row.R.State != ReviewState.Visible) return Problem(409, "review_not_visible", "This review is hidden or removed, so it cannot be replied to.");
            if (!limiter.TryTake(a.SellerId)) { ctx.Response.Headers.RetryAfter = "3600"; return Problem(429, "rate_limited", "Too many replies from this account. Try again later."); }
            var res = await engine.ReplyAsync(id, a.SellerId, a.UserId, text);
            if (res.Status >= 400) return Fail(res);
            return Results.Json(Dto(res.Review!, row.ProductTitle, res.Reply), OrderJson.Opts, statusCode: res.Status);
        }).RequirePermission(Permission.BrandWorkWrite);

        q.MapDelete("/{id:guid}/reply", async (Guid id, HttpContext ctx, ReviewEngine engine) =>
        {
            var a = ctx.GetAccess()!;
            var row = await engine.RowsFor(a).FirstOrDefaultAsync(x => x.R.Id == id);
            if (row is null || !a.CanWorkOnBrand(row.BrandId, write: true)) return Results.NotFound();
            var res = await engine.DeleteReplyAsync(id, a.SellerId, a.UserId);
            return res.Status >= 400 ? Fail(res) : Results.NoContent();
        }).RequirePermission(Permission.BrandWorkWrite);
    }

    // ---- marketplace -> portal -----------------------------------------------------------------------------------------------------
    public static void MapReviewsInternal(this WebApplication app)
    {
        var g = app.MapGroup("/api/internal/v1/reviews").AllowAnonymous();

        // verification, the caller's rate limit and the JSON body, shared by both endpoints (failed verification only ever touches the small per-IP bucket)
        static async Task<(IResult? Error, JsonDocument? Doc)> Open(HttpContext ctx, InboundVerifier verifier, MarketplaceWriteLimits limits)
        {
            var v = await verifier.VerifyAsync(ctx.Request, ctx.RequestAborted);
            if (!v.Ok) return (limits.Rejected(ctx, v.Status, v.Code, v.Message), null);
            if (!limits.TryReview()) return (limits.Limited(ctx, "Too many review calls from this caller."), null);
            try
            {
                var doc = JsonDocument.Parse(v.Body!);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return (null, doc);
                doc.Dispose();
            }
            catch (JsonException) { }
            return (InternalEndpoints.Err(400, "invalid_json", "The request body is not valid JSON of the expected shape."), null);
        }

        g.MapPost("", async (HttpContext ctx, InboundVerifier verifier, MarketplaceWriteLimits limits, ReviewEngine engine) =>
        {
            var (error, doc) = await Open(ctx, verifier, limits);
            if (error is not null) return error;
            using (doc)
            {
                var (value, errors) = ReviewEngine.Parse(doc!.RootElement);
                if (value is null) return InternalEndpoints.Err(422, "invalid_review", "The review was rejected.", Shape(errors));
                var r = await engine.IngestAsync(value);
                if (r.Status >= 400) return InternalEndpoints.Err(r.Status, r.Code, r.Message, r.Errors is null ? null : Shape(r.Errors));
                return Results.Json(new { reviewId = r.Review!.ExternalId, id = r.Review.Id, duplicate = r.Duplicate, updated = r.Updated, state = StateName(r.Review.State) }, OrderJson.Opts, statusCode: r.Status);
            }
        });

        g.MapPost("/{reviewId}/state", async (string reviewId, HttpContext ctx, InboundVerifier verifier, MarketplaceWriteLimits limits, ReviewEngine engine) =>
        {
            var (error, doc) = await Open(ctx, verifier, limits);
            if (error is not null) return error;
            using (doc)
            {
                string? state = null, reason = null;
                var root = doc!.RootElement;
                if (root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String) state = s.GetString();
                if (root.TryGetProperty("reason", out var rs) && rs.ValueKind != JsonValueKind.Null)
                {
                    if (rs.ValueKind != JsonValueKind.String) return InternalEndpoints.Err(422, "invalid_state", "The review state was rejected.", Shape(new Dictionary<string, string> { ["reason"] = "Must be a string." }));
                    reason = rs.GetString();
                }
                var r = await engine.SetStateAsync(reviewId, state, reason);
                if (r.Status >= 400) return InternalEndpoints.Err(r.Status, r.Code, r.Message, r.Errors is null ? null : Shape(r.Errors));
                return Results.Json(new { reviewId = r.Review!.ExternalId, state = StateName(r.Review.State), changed = r.Changed }, OrderJson.Opts);
            }
        });
    }
}
