using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Reviews;

namespace Solvelt.Api.Tests;

/// <summary>Reviews tests run with the marketplace limits high enough that only the dedicated tests ever see a 429.</summary>
public class ReviewFactory : CatalogFactory
{
    public ReviewFactory() { Extra["RateLimit:InternalReviewsPerCallerPerMinute"] = "100000"; }
}

/// <summary>Tiny limits: 6 review calls per minute for the caller, 4 failed requests per IP.</summary>
public class ReviewTightFactory : CatalogFactory
{
    public ReviewTightFactory()
    {
        Extra["RateLimit:InternalReviewsPerCallerPerMinute"] = "6";
        Extra["RateLimit:InternalUnsignedPerMinute"] = "4";
    }
}

/// <summary>3 replies per hour per account (the marketplace limits stay high).</summary>
public class ReviewReplyLimitFactory : ReviewFactory
{
    public ReviewReplyLimitFactory() { Extra["Reviews:RepliesPerHour"] = "3"; }
}

/// <summary>Helpers for the marketplace review endpoints, the seller reviews API and the events they cause.</summary>
public static class Rev
{
    public const string Path = "/api/internal/v1/reviews";
    public static string Rid() => "rev-" + Guid.NewGuid().ToString("N")[..16];

    public static object Body(ApiFactory f, Shop s, string? id = null, int rating = 5, string? text = "Lovely mug, arrived quickly.", string? name = "Jane B.", DateTime? created = null, DateTime? edited = null,
        Guid? listing = null, Guid? seller = null) => new
    {
        reviewId = id ?? Rid(), listingId = listing ?? s.Product, sellerId = seller ?? s.Seller, rating, text, buyerDisplayName = name,
        createdAt = created ?? f.Time.GetUtcNow().UtcDateTime.AddMinutes(-5), editedAt = edited
    };

    public static Task<HttpResponseMessage> Post(ApiFactory f, object body, string? nonce = null, long? ts = null) => Cat.PostSigned(f, Path, body, nonce, ts);
    public static Task<HttpResponseMessage> State(ApiFactory f, string reviewId, object body) => Cat.PostSigned(f, $"{Path}/{reviewId}/state", body);

    /// <summary>Ingests a review through the signed endpoint; returns the portal's id and the marketplace id.</summary>
    public static async Task<(Guid Id, string ReviewId)> Ingest(ApiFactory f, Shop s, string? id = null, int rating = 5, string? text = "Lovely mug, arrived quickly.", string? name = "Jane B.", Guid? listing = null)
    {
        var body = Body(f, s, id, rating, text, name, listing: listing);
        var res = await Post(f, body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var n = await Orders.Node(res);
        return (Guid.Parse((string)n["id"]!), (string)n["reviewId"]!);
    }

    public static async Task<JsonNode> List(Api api, string query = "")
    {
        var r = await api.Get("/api/v1/seller/reviews" + query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Orders.Node(r);
    }

    public static JsonNode? Find(JsonNode list, Guid id) => list["items"]!.AsArray().FirstOrDefault(i => (string)i!["id"]! == id.ToString());

    public static Task<HttpResponseMessage> Reply(Api api, Guid id, string text) => api.Put($"/api/v1/seller/reviews/{id}/reply", new { text });
    public static Task<HttpResponseMessage> DeleteReply(Api api, Guid id) => api.Delete($"/api/v1/seller/reviews/{id}/reply");

    /// <summary>Review events of one review, oldest sequence first (aggregate = the portal's review id).</summary>
    public static List<Solvelt.Api.Domain.OutboxMessage> Outbox(ApiFactory f, Guid reviewId) =>
        f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == reviewId.ToString() && m.EventType.StartsWith("review.")).OrderBy(m => m.OrderKey).ToList());

    public static List<JsonNode> Events(ApiFactory f, Guid reviewId, string? type = null) =>
        Outbox(f, reviewId).Where(m => type is null || m.EventType == type).Select(m => JsonNode.Parse(m.PayloadJson)!).ToList();

    public static string[] Keys(JsonNode n) => n.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>A second product of the same seller in another brand (for brand-scope tests).</summary>
    public static async Task<(Guid Brand, Guid Product)> OtherBrandProduct(Shop s)
    {
        var brand = await TeamHelpers.NewBrand(s.Owner, "Second brand " + Guid.NewGuid().ToString("N")[..6]);
        var product = await Orders.ActiveProduct(s.Owner, "Other brand mug", brand, "SKU-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        return (brand, product);
    }
}

/// <summary>Can make every claim of a review (the conditional version bump) lose, as if another writer kept winning.</summary>
public sealed class ClaimBreaker : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public volatile bool Active;
    private void Break(System.Data.Common.DbCommand c)
    {
        if (Active && c.CommandText.Contains("UPDATE \"reviews\"") && c.CommandText.Contains("\"Version\"")) c.CommandText = c.CommandText.Replace("WHERE ", "WHERE 1 = 0 AND ");
    }
    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result) { Break(command); return result; }
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default) { Break(command); return new(result); }
}

public class ReviewClaimFactory : ReviewFactory
{
    public ClaimBreaker Breaker { get; } = new();
    protected override void ConfigureTestServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.ConfigureDbContext<Solvelt.Api.Data.AppDbContext>(services, o => o.AddInterceptors(Breaker));
}
