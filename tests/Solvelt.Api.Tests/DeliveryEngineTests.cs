using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Delivery;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;
using static Solvelt.Api.Tests.Dlv;

namespace Solvelt.Api.Tests;

public class DeliveryEngineTests
{
    private static DeliveryZone Zone(Action<DeliveryZone>? edit = null)
    {
        var z = new DeliveryZone { Name = "Z", Country = "US", OriginLat = 40.6928, OriginLng = -73.9903, RadiusKm = 5, PostcodesJson = "[\"10001\",\"112*\"]", FeeCents = 500, FreeOverCents = 10000 };
        edit?.Invoke(z); return z;
    }
    private static DeliveryAddress Addr(string pc = "99999", double? lat = null, double? lng = null, string c = "US") => new(pc, c, lat, lng);

    [Fact]
    public void Radius_postcode_prefix_and_polygon_decide_eligibility()
    {
        var z = Zone();
        Assert.True(ZoneEngine.Matches(z, Addr(lat: 40.70, lng: -73.99)));                    // ~0.8 km from the origin
        Assert.False(ZoneEngine.Matches(z, Addr(lat: 40.80, lng: -73.99)));                   // ~12 km: outside the 5 km radius
        Assert.True(ZoneEngine.Matches(z, Addr("100 01")));                                   // exact postcode, spaces ignored
        Assert.True(ZoneEngine.Matches(z, Addr("11299")));                                    // 112* prefix
        Assert.False(ZoneEngine.Matches(z, Addr("11399")));
        Assert.False(ZoneEngine.Matches(z, Addr("10001", c: "CA")));                           // wrong country
        var poly = Zone(x => { x.RadiusKm = null; x.PostcodesJson = "[]"; x.PolygonJson = "[[40,-74],[40,-73],[41,-73],[41,-74]]"; });
        Assert.True(ZoneEngine.Matches(poly, Addr(lat: 40.5, lng: -73.5)));
        Assert.False(ZoneEngine.Matches(poly, Addr(lat: 42, lng: -73.5)));
        Assert.False(ZoneEngine.Matches(Zone(x => x.Active = false), Addr("10001")));
    }

    [Fact]
    public void Fee_rules_flat_free_over_threshold_and_minimum_order()
    {
        var z = Zone(x => x.MinOrderCents = 2000);
        Assert.Equal("below_minimum", ZoneEngine.Evaluate([z], Addr("10001"), 1999).Reason);
        var ok = ZoneEngine.Evaluate([z], Addr("10001"), 2000);
        Assert.Equal(500, ok.FeeCents);
        Assert.Equal(0, ZoneEngine.Evaluate([z], Addr("10001"), 10000).FeeCents);              // free over the threshold
        Assert.Equal("outside_zone", ZoneEngine.Evaluate([z], Addr("90210"), 5000).Reason);
        var cheap = Zone(x => { x.Name = "cheap"; x.FeeCents = 100; x.Priority = 1; });         // lowest priority number wins, then fee
        Assert.Equal("Z", ZoneEngine.Evaluate([cheap, Zone()], Addr("10001"), 500).Zone!.Name);
    }

    private static DeliverySetting Setting(int? cutoff = 14 * 60, int lead = 60) => new()
    {
        Enabled = true, TimeZone = "UTC", SameDayCutoffMinutes = cutoff, MinLeadMinutes = lead, MaxDaysAhead = 3,
        Windows = [new DeliveryWindow { StartMinutes = 12 * 60, EndMinutes = 14 * 60, Capacity = 2, DaysMask = 0b0111110 }]
    };

    [Fact]
    public void Same_day_cutoff_lead_time_days_and_horizon_apply()
    {
        var w = Setting().Windows[0];
        var morning = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);                      // Wednesday
        Assert.NotNull(WindowEngine.Offer(Setting(), w, Today, morning, 0));
        Assert.Null(WindowEngine.Offer(Setting(), w, Today, new DateTime(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc), 0));          // cutoff passed
        Assert.Null(WindowEngine.Offer(Setting(cutoff: null), w, Today, morning, 0));                                                // no same-day delivery at all
        Assert.Null(WindowEngine.Offer(Setting(), w, Today, new DateTime(2026, 11, 4, 11, 30, 0, DateTimeKind.Utc), 0));          // starts in 30 min, lead is 60
        Assert.Null(WindowEngine.Offer(Setting(), w, new DateOnly(2026, 11, 7), morning, 0));                                       // Saturday: not a delivery day
        Assert.Null(WindowEngine.Offer(Setting(), w, new DateOnly(2026, 11, 9), morning, 0));                                       // beyond max days ahead
        Assert.Null(WindowEngine.Offer(Setting(), w, new DateOnly(2026, 11, 3), morning, 0));                                       // yesterday
        Assert.Equal(0, WindowEngine.Offer(Setting(), w, Tomorrow, morning, 2)!.Remaining);
    }

    [Fact]
    public void Time_zones_and_dst_gaps_are_respected()
    {
        var tz = WindowEngine.Zone("America/New_York");
        Assert.Equal(new DateTime(2026, 11, 4, 17, 0, 0), WindowEngine.ToUtc(tz, Today, 12 * 60));          // EST: UTC-5
        Assert.Equal(new DateTime(2026, 7, 1, 16, 0, 0), WindowEngine.ToUtc(tz, new DateOnly(2026, 7, 1), 12 * 60));  // EDT: UTC-4
        Assert.Null(WindowEngine.ToUtc(tz, new DateOnly(2026, 3, 8), 2 * 60 + 30));                          // 02:30 does not exist on spring-forward day
    }

    [Fact]
    public async Task Parallel_bookings_never_exceed_the_window_capacity()
    {
        using var f = new DeliveryFactory();
        await NewShop(f);   // creates the database
        var window = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CapacityService>().TryBookAsync(window, Tomorrow, 5);
        })));
        Assert.Equal(5, results.Count(x => x));
        Assert.Equal(5, f.WithDb(db => db.DeliveryWindowUsage.Single(u => u.WindowId == window).Booked));
        using (var scope = f.Services.CreateScope()) { var c = scope.ServiceProvider.GetRequiredService<CapacityService>(); await c.ReleaseAsync(window, Tomorrow); Assert.True(await c.TryBookAsync(window, Tomorrow, 5)); }
    }

    [Fact]
    public async Task Nearest_neighbour_orders_stops_from_the_start_and_the_fake_is_deterministic()
    {
        var a = new StopPoint(Guid.NewGuid(), 40.70, -73.99); var b = new StopPoint(Guid.NewGuid(), 40.75, -73.99); var c = new StopPoint(Guid.NewGuid(), 40.72, -73.99);
        var o = new NearestNeighbourOptimizer();
        Assert.Equal([a.Id, c.Id, b.Id], await o.OrderAsync(40.69, -73.99, [b, a, c]));
        Assert.Equal([b.Id, c.Id, a.Id], await o.OrderAsync(40.76, -73.99, [a, b, c]));
        var eta = Eta.ForStops(40.69, -73.99, [(40.70, -73.99), (null, null), (40.71, -73.99)], new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(eta[0]); Assert.Null(eta[1]); Assert.True(eta[2] > eta[0]);
    }

    [Fact]
    public void Images_lose_exif_and_text_metadata_and_only_jpeg_png_are_accepted()
    {
        var (jpeg, type) = ImageSanitizer.Clean(Jpeg("GPS-LAT-40.69"));
        Assert.Equal("image/jpeg", type);
        Assert.DoesNotContain("Exif", System.Text.Encoding.ASCII.GetString(jpeg!)); Assert.DoesNotContain("GPS", System.Text.Encoding.ASCII.GetString(jpeg!));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA }, jpeg!.Take(4).ToArray());
        byte[] Chunk(string t, byte[] d) => [.. new byte[] { 0, 0, 0, (byte)d.Length }, .. System.Text.Encoding.ASCII.GetBytes(t), .. d, 0, 0, 0, 0];
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", new byte[13]), .. Chunk("eXIf", [1, 2, 3, 4]), .. Chunk("tEXt", [65, 66]), .. Chunk("IDAT", [9, 9]), .. Chunk("IEND", [])];
        var (cleanPng, ptype) = ImageSanitizer.Clean(png);
        Assert.Equal("image/png", ptype);
        var text = System.Text.Encoding.ASCII.GetString(cleanPng!);
        Assert.Contains("IDAT", text); Assert.DoesNotContain("eXIf", text); Assert.DoesNotContain("tEXt", text);
        Assert.Null(ImageSanitizer.Clean("<html><script>alert(1)</script></html>"u8.ToArray()).Data);
        Assert.Null(ImageSanitizer.Clean([0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF]).Data);          // truncated segment
    }
}
