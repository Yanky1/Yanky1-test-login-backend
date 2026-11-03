using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>Orders factory whose sessions never time out, so tests can move the fake clock by days and keep calling the seller API.</summary>
public class SvcFactory : OrdersFactory
{
    public SvcFactory()
    {
        Extra["Session:IdleTimeoutMinutes"] = "100000000";
        Extra["Session:RefreshTokenDays"] = "36000";
        Extra["Jwt:AccessTokenMinutes"] = "100000000";
    }
}

public record Biz(ApiFactory F, Api Owner, Guid Seller, Guid Brand, Guid OwnerMember, Guid Service, string Tz);

public static class Svc
{
    public const string Ny = "America/New_York";

    public static Guid MemberOf(ApiFactory f, Guid seller, string? email = null) => f.WithDb(db =>
    {
        var q = from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.SellerId == seller select new { m.Id, m.Role, u.Email };
        return (email is null ? q.First(x => x.Role == AccountRole.Owner) : q.First(x => x.Email == email)).Id;
    });

    /// <summary>The next given weekday at least <paramref name="minDays"/> days after the fake clock, as a UTC date at 00:00.</summary>
    public static DateTime Next(ApiFactory f, DayOfWeek dow, int minDays = 3)
    {
        var d = f.Time.GetUtcNow().UtcDateTime.Date.AddDays(minDays);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d;
    }

    public static DateTime Local(string tz, DateTime date, int h, int m = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(date.Year, date.Month, date.Day, h, m, 0, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(tz));

    public static JsonObject BaseDetails(Action<JsonObject>? tweak = null)
    {
        var d = new JsonObject
        {
            ["pricingModel"] = "hourly", ["price"] = 60, ["minMinutes"] = 60, ["maxMinutes"] = 480, ["incrementMinutes"] = 30, ["defaultDurationMinutes"] = 120,
            ["bufferBeforeMinutes"] = 0, ["bufferAfterMinutes"] = 0, ["minNoticeHours"] = 1, ["maxAdvanceDays"] = 120, ["slotGranularityMinutes"] = 60,
            ["online"] = true, ["onlineInstructions"] = "https://meet.example.test/room-42",
            ["atCustomer"] = true, ["serviceArea"] = new JsonObject { ["postalCodes"] = new JsonArray("11201", "112*") }, ["travelFee"] = 10, ["travelBufferMinutes"] = 30,
            ["atSeller"] = false, ["bookingMode"] = "instant", ["approvalHours"] = 24,
            ["intake"] = new JsonArray(new JsonObject { ["id"] = "pets", ["label"] = "Any pets?", ["type"] = "yesno", ["required"] = false }),
            ["requirements"] = "Clear the area first.", ["addOns"] = new JsonArray(new JsonObject { ["id"] = "deep", ["name"] = "Deep clean", ["price"] = 25, ["extraMinutes"] = 30 }),
            ["cancelFreeHours"] = 24, ["cancelFeePercent"] = 50, ["maxReschedules"] = 2, ["noShowFeePercent"] = 100, ["assignmentRule"] = "buyerChoice"
        };
        tweak?.Invoke(d);
        return d;
    }

    public static async Task SetHours(Api api, Guid member, params (int Day, string Start, string End)[] days)
    {
        var weekly = days.Select(d => new { day = d.Day, intervals = new[] { new { start = d.Start, end = d.End } } }).ToArray();
        var r = await api.Put($"/api/v1/seller/providers/{member}/availability", new { weekly, overrides = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    public static (int, string, string)[] WeekdayHours(string s = "09:00", string e = "17:00") => Enumerable.Range(1, 5).Select(d => (d, s, e)).ToArray();

    public static async Task MakeProvider(Api owner, Guid member, string? tz = Ny)
    {
        var r = await owner.Put($"/api/v1/seller/providers/{member}", new { isProvider = true, timeZone = tz });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    public static async Task<Guid> NewService(Api owner, string title = "Home cleaning service", Guid? brand = null, string? slug = "home-services")
    {
        var cat = await ProdApi.CategoryId(owner, slug ?? "kitchen-dining");
        var r = await owner.Post("/api/v1/seller/products", new { title, brandId = brand, categoryId = cat, type = "service" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = Guid.Parse((string)(await ProdApi.Node(r))["id"]!);
        Assert.Equal(HttpStatusCode.Created, (await ProdApi.Upload(owner, id, TestImages.Png())).StatusCode);
        return id;
    }

    public static async Task<HttpResponseMessage> PutDetails(Api api, Guid id, JsonObject details)
    {
        var cur = await ProdApi.Get(api, id);
        details["version"] = (int)cur["version"]!;
        return await api.Put($"/api/v1/seller/services/{id}/details", JsonSerializer.Deserialize<JsonElement>(details.ToJsonString()));
    }

    public static Task<HttpResponseMessage> SetProviders(Api api, Guid service, params Guid[] members) => api.Put($"/api/v1/seller/services/{service}/providers", new { membershipIds = members });

    /// <summary>An approved account with a provider (the owner, Mon-Fri 9-17 New York), and an ACTIVE hourly service.</summary>
    public static async Task<Biz> NewBiz(ApiFactory f, Action<JsonObject>? tweak = null, string tz = Ny, bool activate = true, bool hours = true)
    {
        var (api, seller) = await ProdApi.Approved(f);
        var brand = await TeamHelpers.BrandId(api);
        var owner = MemberOf(f, seller);
        await MakeProvider(api, owner, tz);
        if (hours) await SetHours(api, owner, WeekdayHours());
        var id = await NewService(api, brand: brand);
        var r = await PutDetails(api, id, BaseDetails(tweak));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetProviders(api, id, owner)).StatusCode);
        if (activate) Assert.Equal(HttpStatusCode.OK, (await api.Post($"/api/v1/seller/products/{id}/status", new { status = "active" })).StatusCode);
        return new(f, api, seller, brand, owner, id, tz);
    }

    // ---- marketplace side (signed requests) ---------------------------------------------------------------------------------------
    public static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

    public static async Task<JsonNode> Slots(Biz b, DateTime from, DateTime to, string extra = "&location=customer&postalCode=11201")
    {
        var res = await b.F.NewClient().SendAsync(Orders.Signed(b.F, "GET", $"/api/internal/v1/services/{b.Service}/availability?from={Iso(from)}&to={Iso(to)}{extra}", null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await Orders.Node(res);
    }

    public static object Body(Biz b, string mid, DateTime start, string location = "customer", Guid? provider = null, int? minutes = null, string[]? addOns = null, string postal = "11201", object[]? answers = null) => new
    {
        marketplaceBookingId = mid, serviceId = b.Service, providerId = provider ?? b.OwnerMember, start, durationMinutes = minutes, location, addOnIds = addOns,
        buyer = new { displayName = "Jane Buyer", relayEmail = "relay-j@relay.solvelt.example", phone = "+1 555 010 0000" },
        address = new { name = "Jane Buyer", line1 = "1 Main St", city = "Brooklyn", region = "NY", postalCode = postal, country = "US" }, intakeAnswers = answers
    };

    public static string BookingId() => "BK-" + Guid.NewGuid().ToString("N")[..12];

    public static Task<HttpResponseMessage> Post(Biz b, string path, object body) => b.F.NewClient().SendAsync(Orders.Signed(b.F, "POST", "/api/internal/v1" + path, body));

    /// <summary>Instant booking at the given local time on a future Wednesday; returns (marketplace id, booking id).</summary>
    public static async Task<(string Mid, Guid Id, DateTime Start)> Book(Biz b, int hourLocal = 10, int dayOffsetWeeks = 0, string location = "customer", int? minutes = null, string[]? addOns = null, int minDays = 3)
    {
        var date = Next(b.F, DayOfWeek.Wednesday, minDays).AddDays(7 * dayOffsetWeeks);
        var start = Local(b.Tz, date, hourLocal);
        var mid = BookingId();
        var r = await Post(b, "/bookings", Body(b, mid, start, location, minutes: minutes, addOns: addOns));
        Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var id = Guid.Parse((string)(await Orders.Node(r))["booking"]!["bookingId"]!);
        return (mid, id, start);
    }

    public static async Task<JsonNode> Detail(Api api, Guid id)
    {
        var r = await api.Get($"/api/v1/seller/bookings/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ProdApi.Node(r);
    }

    public static Task<HttpResponseMessage> Act(Api api, Guid id, string action, object? body = null) => api.Post($"/api/v1/seller/bookings/{id}/{action}", body ?? new { });

    public static List<(string Type, JsonNode Data)> Events(ApiFactory f, Guid seller, string? type = null) => f.WithDb(db =>
        db.Outbox.AsNoTracking().Where(m => m.SellerId == seller && (type == null || m.EventType == type)).OrderBy(m => m.CreatedAt).ToList()
            .Select(m => (m.EventType, JsonNode.Parse(m.PayloadJson)!["data"]!)).ToList());

    public static Booking Row(ApiFactory f, Guid id) => f.WithDb(db => db.Bookings.AsNoTracking().First(b => b.Id == id));
}
