using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class DemoFactory : PayFactory
{
    public DemoFactory() { Extra["Demo:Enabled"] = "true"; Extra["Demo:PerHour"] = "100"; }
    public List<string> Errors { get; } = [];
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(new Capture(Errors)));
    }
    private sealed class Capture(List<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string c) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => l >= Microsoft.Extensions.Logging.LogLevel.Error;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId e, TState st, Exception? ex, Func<TState, Exception?, string> fmt)
        { if (l >= Microsoft.Extensions.Logging.LogLevel.Error) lock (sink) sink.Add(fmt(st, ex) + " :: " + ex); }
    }
}

public static class DemoKit
{
    public static Dictionary<string, long> TableCounts(ApiFactory f) => f.WithDb(db =>
    {
        var res = new Dictionary<string, long>();
        foreach (var t in db.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(n => n is not null).Distinct())
            res[t!] = db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM \"{t}\"").AsEnumerable().First();
        return res;
    });

    public static Dictionary<string, long> Diff(Dictionary<string, long> a, Dictionary<string, long> b) =>
        b.Where(kv => a.GetValueOrDefault(kv.Key) != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value - a.GetValueOrDefault(kv.Key));
}

public class DemoLimitedFactory : DemoFactory { public DemoLimitedFactory() { Extra["Demo:PerHour"] = "2"; } }

/// <summary>The test-only gate: refuses Production, and the endpoints exist only when Demo:Enabled is true.</summary>
public class DemoGateTests
{
    private sealed class ProdFactory : DemoFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) { base.ConfigureWebHost(builder); builder.UseEnvironment("Production"); }
    }

    [Fact]
    public void App_refuses_to_start_with_demo_enabled_in_production()
    {
        using var prod = new ProdFactory();
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        var all = ex.ToString();
        Assert.Contains("Demo:Enabled must never be true in the Production environment", all);
    }

    [Fact]
    public async Task Endpoints_are_404_and_config_says_disabled_when_demo_is_off()
    {
        using var off = new ApiFactory();
        var (api, _) = await ProdApi.Approved(off);
        Assert.Equal(HttpStatusCode.NotFound, await api.Get("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.NotFound, await api.Post("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.NotFound, await api.Delete("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.NotFound, await api.Post("/api/v1/seller/demo/approve").Code());
        var cfg = await api.JsonBody(await api.Get("/api/v1/public-config", false));
        Assert.False(cfg.GetProperty("demoEnabled").GetBoolean());
    }

    [Fact]
    public async Task Config_exposes_demoEnabled_when_on()
    {
        using var on = new DemoFactory();
        var cfg = await new Api(on).JsonBody(await new Api(on).Get("/api/v1/public-config", false));
        Assert.True(cfg.GetProperty("demoEnabled").GetBoolean());
    }
}

public class DemoFillTests(DemoFactory f) : IClassFixture<DemoFactory>
{
    private static readonly string[] Roles = ["Admin", "Manager", "Viewer", "Driver"];

    [Fact]
    public async Task Only_the_owner_may_use_the_demo_endpoints()
    {
        var (owner, seller) = await ProdApi.Approved(f);
        foreach (var role in Roles)
        {
            var (member, _) = await TeamHelpers.JoinAs(f, owner, role);
            await member.Switch(seller);
            Assert.Equal(HttpStatusCode.Forbidden, await member.Get("/api/v1/seller/demo-data").Code());
            Assert.Equal(HttpStatusCode.Forbidden, await member.Post("/api/v1/seller/demo-data").Code());
            Assert.Equal(HttpStatusCode.Forbidden, await member.Delete("/api/v1/seller/demo-data").Code());
            Assert.Equal(HttpStatusCode.Forbidden, await member.Post("/api/v1/seller/demo/approve").Code());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await new Api(f).Post("/api/v1/seller/demo-data", null, false).Code());
        Assert.Equal(0, f.WithDb(db => db.DemoBatches.Count(b => b.SellerId == seller)));
        Assert.Equal(HttpStatusCode.OK, await owner.Get("/api/v1/seller/demo-data").Code());
    }

    private async Task<(Api api, Guid seller, JsonNode fill)> Filled()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var r = await api.Post("/api/v1/seller/demo-data");
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync() + string.Join("\n", f.Errors));
        return (api, seller, await Orders.Node(r));
    }

    [Fact]
    public async Task Fill_creates_every_kind_of_record_and_the_ledger_is_consistent()
    {
        var (api, seller, fill) = await Filled();
        Assert.Equal(8, (int)fill["counts"]!["products"]!);
        Assert.Equal(3, (int)fill["counts"]!["services"]!);
        Assert.Equal(10, (int)fill["counts"]!["orders"]!);
        f.WithDb(db =>
        {
            var statuses = db.Orders.Where(o => o.SellerId == seller).Select(o => o.Status).ToList();
            foreach (var st in new[] { OrderStatus.New, OrderStatus.Accepted, OrderStatus.PartiallyShipped, OrderStatus.Shipped, OrderStatus.Delivered, OrderStatus.Cancelled, OrderStatus.Refunded, OrderStatus.ReturnRequested })
                Assert.Contains(st, statuses);
            Assert.Equal(1, db.LocalDeliveries.Count(d => d.SellerId == seller));
            Assert.Equal(1, db.Returns.Count(r => db.Orders.Any(o => o.Id == r.OrderId && o.SellerId == seller)));
            Assert.True(db.Shipments.Count(x => db.Orders.Any(o => o.Id == x.OrderId && o.SellerId == seller) && x.TrackingNumber != null) >= 3);
            var bs = db.Bookings.Where(b => b.SellerId == seller).Select(b => b.Status).ToList();
            foreach (var st in new[] { BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Completed, BookingStatus.CancelledBySeller }) Assert.Contains(st, bs);
            Assert.Equal(1, db.QuoteRequests.Count(q => q.SellerId == seller));
            Assert.Equal(1, db.Questions.Count(q => q.SellerId == seller));
            Assert.True(db.Conversations.Count(c => c.SellerId == seller && c.Kind == ConversationKind.Order) >= 3);
            Assert.Equal(1, db.Conversations.Count(c => c.SellerId == seller && c.Kind == ConversationKind.Product));
            Assert.Equal(11, db.Products.Count(p => p.SellerId == seller));
            Assert.Equal(1, db.ShipFromAddresses.Count(x => x.SellerId == seller)); Assert.Equal(1, db.PackagePresets.Count(x => x.SellerId == seller)); Assert.Equal(1, db.ShippingProfiles.Count(x => x.SellerId == seller));
            Assert.True(db.Products.Where(p => p.SellerId == seller).All(p => p.Type == ListingType.Service || db.ProductImages.Any(i => i.ProductId == p.Id)));
            // every journal balances to zero; transfers moved exactly the settled amount; nothing pending went negative
            var entries = db.LedgerEntries.Where(e => e.SellerId == seller).ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries.GroupBy(e => e.JournalId), g => Assert.Equal(0, g.Sum(e => e.AmountCents)));
            var sum = (Solvelt.Api.Domain.LedgerAccount a) => entries.Where(e => e.Account == a).Sum(e => e.AmountCents);
            Assert.True(sum(LedgerAccount.Pending) > 0, "pending balance");
            Assert.True(sum(LedgerAccount.Settled) > 0, "a transfer was made");
            Assert.True(entries.Where(e => e.Account == LedgerAccount.Pending).GroupBy(e => e.OrderId ?? e.BookingId).All(g => g.Sum(e => e.AmountCents) >= 0));
            var t = db.PaymentTransfers.Single(x => x.SellerId == seller);
            Assert.Equal(t.AmountCents, sum(LedgerAccount.Settled));
            Assert.Equal(TransferStatus.Created, t.Status);
            return 0;
        });
        // the payments screens show it
        var summary = await api.JsonBody(await api.Get("/api/v1/seller/payments/summary"));
        Assert.Contains("pending", summary.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, await api.Get($"/api/v1/seller/payments/statements/{DateTime.UtcNow:yyyy-MM}").Code());
        var transfers = await api.JsonBody(await api.Get("/api/v1/seller/payments/transfers"));
        Assert.Contains("items", transfers.ToString());
    }

    [Fact]
    public async Task Lists_mark_demo_records_with_demo_true_and_real_ones_stay_unmarked()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var real = await Orders.ActiveProduct(api, "My real product", brand, "REAL-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        var list = await Orders.Node(await api.Get("/api/v1/seller/products?pageSize=100"));
        var items = list["items"]!.AsArray();
        Assert.Contains(items, i => (string)i!["id"]! == real.ToString() && i["demo"] is null);
        Assert.Equal(11, items.Count(i => i!["demo"]?.GetValue<bool>() == true));
        var orders = await Orders.Node(await api.Get("/api/v1/seller/orders?pageSize=100"));
        Assert.Equal(10, orders["items"]!.AsArray().Count(i => i!["demo"]?.GetValue<bool>() == true));
    }

    [Fact]
    public async Task A_second_fill_is_409_and_parallel_fills_create_one_batch()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => api.Post("/api/v1/seller/demo-data").Code()));
        Assert.Equal(1, results.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(3, results.Count(c => c == HttpStatusCode.Conflict));
        Assert.Equal(1, f.WithDb(db => db.DemoBatches.Count(b => b.SellerId == seller)));
        Assert.Equal(10, f.WithDb(db => db.Orders.Count(o => o.SellerId == seller)));
        var again = await api.Post("/api/v1/seller/demo-data");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("demo_data_exists", await again.ErrCode());
    }

    [Fact]
    public async Task Remove_takes_away_exactly_the_demo_records_and_real_ones_survive()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var realProduct = await Orders.ActiveProduct(api, "My real product", brand, "REAL-" + Guid.NewGuid().ToString("N")[..6]);
        var shop = new Shop(f, api, seller, brand, realProduct, "x");
        var (_, realOrder, _, _) = await Orders.Place(shop);
        Assert.Equal(HttpStatusCode.OK, await Orders.Act(api, realOrder, "accept").Code());
        var before = DemoKit.TableCounts(f);

        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        var d = await api.Delete("/api/v1/seller/demo-data");
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(10, (int)(await Orders.Node(d))["removed"]!["orders"]!);

        // real records survive untouched; the account is back to what it was, table by table (only logs, counters and feed bookkeeping differ)
        Assert.True(f.WithDb(db => db.Products.Any(p => p.Id == realProduct)));
        Assert.True(f.WithDb(db => db.Orders.Any(o => o.Id == realOrder && o.Status == OrderStatus.Accepted)));
        var left = DemoKit.Diff(before, DemoKit.TableCounts(f));
        var allowed = new[] { "AuditEvents", "audit_events", "outbox_messages", "catalog_tombstones", "booking_counters", "order_counters", "catalog_refresh_requests" };
        Assert.True(left.Keys.All(k => allowed.Contains(k)), "left behind: " + string.Join(", ", left.Where(kv => !allowed.Contains(kv.Key)).Select(kv => kv.Key + "=" + kv.Value)));
        Assert.Equal(0, f.WithDb(db => db.LedgerEntries.Count(e => e.SellerId == seller && e.OrderId != realOrder && e.OrderId != null)));
        // gone from the catalog feed as tombstones, and the real product is still listed
        var (feed, _) = await Cat.ReadFeed(f);
        Assert.True(Cat.InFeed(feed, realProduct));
        Assert.Equal(11, feed.Count(i => !(bool)i["listed"]! && (string)i["reason"]! == "deleted"));
        // fill again after removal works, and removing twice is harmless
        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.OK, await api.Delete("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.OK, await api.Delete("/api/v1/seller/demo-data").Code());
    }

    [Fact]
    public async Task Demo_orders_and_bookings_send_no_outbox_events_no_mail_and_no_staff_metrics()
    {
        var (api, seller, _) = await Filled();
        var email = api.Session!.Email;
        Assert.Empty(f.WithDb(db => db.Outbox.Where(m => !m.EventType.StartsWith("listing.")).ToList().Where(m => m.SellerId == seller).ToList()));
        Assert.Empty(f.Mails(email).Where(m => m.Kind is not ("verify-code" or "welcome")).Where(m => m.Kind.Contains("order") || m.Kind.Contains("message") || m.Kind.Contains("booking") || m.Kind == "payment-notice").ToList());
        // later actions on a demo order stay silent too
        var newOrder = f.WithDb(db => db.Orders.First(o => o.SellerId == seller && o.Status == OrderStatus.New).Id);
        Assert.Equal(HttpStatusCode.OK, await Orders.Act(api, newOrder, "accept").Code());
        var conv = f.WithDb(db => db.Conversations.First(c => c.SellerId == seller && c.Kind == ConversationKind.Order).Id);
        Assert.True((await api.Post($"/api/v1/seller/messages/{conv}/messages", new { text = "Thanks for your message (demo reply)" })).IsSuccessStatusCode);
        Assert.Empty(f.WithDb(db => db.Outbox.Where(m => m.SellerId == seller && !m.EventType.StartsWith("listing.")).ToList()));
        // a real order still produces its event
        var brand = await TeamHelpers.BrandId(api);
        var real = await Orders.ActiveProduct(api, "Real mug", brand, "R-" + Guid.NewGuid().ToString("N")[..6]);
        var (_, realOrder, _, _) = await Orders.Place(new Shop(f, api, seller, brand, real, "x"));
        await Orders.Act(api, realOrder, "accept");
        Assert.NotEmpty(f.WithDb(db => db.Outbox.Where(m => m.SellerId == seller && m.EventType == "order.accepted").ToList()));
        // staff metrics ignore demo data
        var (admin, _) = await StaffKit.LoginSteppedUp(f, Solvelt.Api.Admin.StaffRole.SuperAdmin);
        var detail = await admin.JsonBody(await admin.Get($"/api/v1/admin/sellers/{seller}"));
        Assert.Equal(1, detail.GetProperty("counts").GetProperty("orders").GetInt32());
        Assert.Equal(1, detail.GetProperty("counts").GetProperty("listings").GetInt32());
        Assert.Equal(0, detail.GetProperty("counts").GetProperty("bookings").GetInt32());
    }

    [Fact]
    public async Task Demo_listings_carry_demo_true_in_the_catalog_feed_and_real_ones_demo_false()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var real = await Orders.ActiveProduct(api, "Real product", brand, "R-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        var (feed, _) = await Cat.ReadFeed(f);
        var mine = feed.Where(i => (bool)i["listed"]! && (string)i["seller"]!["sellerId"]! == seller.ToString()).ToList();
        Assert.Equal(11, mine.Count(i => (bool)i["demo"]!));
        Assert.Contains(mine, i => (string)i["listingId"]! == real.ToString() && !(bool)i["demo"]!);
        var one = await Cat.Listing(f, Guid.Parse((string)mine.First(i => (bool)i["demo"]!)["listingId"]!));
        Assert.True((bool)one!["demo"]!);
    }

    [Fact]
    public async Task Fill_works_for_a_pending_account_and_does_not_approve_it()
    {
        var (api, seller) = await ProdApi.Approved(f, approve: false);
        await api.CompleteApplicationAndSubmit();
        var r = await api.Post("/api/v1/seller/demo-data");
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync() + string.Join("\n", f.Errors));
        Assert.Equal(SellerStatus.PendingReview, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).Status));
        Assert.Equal(11, f.WithDb(db => db.Products.Count(p => p.SellerId == seller)));
        // nothing is listed before approval
        var (feed, _) = await Cat.ReadFeed(f);
        Assert.DoesNotContain(feed, i => (bool)i["listed"]! && (string)i["seller"]!["sellerId"]! == seller.ToString());
        Assert.Equal(HttpStatusCode.OK, await api.Delete("/api/v1/seller/demo-data").Code());
    }
}

public class DemoApproveTests(DemoFactory f) : IClassFixture<DemoFactory>
{
    private async Task<(Api api, Guid seller)> Pending()
    {
        var (api, seller) = await ProdApi.Approved(f, approve: false);
        await api.CompleteApplicationAndSubmit();
        return (api, seller);
    }

    [Fact]
    public async Task Owner_approves_the_own_pending_account_exactly_like_staff_would()
    {
        var (me, mine) = await Pending();
        var (other, theirs) = await Pending();
        var r = await me.Post("/api/v1/seller/demo/approve");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Approved", (await Orders.Node(r))["status"]!.ToString());
        var (admin, _) = await StaffKit.LoginSteppedUp(f, Solvelt.Api.Admin.StaffRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/applications/{theirs}/approve", new { reason = StaffKit.Why }).Code());
        // same status and the same side effects: status audit, approval mail (to the sink)
        f.WithDb(db =>
        {
            Assert.Equal(SellerStatus.Approved, db.Sellers.Single(s => s.Id == mine).Status);
            Assert.Equal(SellerStatus.Approved, db.Sellers.Single(s => s.Id == theirs).Status);
            var changed = db.AuditEvents.Where(e => e.Type == "seller.status_changed" && e.Detail!.Contains("PendingReview->Approved")).ToList();
            Assert.Contains(changed, e => e.Detail!.EndsWith($"seller={mine}") && e.Detail.Contains("actor=demo-self-approve"));
            Assert.Contains(changed, e => e.Detail!.EndsWith($"seller={theirs}"));
            Assert.Contains(db.AuditEvents.ToList(), e => e.Type == "admin.demo.self_approve" && e.TargetId == mine.ToString() && e.Reason == "demo-self-approve");
            Assert.Contains(db.AuditEvents.ToList(), e => e.Type == "demo.self_approved" && e.Detail!.Contains($"seller={mine}"));
            return 0;
        });
        Assert.Single(f.Mails(me.Session!.Email, "approved"));
        Assert.Single(f.Mails(other.Session!.Email, "approved"));
    }

    [Fact]
    public async Task It_cannot_approve_any_other_account()
    {
        var (me, mine) = await Pending();
        var (_, victim) = await Pending();
        var r = await me.Post("/api/v1/seller/demo/approve", new { sellerId = victim, id = victim });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(SellerStatus.Approved, f.WithDb(db => db.Sellers.Single(s => s.Id == mine).Status));
        Assert.Equal(SellerStatus.PendingReview, f.WithDb(db => db.Sellers.Single(s => s.Id == victim).Status));
        // a second, already approved, owner has nothing to approve, and the victim is still untouched
        Assert.Equal(SellerStatus.PendingReview, f.WithDb(db => db.Sellers.Single(s => s.Id == victim).Status));
    }

    [Fact]
    public async Task Already_approved_is_409_and_other_states_are_409_not_pending()
    {
        var (api, seller) = await Pending();
        Assert.Equal(HttpStatusCode.OK, await api.Post("/api/v1/seller/demo/approve").Code());
        var again = await api.Post("/api/v1/seller/demo/approve");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_approved", await again.ErrCode());
        // a draft (application not submitted) is not pending
        var (draft, draftSeller) = await ProdApi.Approved(f, approve: false);
        var d = await draft.Post("/api/v1/seller/demo/approve");
        Assert.Equal(HttpStatusCode.Conflict, d.StatusCode);
        Assert.Equal("not_pending", await d.ErrCode());
        Assert.Equal(SellerStatus.Draft, f.WithDb(db => db.Sellers.Single(s => s.Id == draftSeller).Status));
        // a suspended account is never un-suspended: its session is cut off before the handler, and its status does not change
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Suspended" }).Code());
        var s = await api.Post("/api/v1/seller/demo/approve");
        Assert.True(s.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal(SellerStatus.Suspended, f.WithDb(db => db.Sellers.Single(x => x.Id == seller).Status));
        // a rejected account likewise
        var (rej, rejSeller) = await Pending();
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{rejSeller}/status", new { status = "Rejected" }).Code());
        var rr = await rej.Post("/api/v1/seller/demo/approve");
        Assert.True(rr.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal(SellerStatus.Rejected, f.WithDb(db => db.Sellers.Single(x => x.Id == rejSeller).Status));
    }
}

public class DemoRateLimitTests(DemoLimitedFactory f) : IClassFixture<DemoLimitedFactory>
{
    [Fact]
    public async Task Demo_actions_are_limited_per_account_per_hour_and_every_action_is_audited()
    {
        var (api, seller) = await ProdApi.Approved(f);
        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        Assert.Equal(HttpStatusCode.OK, await api.Delete("/api/v1/seller/demo-data").Code());
        var third = await api.Post("/api/v1/seller/demo-data");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
        // another account is not affected, and an hour later the limit is open again
        var (other, _) = await ProdApi.Approved(f);
        Assert.Equal(HttpStatusCode.Created, await other.Post("/api/v1/seller/demo-data").Code());
        f.Time.Advance(TimeSpan.FromMinutes(61));
        await api.LoginAs(api.Session!.Email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.Created, await api.Post("/api/v1/seller/demo-data").Code());
        var types = f.WithDb(db => db.AuditEvents.Where(e => e.Type.StartsWith("demo.")).Select(e => e.Type).ToList());
        Assert.Contains("demo.filled", types); Assert.Contains("demo.removed", types);
    }
}
