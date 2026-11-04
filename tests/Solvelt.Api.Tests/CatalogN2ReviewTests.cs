using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>Brief N6: the review of C5/C6 (rejected-request audit rows are bounded and expire) and the coverage gaps it found.</summary>
public class RejectedRequestAuditTests
{
    private static Task<HttpResponseMessage> Unsigned(ApiFactory f, string path) =>
        f.NewClient().SendAsync(Orders.Signed(f, "POST", path, new { questionId = "x", text = "y" }, sign: false));

    private static int Rows(ApiFactory f) => f.WithDb(db => db.AuditEvents.Count(e => e.Type == "inbound.rejected"));

    [Fact]
    public async Task A_flood_of_unsigned_requests_writes_a_bounded_number_of_audit_rows_and_is_still_answered_with_429()
    {
        using var f = new CatalogTightLimitsFactory();                                          // 4 failed requests per IP and minute
        var s = await Orders.NewShop(f);
        var path = $"/api/internal/v1/products/{s.Product}/questions";
        var before = Rows(f);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 200; i++) codes.Add((await Unsigned(f, path)).StatusCode);
        Assert.Equal(196, codes.Count(c => c == HttpStatusCode.TooManyRequests));                // the flood is still refused
        Assert.Equal(4, codes.Count(c => c == HttpStatusCode.Unauthorized));
        var rows = Rows(f) - before;
        Assert.Equal(20, rows);                                                                  // Orders:InboundRejectedAuditPerIpPerMinute (20), not one row per request (200)
    }

    [Fact]
    public async Task The_audit_budget_is_per_minute_and_a_signed_caller_on_the_same_address_is_never_refused_by_it()
    {
        using var f = new CatalogTightLimitsFactory();
        var s = await Orders.NewShop(f);
        var path = $"/api/internal/v1/products/{s.Product}/questions";
        for (var i = 0; i < 30; i++) await Unsigned(f, path);
        var first = Rows(f);
        f.Time.Advance(TimeSpan.FromSeconds(61));                                                // the next minute has a fresh budget
        for (var i = 0; i < 30; i++) await Unsigned(f, path);
        Assert.Equal(first + 20, Rows(f));
        // auditing never decides a refusal: the signed caller from the very same address is admitted
        var r = await PostSigned(f, path, new { questionId = "Q-" + Guid.NewGuid().ToString("N"), text = "How big is it?" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Rejected_request_audit_rows_are_deleted_by_the_maintenance_job_after_the_retention_period()
    {
        using var f = new CatalogFactory();
        var now = f.Time.GetUtcNow().UtcDateTime;
        f.WithDb(db =>
        {
            db.AuditEvents.Add(new AuditEvent { Type = "inbound.rejected", Detail = "code=unsigned", At = now.AddDays(-40) });
            db.AuditEvents.Add(new AuditEvent { Type = "inbound.rejected", Detail = "code=unsigned", At = now.AddDays(-1) });
            db.AuditEvents.Add(new AuditEvent { Type = "something.else", Detail = "kept", At = now.AddDays(-400) });     // only rejected-request rows expire here
            db.SaveChanges(); return 0;
        });
        using (var sc = f.Services.CreateScope()) await sc.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync();
        Assert.Equal(1, f.WithDb(db => db.AuditEvents.Count(e => e.Type == "inbound.rejected" && e.Detail == "code=unsigned")));
        Assert.Equal(1, f.WithDb(db => db.AuditEvents.Count(e => e.Type == "something.else")));
    }
}

public class CatalogN2ReviewTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static async Task<(bool Ships, bool LocalDelivery)> Delivery(ApiFactory f, Guid id)
    {
        var d = (await Listing(f, id))!["delivery"]!;
        return ((bool)d["ships"]!, (bool)d["localDelivery"]!);
    }

    private static readonly object[] Zones =
    [
        new { name = "Domestic", kind = "domestic", countries = new[] { "US" }, speeds = new object[] { new { speed = "standard", method = "flat", flatCents = 500 } } }
    ];

    // ---- gap 1: the per-caller ceiling for reports ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_per_caller_ceiling_also_caps_content_reports_across_many_different_targets()
    {
        using var tight = new CatalogTightLimitsFactory();                                        // 3 reports per target, 40 per caller and minute
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 45; i++)
            codes.Add((await PostSigned(tight, "/api/internal/v1/content/reports", new { reportId = "RC-" + Guid.NewGuid().ToString("N"), kind = "review", targetId = "review-" + i, reason = "spam", reporterRef = "u1" })).StatusCode);
        Assert.Equal(40, codes.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    // ---- gap 2: a brand's own enabled setting without an active zone wins over the account-wide one ----------------------------------

    [Fact]
    public async Task A_brands_own_enabled_setting_without_an_active_zone_wins_over_an_enabled_account_wide_setting_with_a_zone()
    {
        var s = await Orders.NewShop(f);
        var brand2 = await TeamHelpers.NewBrand(s.Owner, "Second brand " + Guid.NewGuid().ToString("N")[..5]);
        var other = await Orders.ActiveProduct(s.Owner, "Other brand mug", brand2, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put("/api/v1/seller/delivery/settings", Dlv.Settings(null, enabled: true))).StatusCode);           // account-wide: enabled, one active zone
        Assert.True((await Delivery(f, other)).LocalDelivery);
        var inactive = new object[] { new { name = "Paused", country = "US", originLat = Dlv.HomeLat, originLng = Dlv.HomeLng, radiusKm = 5.0, postcodes = new[] { "11201" }, feeCents = 500, freeOverCents = 10000, minOrderCents = 0, active = false } };
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Put("/api/v1/seller/delivery/settings", Dlv.Settings(brand2, enabled: true, zones: inactive))).StatusCode);   // the brand's own: enabled, but its only zone is inactive
        Assert.False((await Delivery(f, other)).LocalDelivery);                                    // the brand's own setting applies (and offers nothing); it does not fall back to the account-wide one
        Assert.True((await Delivery(f, s.Product)).LocalDelivery);                                 // the other brand still follows the account-wide setting
    }

    // ---- gap 3: a service never ships -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_service_never_ships_even_when_the_account_has_a_shipping_profile_covering_its_brand()
    {
        var b = await Svc.NewBiz(f, d => { d["online"] = true; });
        var r = await b.Owner.Post("/api/v1/seller/shipping/profiles", new { name = "Account-wide " + Guid.NewGuid().ToString("N")[..5], brandId = (Guid?)null, currency = "USD", zones = Zones });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.False((await Delivery(f, b.Service)).Ships);                                        // the profile covers the brand, but a service is booked, not shipped
        var product = await Orders.ActiveProduct(b.Owner, "A mug in the same account", b.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.True((await Delivery(f, product)).Ships);                                           // ... while a product of that brand does ship
    }

    // ---- gap 4: processed refresh requests are deleted after 7 days ------------------------------------------------------------------

    [Fact]
    public async Task Processed_catalog_refresh_requests_are_deleted_after_seven_days_and_unprocessed_ones_are_kept()
    {
        var s = await Orders.NewShop(f);
        var now = f.Time.GetUtcNow().UtcDateTime;
        Guid Add(DateTime? processed) { var id = Guid.NewGuid(); f.WithDb(db => { db.CatalogRefreshRequests.Add(new CatalogRefreshRequest { Id = id, SellerId = s.Seller, BrandId = null, CreatedAt = now.AddDays(-9), ProcessedAt = processed }); db.SaveChanges(); return 0; }); return id; }
        var old = Add(now.AddDays(-8)); var recent = Add(now.AddDays(-1));
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/catalog/refresh", null)).StatusCode);    // runs the job: it also removes old processed rows
        bool Exists(Guid id) => f.WithDb(db => db.CatalogRefreshRequests.Any(r => r.Id == id));
        Assert.False(Exists(old));
        Assert.True(Exists(recent));
    }
}
