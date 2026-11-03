using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Tests;

/// <summary>"Live" was removed as a seller status (user decision): Approved is the final selling state; Approved &lt;-&gt; Suspended.</summary>
public class LiveStatusRemovalTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private async Task<Api> Legacy() { var a = new Api(f); await a.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword); return a; }

    [Fact]
    public async Task The_admin_status_api_rejects_Live_with_a_clear_message()
    {
        var shop = await Orders.NewShop(f);
        var admin = await Legacy();
        foreach (var live in new[] { "Live", "live", " LIVE " })
        {
            var res = await admin.Post($"/api/v1/admin/sellers/{shop.Seller}/status", new { status = live });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("Live was removed; use Approved.", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal("Approved", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        // the staff lists reject it too (400), they do not silently return nothing
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        Assert.Equal(HttpStatusCode.BadRequest, (await ro.Get("/api/v1/admin/sellers?status=Live")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ro.Get("/api/v1/admin/applications?status=Live")).StatusCode);
    }

    [Fact]
    public void The_state_machine_has_no_Live_and_reactivation_goes_back_to_Approved()
    {
#pragma warning disable CS0618
        var live = SellerStatus.Live;
#pragma warning restore CS0618
        foreach (var s in Enum.GetValues<SellerStatus>())
        {
            Assert.False(SellerStatusService.CanTransition(s, live), $"{s} -> Live must not exist");
            Assert.False(SellerStatusService.CanTransition(live, s), $"Live -> {s} must not exist");
        }
        Assert.True(SellerStatusService.CanTransition(SellerStatus.Approved, SellerStatus.Suspended));
        Assert.True(SellerStatusService.CanTransition(SellerStatus.Suspended, SellerStatus.Approved));
        Assert.False(SellerStatusService.CanTransition(SellerStatus.Approved, SellerStatus.PendingReview));
    }

    [Fact]
    public async Task Suspended_to_Approved_reactivation_works_through_both_apis_and_restores_access()
    {
        var shop = await Orders.NewShop(f);
        var admin = await Legacy();
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/v1/admin/sellers/{shop.Seller}/status", new { status = "Suspended" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/v1/admin/sellers/{shop.Seller}/status", new { status = "Approved" })).StatusCode);
        var again = new Api(f); await again.LoginAs(shop.Owner.Session!.Email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.OK, (await again.Get("/api/v1/seller/products")).StatusCode);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("Approved", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
    }

    [Fact]
    public async Task Approved_sellers_can_publish_take_orders_and_take_bookings()
    {
        var shop = await Orders.NewShop(f);                       // approval -> activating a listing works
        Assert.Equal("Active", (await ProdApi.Get(shop.Owner, shop.Product))["status"]!.ToString());
        var (_, orderId, _, _) = await Orders.Place(shop);        // the marketplace can order from it
        Assert.NotEqual(Guid.Empty, orderId);
        var biz = await Svc.NewBiz(f);                            // service listing activated, availability + booking work
        var (_, bookingId, _) = await Svc.Book(biz);
        Assert.NotEqual(Guid.Empty, bookingId);
        // an unapproved seller cannot publish (still true)
        var (draft, _) = await ProdApi.Approved(f, approve: false);
        var p = await ProdApi.Create(draft);
        Assert.Equal(HttpStatusCode.Forbidden, (await draft.Post($"/api/v1/seller/products/{(Guid)p["id"]!}/status", new { status = "active" })).StatusCode);
    }

    [Fact]
    public async Task Legacy_Live_rows_are_converted_to_Approved_by_the_migration_sql_and_at_startup()
    {
        // 1. the exact SQL the EF migration runs
        var shop = await Orders.NewShop(f);
        f.WithDb(db => { db.Database.ExecuteSqlRaw("UPDATE seller_accounts SET \"Status\" = 'Live' WHERE LOWER(\"Id\") = {0}", shop.Seller.ToString().ToLowerInvariant()); return 0; });
        Assert.Equal("Live", f.WithDb(db => db.Database.SqlQueryRaw<string>("SELECT \"Status\" AS \"Value\" FROM seller_accounts WHERE LOWER(\"Id\") = {0}", shop.Seller.ToString().ToLowerInvariant()).ToList().Single()));
        var changed = f.WithDb(db => LegacyStatus.NormalizeAsync(db).Result);
        Assert.True(changed >= 1);
        Assert.Equal("Approved", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        Assert.Equal(0, f.WithDb(db => LegacyStatus.NormalizeAsync(db).Result)); // idempotent
        // the migration class uses that very statement
        var src = File.ReadAllText(Directory.GetFiles(FindMigrationsDir(), "*_RemoveLiveStatus.cs").Single());
        Assert.Contains("LegacyStatus.ConvertLiveSql", src);

        // 2. start-up: a database that still holds a Live row is fixed when the app boots (dev/SQLite databases are created, not migrated)
        var seller2 = (await Orders.NewShop(f)).Seller;
        f.WithDb(db => { db.Database.ExecuteSqlRaw("UPDATE seller_accounts SET \"Status\" = 'Live' WHERE LOWER(\"Id\") = {0}", seller2.ToString().ToLowerInvariant()); return 0; });
        f.WithDb(db => { db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)"); return 0; }); // SQLite runs in WAL mode: flush before copying the file
        var copy = Path.Combine(Path.GetTempPath(), "live-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(Path.Combine(f.Dir, "t.db"), copy);
        try
        {
            using var boot = new ApiFactory();
            boot.Extra["Database:ConnectionString"] = $"Data Source={copy}";
            Assert.Equal("Approved", boot.WithDb(db => db.Sellers.Single(s => s.Id == seller2).Status.ToString()));
        }
        finally { try { File.Delete(copy); } catch { /* ignore */ } }
    }

    private static string FindMigrationsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Solvelt.Api", "Migrations"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "src", "Solvelt.Api", "Migrations");
    }
}
