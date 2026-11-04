using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Tests;

/// <summary>Marketplace contract tests run with the orders secrets and limits high enough that only the dedicated rate-limit tests ever see a 429.</summary>
public class CatalogFactory : OrdersFactory
{
    public CatalogFactory()
    {
        Extra["RateLimit:InternalCatalogPerMinute"] = "100000";
        Extra["RateLimit:InternalQuestionsPerMinute"] = "100000";
        Extra["RateLimit:InternalReportsPerMinute"] = "100000";
    }
}

public class CatalogLimitedFactory : OrdersFactory
{
    public CatalogLimitedFactory()
    {
        Extra["RateLimit:InternalCatalogPerMinute"] = "100000";
        Extra["RateLimit:InternalQuestionsPerMinute"] = "3";
        Extra["RateLimit:InternalReportsPerMinute"] = "3";
    }
}

/// <summary>Helpers for the signed catalog feed, the marketplace-side questions / reports and the events they cause.</summary>
public static class Cat
{
    public const string Base = "/api/internal/v1/catalog";

    public static async Task<HttpResponseMessage> SignedGet(ApiFactory f, string path, string? nonce = null, long? ts = null, string secret = OrdersFactory.InSecret) =>
        await f.NewClient().SendAsync(Orders.Signed(f, "GET", path, null, nonce, ts, secret));

    public static async Task<JsonNode> GetOk(ApiFactory f, string path)
    {
        var r = await SignedGet(f, path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Orders.Node(r);
    }

    /// <summary>Reads the whole feed from <paramref name="since"/> with the given page size; returns every item in the order served plus the final cursor.</summary>
    public static async Task<(List<JsonNode> Items, string Cursor)> ReadFeed(ApiFactory f, string since = "0", int limit = 200)
    {
        var items = new List<JsonNode>(); var cursor = since;
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = await GetOk(f, $"{Base}/listings?since={cursor}&limit={limit}");
            foreach (var i in page["items"]!.AsArray()) items.Add(i!);
            cursor = (string)page["nextCursor"]!;
            if (!(bool)page["hasMore"]!) return (items, cursor);
        }
        throw new InvalidOperationException("feed did not end");
    }

    public static async Task<JsonNode?> Listing(ApiFactory f, Guid id)
    {
        var r = await SignedGet(f, $"{Base}/listings/{id}");
        return r.StatusCode == HttpStatusCode.OK ? await Orders.Node(r) : null;
    }

    public static bool InFeed(IEnumerable<JsonNode> items, Guid id) => items.Any(i => (string)i["listingId"]! == id.ToString() && (bool)i["listed"]!);

    /// <summary>Events of one aggregate (a listing id, a report's id) as parsed JSON, oldest first.</summary>
    public static List<JsonNode> Events(ApiFactory f, string aggregate, string? type = null) =>
        Orders.OutboxFor(f, aggregate).Where(m => type is null || m.EventType == type).Select(m => JsonNode.Parse(m.PayloadJson)!).ToList();

    public static List<(string Type, JsonNode Data)> ListingEvents(ApiFactory f, Guid id) =>
        f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == id.ToString() && m.EventType.StartsWith("listing.")).OrderBy(m => m.OrderKey).ToList())
            .Select(m => (m.EventType, JsonNode.Parse(m.PayloadJson)!["data"]!)).ToList();

    public static long Version(ApiFactory f, Guid id) => f.WithDb(db => db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.CatalogVersion).Single());

    public static async Task SetPrice(Api api, Guid id, double price) => Assert.Equal(HttpStatusCode.OK, (await ProdApi.Put(api, id, d => d["price"] = price)).StatusCode);

    public static async Task<HttpResponseMessage> PostSigned(ApiFactory f, string path, object body, string? nonce = null, long? ts = null) =>
        await f.NewClient().SendAsync(Orders.Signed(f, "POST", path, body, nonce, ts));

    public static async Task<Guid> Draft(Api api, Guid brand, string title) => (await ProdApi.ReadyProduct(api, title, brand, "D-" + Guid.NewGuid().ToString("N")[..8]));

    public static async Task DispatchAll(ApiFactory f)
    {
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)).StatusCode);
    }

    public static async Task<Api> Staff(ApiFactory f, StaffRole role) => await StaffKit.LoginNew(f, role);

    /// <summary>Moves a seller account to a status through the staff API (Suspended / Approved).</summary>
    public static async Task SetSellerStatus(ApiFactory f, Guid seller, string status)
    {
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var r = await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status, reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
