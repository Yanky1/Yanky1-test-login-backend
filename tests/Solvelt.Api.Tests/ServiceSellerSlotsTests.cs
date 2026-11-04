using System.Net;

namespace Solvelt.Api.Tests;

public class ServiceSellerSlotsTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public ServiceSellerSlotsTests(SvcFactory f) => _f = f;

    [Fact]
    public async Task Seller_slot_picker_returns_the_same_times_the_marketplace_would_book_and_is_scoped_to_the_sellers_own_listing()
    {
        var b = await Svc.NewBiz(_f);
        var from = Svc.Next(_f, DayOfWeek.Tuesday); var to = from.AddDays(1);
        var market = await Svc.Slots(b, from, to);
        var res = await b.Owner.Get($"/api/v1/seller/services/{b.Service}/slots?from={Svc.Iso(from)}&to={Svc.Iso(to)}&location=customer&postalCode=11201");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var mine = await Orders.Node(res);
        var a = market["slots"]!.AsArray().Select(s => s!["startAt"]!.GetValue<DateTime>()).ToList(); var m = mine["slots"]!.AsArray().Select(s => s!["startAt"]!.GetValue<DateTime>()).ToList();
        Assert.NotEmpty(m); Assert.Equal(a, m);

        // another seller cannot read this listing's slots; a missing range is a validation error, not a 500
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/services/{b.Service}/slots?from={Svc.Iso(from)}&to={Svc.Iso(to)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Get($"/api/v1/seller/services/{b.Service}/slots")).StatusCode);
    }
}

public class ServiceMoveSlotsTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public ServiceMoveSlotsTests(SvcFactory f) => _f = f;

    private async Task<(Api api, Guid member)> Provider(Biz b, string role, Guid[]? brands = null)
    {
        var (api, email) = await TeamHelpers.JoinAs(_f, b.Owner, role, brands);
        var mem = Svc.MemberOf(_f, b.Seller, email);
        await Svc.MakeProvider(b.Owner, mem);
        await Svc.SetHours(b.Owner, mem, Svc.WeekdayHours());
        Assert.Equal(HttpStatusCode.OK, (await Svc.SetProviders(b.Owner, b.Service, b.OwnerMember, mem)).StatusCode);
        return (api, mem);
    }

    private static string Url(Guid service, DateTime from, DateTime to, string extra = "") => $"/api/v1/seller/services/{service}/slots?from={Svc.Iso(from)}&to={Svc.Iso(to)}{extra}";
    private static List<DateTime> Starts(System.Text.Json.Nodes.JsonNode n) => n["slots"]!.AsArray().Select(s => s!["startAt"]!.GetValue<DateTime>()).ToList();

    [Fact]
    public async Task A_provider_viewer_cannot_read_the_owners_free_busy_and_a_provider_scoped_to_another_brand_can_still_move_their_own_booking()
    {
        var b = await Svc.NewBiz(_f);
        var wed = Svc.Next(_f, DayOfWeek.Wednesday);
        var from = wed; var to = wed.AddDays(1);
        var (viewer, vMember) = await Provider(b, "Viewer");
        // the same rule as providers/{id}/availability: somebody else's calendar is a 404, with or without the providerId
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Get($"/api/v1/seller/providers/{b.OwnerMember}/availability")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Get(Url(b.Service, from, to, $"&providerId={b.OwnerMember}&location=online"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Get(Url(b.Service, from, to, "&location=online"))).StatusCode);
        var (_, ownersBooking, _) = await Svc.Book(b);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Get(Url(b.Service, from, to, $"&bookingId={ownersBooking}"))).StatusCode);
        // their own calendar is fine
        var own = await viewer.Get(Url(b.Service, from, to, $"&providerId={vMember}&location=online"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // an assigned provider whose role is scoped to ANOTHER brand: the listing is out of their brand scope, but the booking is theirs to move
        var brand2 = await TeamHelpers.NewBrand(b.Owner, "Second brand");
        var (scoped, sMember) = await Provider(b, "Manager", [brand2]);
        var theirs = await Orders.Node(await Svc.Post(b, "/bookings", Svc.Body(b, Svc.BookingId(), Svc.Local(b.Tz, wed, 14), provider: sMember)));
        var theirId = Guid.Parse((string)theirs["booking"]!["bookingId"]!);
        var res = await scoped.Get(Url(b.Service, from, to, $"&bookingId={theirId}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var node = await Orders.Node(res);
        Assert.NotEmpty(Starts(node));
        Assert.All(node["slots"]!.AsArray(), s => Assert.Equal(sMember.ToString(), (string)s!["providerIds"]![0]!));   // only their own provider id
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.Get(Url(b.Service, from, to, $"&bookingId={ownersBooking}"))).StatusCode);
    }

    [Fact]
    public async Task Move_slots_use_the_bookings_own_duration_with_add_ons_and_may_overlap_its_current_slot()
    {
        var b = await Svc.NewBiz(_f, d => { d["slotGranularityMinutes"] = 30; d["incrementMinutes"] = 60; });
        var (_, id, start) = await Svc.Book(b, 10, addOns: ["deep"]);         // 120 min + the 30-minute add-on = 150
        var res = await b.Owner.Get(Url(b.Service, Svc.Local(b.Tz, start.Date, 0), Svc.Local(b.Tz, start.Date.AddDays(1), 0), $"&bookingId={id}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var node = await Orders.Node(res); var starts = Starts(node);
        Assert.Equal(150, (int)node["durationMinutes"]!);
        Assert.Contains(start, starts);                       // its own start is free: the booking does not block itself
        Assert.Contains(start.AddMinutes(30), starts);
        Assert.DoesNotContain(Svc.Local(b.Tz, start.Date, 15), starts);   // 15:00 + 150 min would end after the 17:00 close
        // ... and the server accepts what the picker offered
        Assert.Equal(HttpStatusCode.OK, (await Svc.Act(b.Owner, id, "reschedule", new { start = start.AddMinutes(30) })).StatusCode);
    }
}
