using System.Net;

namespace Solvelt.Api.Tests;

public class SellerSlotsTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public SellerSlotsTests(SvcFactory f) => _f = f;

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
