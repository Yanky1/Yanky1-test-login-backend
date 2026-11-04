using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Tests;

/// <summary>Marketplace contract tests run with the orders secrets and limits high enough that only the dedicated rate-limit tests ever see a 429.</summary>
public class CatalogFactory : OrdersFactory
{
    public CatalogFactory()
    {
        Extra["RateLimit:InternalCatalogPerMinute"] = "100000";
        Extra["RateLimit:InternalQuestionsPerListingPerMinute"] = "100000";
        Extra["RateLimit:InternalQuestionsPerCallerPerMinute"] = "100000";
        Extra["RateLimit:InternalReportsPerTargetPerMinute"] = "100000";
        Extra["RateLimit:InternalReportsPerCallerPerMinute"] = "100000";
        Extra["RateLimit:InternalUnsignedPerMinute"] = "100000";
    }
}

/// <summary>Records every SQL command text the portal sends, in order (opt-in per test factory).</summary>
public sealed class SqlLog : DbCommandInterceptor
{
    private readonly List<string> _items = [];
    private void Add(DbCommand c) { lock (_items) _items.Add(c.CommandText); }
    public List<string> All() { lock (_items) return _items.ToList(); }
    public void Clear() { lock (_items) _items.Clear(); }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Add(command); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Add(command); return new(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Add(command); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) { Add(command); return new(result); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) { Add(command); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default) { Add(command); return new(result); }
}

/// <summary>A catalog factory whose SQL is recorded (see <see cref="SqlLog"/>).</summary>
public class CatalogSqlLogFactory : CatalogFactory
{
    public SqlLog Log { get; } = new();
    protected override void ConfigureTestServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.ConfigureDbContext<Solvelt.Api.Data.AppDbContext>(services, o => o.AddInterceptors(Log));
}

/// <summary>Image downloads accept ONLY the image key (the inbound secret no longer works for them).</summary>
public class CatalogStrictImageKeyFactory : CatalogFactory { public CatalogStrictImageKeyFactory() { Extra["Orders:ImageAcceptInboundKey"] = "false"; } }

/// <summary>An explicitly configured image key (not derived), inbound secret not accepted for images.</summary>
public class CatalogConfiguredImageKeyFactory : CatalogFactory
{
    public const string Key = "explicit-image-key-0123456789abcdef0123";
    public CatalogConfiguredImageKeyFactory() { Extra["Orders:ImageKey"] = Key; Extra["Orders:ImageAcceptInboundKey"] = "false"; }
}

/// <summary>Default marketplace write limits (nothing overridden): what production runs with.</summary>
public class CatalogDefaultLimitsFactory : OrdersFactory { }

/// <summary>Tiny limits so a test can reach them: 5 questions per listing, 3 reports per target, 40 per caller, 4 failed requests per IP.</summary>
public class CatalogTightLimitsFactory : OrdersFactory
{
    public CatalogTightLimitsFactory()
    {
        Extra["RateLimit:InternalQuestionsPerListingPerMinute"] = "5";
        Extra["RateLimit:InternalQuestionsPerCallerPerMinute"] = "40";
        Extra["RateLimit:InternalReportsPerTargetPerMinute"] = "3";
        Extra["RateLimit:InternalReportsPerCallerPerMinute"] = "40";
        Extra["RateLimit:InternalUnsignedPerMinute"] = "4";
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
