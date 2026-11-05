using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using Solvelt.Api.Products;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Reviews;

public record ReviewIn(string ReviewId, Guid ListingId, Guid SellerId, int Rating, string? Text, string? BuyerDisplayName, DateTime CreatedAt, DateTime? EditedAt);
public record ReviewResult(int Status, string Code, string Message, Review? Review = null, bool Duplicate = false, bool Updated = false, bool Changed = false, Dictionary<string, string>? Errors = null, ReviewReply? Reply = null);

/// <summary>
/// Reviews and seller replies (C17). One place for the rules: the marketplace ingest, the marketplace state changes, the seller's reply, and the removal of a reply (seller, review
/// removal, staff). Every change of one review first CLAIMS it (an atomic version bump inside the transaction), so the writers of one review are serialized and the events of its
/// outbox sequence <c>review:{reviewId}</c> get increasing OrderKeys without ties. See docs/REVIEWS.md.
/// </summary>
public class ReviewEngine(AppDbContext db, Outbox outbox, TimeProvider time, IAuditLog audit)
{
    public const int MaxReviewIdLength = 50, MaxTextLength = 5000, MaxNameLength = 60, MaxReplyLength = 2000, MaxReasonLength = 300;
    public static string Scope(string reviewId) => "review:" + reviewId;
    public static readonly string[] ContactKinds = ["email", "phone", "link"];

    // ---- parsing -----------------------------------------------------------------------------------------------------------------
    /// <summary>Reads the marketplace body strictly: anything about the buyer other than the display name is ignored (never stored).</summary>
    public static (ReviewIn? Value, Dictionary<string, string> Errors) Parse(JsonElement root)
    {
        var errors = new Dictionary<string, string>();
        string? Str(string name, bool required, int max, out bool ok)
        {
            ok = true;
            if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                if (required) { errors[name] = "Required."; ok = false; }
                return null;
            }
            if (v.ValueKind != JsonValueKind.String) { errors[name] = "Must be a string."; ok = false; return null; }
            var s = v.GetString()!;
            if (s.Length > max) { errors[name] = $"At most {max} characters."; ok = false; return null; }
            return s;
        }
        var reviewId = Str("reviewId", true, MaxReviewIdLength, out _)?.Trim();
        if (reviewId is { Length: 0 }) { errors["reviewId"] = "Required."; reviewId = null; }
        Guid listing = default, seller = default;
        var l = Str("listingId", true, 100, out var lok); if (lok && l is not null && !Guid.TryParse(l, out listing)) errors["listingId"] = "Must be a listing id.";
        var s2 = Str("sellerId", true, 100, out var sok); if (sok && s2 is not null && !Guid.TryParse(s2, out seller)) errors["sellerId"] = "Must be a seller id.";
        var rating = 0;
        if (!root.TryGetProperty("rating", out var r) || r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out rating) || rating is < 1 or > 5) errors["rating"] = "An integer from 1 to 5.";
        var text = Str("text", false, MaxTextLength, out _);
        var name = Str("buyerDisplayName", false, MaxNameLength, out _);
        DateTime? Date(string key, bool required)
        {
            if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) { if (required) errors[key] = "Required."; return null; }
            if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return d;
            errors[key] = "Must be an ISO 8601 date and time."; return null;
        }
        var created = Date("createdAt", true);
        var edited = Date("editedAt", false);
        if (errors.Count > 0 || reviewId is null || created is null) return (null, errors);
        return (new ReviewIn(reviewId, listing, seller, rating, string.IsNullOrEmpty(text) ? null : text, name, created.Value, edited), errors);
    }

    // ---- claim ---------------------------------------------------------------------------------------------------------------------
    /// <summary>Takes the review for this transaction: an atomic conditional version bump. False when the review is gone or another writer kept winning.</summary>
    private async Task<bool> ClaimAsync(Guid reviewId)
    {
        for (var i = 0; i < 4; i++)
        {
            var v = await db.Reviews.AsNoTracking().Where(x => x.Id == reviewId).Select(x => (int?)x.Version).FirstOrDefaultAsync();
            if (v is null) return false;
            if (await db.Reviews.Where(x => x.Id == reviewId && x.Version == v).ExecuteUpdateAsync(u => u.SetProperty(x => x.Version, v.Value + 1)) == 1) return true;
        }
        return false;
    }

    private async Task<T> InTx<T>(Func<Task<T>> work) where T : class
    {
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            var res = await work();
            if (tx is not null) await tx.CommitAsync();
            return res;
        }
        catch { if (tx is not null) await tx.RollbackAsync(); db.ChangeTracker.Clear(); throw; }
    }

    // ---- marketplace -> portal -----------------------------------------------------------------------------------------------------
    public async Task<ReviewResult> IngestAsync(ReviewIn r)
    {
        var errors = new Dictionary<string, string>();
        var product = await db.Products.AsNoTracking().Where(p => p.Id == r.ListingId).Select(p => new { p.Id, p.SellerId }).FirstOrDefaultAsync();
        if (product is null || product.SellerId != r.SellerId) errors["listingId"] = "The listing must be one of this seller's products (and sellerId must match).";
        if (errors.Count > 0) return new(422, "invalid_review", "The review was rejected.", Errors: errors);

        async Task<ReviewResult?> Existing()
        {
            var e = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(x => x.ExternalId == r.ReviewId);
            if (e is null) return null;
            if (e.SellerId != r.SellerId || e.ProductId != r.ListingId)
                return new(422, "invalid_review", "The review was rejected.", Errors: new() { ["listingId"] = "This reviewId already belongs to another listing." });
            var newer = r.EditedAt is { } at && (e.EditedAt is null || at > e.EditedAt);
            if (!newer || e.State == ReviewState.Removed) return new(200, "ok", "", e, Duplicate: true);          // older or equal editedAt, and a removed (redacted) review: unchanged
            // one conditional UPDATE: the stored editedAt only ever moves forward, so parallel deliveries of two edits end with the newest
            var n = await db.Reviews.Where(x => x.Id == e.Id && x.State != ReviewState.Removed && (x.EditedAt == null || x.EditedAt < r.EditedAt))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Rating, r.Rating).SetProperty(x => x.Text, r.Text).SetProperty(x => x.BuyerDisplayName, r.BuyerDisplayName).SetProperty(x => x.EditedAt, r.EditedAt));
            var fresh = await db.Reviews.AsNoTracking().FirstAsync(x => x.Id == e.Id);
            return new(200, "ok", "", fresh, Duplicate: n == 0, Updated: n == 1);
        }
        if (await Existing() is { } known) return known;

        var review = new Review
        {
            ExternalId = r.ReviewId, SellerId = r.SellerId, ProductId = r.ListingId, Rating = r.Rating, Text = r.Text, BuyerDisplayName = r.BuyerDisplayName,
            CreatedAt = r.CreatedAt, EditedAt = r.EditedAt, ReceivedAt = time.UtcNow()
        };
        db.Reviews.Add(review);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await Existing() is { } raced) return raced;       // a parallel delivery of the same reviewId won
            throw;
        }
        return new(201, "ok", "", review);
    }

    /// <summary>Marketplace moderation of a review: visible | hidden | removed. Removed is final: it redacts the text and the display name and removes the reply.</summary>
    public async Task<ReviewResult> SetStateAsync(string reviewId, string? state, string? reason)
    {
        var target = state?.Trim().ToLowerInvariant() switch { "visible" => ReviewState.Visible, "hidden" => ReviewState.Hidden, "removed" => ReviewState.Removed, _ => (ReviewState?)null };
        if (target is null) return new(422, "invalid_state", "The review state was rejected.", Errors: new() { ["state"] = "Use visible, hidden or removed." });
        var why = reason?.Trim();
        if (why is { Length: > MaxReasonLength }) return new(422, "invalid_state", "The review state was rejected.", Errors: new() { ["reason"] = $"At most {MaxReasonLength} characters." });
        if (string.IsNullOrEmpty(why)) why = null;
        var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(x => x.ExternalId == reviewId.Trim());
        if (review is null) return new(404, "not_found", "Unknown review.");
        if (review.State == target) return new(200, "ok", "", review, Changed: false);
        if (review.State == ReviewState.Removed) return new(409, "review_removed", "A removed review cannot change state again.");

        return await InTx(async () =>
        {
            if (!await ClaimAsync(review.Id)) return new ReviewResult(409, "conflict", "The review was changed at the same time; retry.");
            var row = await db.Reviews.FirstAsync(x => x.Id == review.Id);
            if (row.State == ReviewState.Removed) { db.ChangeTracker.Clear(); return new ReviewResult(409, "review_removed", "A removed review cannot change state again."); }
            var now = time.UtcNow();
            row.State = target.Value; row.StateReason = why; row.StateChangedAt = now;
            if (target == ReviewState.Removed)
            {
                row.Text = null; row.BuyerDisplayName = null;      // redaction: the rating stays for the averages, nothing a person wrote does
                await RemoveReplyAsync(row, "review_removed");
            }
            await db.SaveChangesAsync();
            return new ReviewResult(200, "ok", "", row, Changed: true);
        });
    }

    // ---- seller replies ------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Creates or edits the reply (the caller has checked scope, permission, the text rules and the rate limit). One transaction: claim, reply row, outbox event. Editing with the same text changes nothing.
    /// </summary>
    public async Task<ReviewResult> ReplyAsync(Guid reviewId, Guid sellerId, Guid userId, string text)
    {
        var res = await InTx(async () =>
        {
            if (!await ClaimAsync(reviewId)) return new ReviewResult(409, "conflict", "The review was changed at the same time; retry.");
            var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId && x.SellerId == sellerId);
            if (review is null) return new ReviewResult(404, "not_found", "Unknown review.");
            if (review.State != ReviewState.Visible) return new ReviewResult(409, "review_not_visible", "This review is hidden or removed, so it cannot be replied to.");
            var now = time.UtcNow();
            var reply = await db.ReviewReplies.FirstOrDefaultAsync(x => x.ReviewId == reviewId && x.RemovedAt == null);
            var created = reply is null;
            if (reply is null)
            {
                reply = new ReviewReply { ReviewId = reviewId, SellerId = sellerId, Text = text, CreatedAt = now, CreatedBy = userId };
                db.ReviewReplies.Add(reply);
            }
            else
            {
                if (reply.Text == text) return new ReviewResult(200, "ok", "", review, Changed: false, Reply: reply);
                reply.Text = text; reply.EditedAt = now; reply.EditedBy = userId;
            }
            outbox.Enqueue("review.reply_posted", sellerId, reviewId.ToString(),
                new { reviewId = review.ExternalId, replyId = reply.Id, text, repliedAt = reply.CreatedAt, editedAt = created ? (DateTime?)null : now },
                scope: Scope(review.ExternalId));
            await db.SaveChangesAsync();
            return new ReviewResult(created ? 201 : 200, "ok", "", review, Changed: true, Reply: reply);
        });
        if (res is { Status: < 300, Changed: true, Reply: { } r })
            await audit.WriteAsync(r.EditedAt is null ? "review.reply_posted" : "review.reply_edited", userId, null, $"seller={sellerId};review={reviewId};reply={r.Id}");
        return res;
    }

    public async Task<ReviewResult> DeleteReplyAsync(Guid reviewId, Guid sellerId, Guid userId)
    {
        var res = await InTx(async () =>
        {
            if (!await ClaimAsync(reviewId)) return new ReviewResult(409, "conflict", "The review was changed at the same time; retry.");
            var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId && x.SellerId == sellerId);
            if (review is null) return new ReviewResult(404, "not_found", "Unknown review.");
            var removed = await RemoveReplyAsync(review, "seller_deleted");
            if (removed is null) return new ReviewResult(404, "not_found", "This review has no reply.");
            await db.SaveChangesAsync();
            return new ReviewResult(200, "ok", "", review, Changed: true, Reply: removed);
        });
        if (res is { Status: 200, Reply: { } r }) await audit.WriteAsync("review.reply_deleted", userId, null, $"seller={sellerId};review={reviewId};reply={r.Id}");
        return res;
    }

    /// <summary>
    /// Staff removal of a reported reply (inside the moderation transaction, after the report was claimed). Claims the review so the event takes its place in the sequence; false when the reply
    /// was already gone (the seller deleted it a moment earlier: that removal already sent its event), and <c>null</c> when the review could not be claimed (another writer kept winning):
    /// the caller must roll the decision back and answer 409, the reply is still there.
    /// </summary>
    public async Task<bool?> RemoveReplyForStaffAsync(Guid replyId)
    {
        var reply = await db.ReviewReplies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == replyId);
        if (reply is null || reply.RemovedAt is not null) return false;
        if (!await ClaimAsync(reply.ReviewId)) return null;
        var review = await db.Reviews.AsNoTracking().FirstAsync(x => x.Id == reply.ReviewId);
        return await RemoveReplyAsync(review, "staff_removed", replyId) is not null;
    }

    /// <summary>Marks the review's active reply removed (text cleared) and queues <c>review.reply_removed</c>. The review must be claimed by the caller, who also saves.</summary>
    private async Task<ReviewReply?> RemoveReplyAsync(Review review, string reason, Guid? onlyReply = null)
    {
        var reply = await db.ReviewReplies.AsNoTracking().FirstOrDefaultAsync(x => x.ReviewId == review.Id && x.RemovedAt == null && (onlyReply == null || x.Id == onlyReply));
        if (reply is null) return null;
        var now = time.UtcNow();
        var id = reply.Id;
        if (await db.ReviewReplies.Where(x => x.Id == id && x.RemovedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.RemovedAt, (DateTime?)now).SetProperty(x => x.RemovedReason, reason).SetProperty(x => x.Text, "")) != 1) return null;
        outbox.Enqueue("review.reply_removed", review.SellerId, review.Id.ToString(), new { reviewId = review.ExternalId, replyId = id, removedAt = now, reason }, scope: Scope(review.ExternalId));
        return reply;
    }

    // ---- reads ---------------------------------------------------------------------------------------------------------------------
    /// <summary>The seller-side view of reviews inside the caller's account and brand scope (join to the products the caller may see).</summary>
    public IQueryable<ReviewRow> RowsFor(AccountAccess a) =>
        from r in db.Reviews.AsNoTracking()
        join p in db.ProductsFor(a) on r.ProductId equals p.Id
        where r.SellerId == a.SellerId
        select new ReviewRow { R = r, ProductTitle = p.Title, BrandId = p.BrandId };
}

public class ReviewRow
{
    public Review R { get; init; } = null!;
    public string ProductTitle { get; init; } = "";
    public Guid BrandId { get; init; }
}
