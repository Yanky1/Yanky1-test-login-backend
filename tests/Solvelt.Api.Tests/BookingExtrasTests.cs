using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class BookingProductionTests : IClassFixture<ProductionFactory>
{
    private readonly ProductionFactory _f;
    public BookingProductionTests(ProductionFactory f) => _f = f;

    [Fact]
    public async Task In_production_the_booking_simulator_and_job_trigger_do_not_exist_but_the_signed_booking_api_does()
    {
        var c = _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/seller/dev/bookings", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/seller/dev/quotes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/seller/dev/services/{Guid.NewGuid()}/slots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/internal/v1/bookings", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/internal/v1/services/{Guid.NewGuid()}/availability?from=2030-01-01&to=2030-01-02")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/calendar/feeds/ics_" + new string('x', 43) + ".ics")).StatusCode);
    }
}

public class BookingMessagingTests : IClassFixture<SvcFactory>
{
    private readonly SvcFactory _f;
    public BookingMessagingTests(SvcFactory f) => _f = f;

    [Fact]
    public async Task Buyer_and_team_exchange_messages_on_a_booking_and_the_thread_is_scoped_idempotent_and_sent_through_the_outbox()
    {
        var b = await Svc.NewBiz(_f);
        var (mid, id, _) = await Svc.Book(b);
        var m1 = "M-" + Guid.NewGuid().ToString("N")[..10];
        var inbound = await Svc.Post(b, $"/bookings/{mid}/messages", new { messageId = m1, text = "Can you bring a ladder?" });
        Assert.Equal(HttpStatusCode.Created, inbound.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Svc.Post(b, $"/bookings/{mid}/messages", new { messageId = m1, text = "Can you bring a ladder?" })).StatusCode);   // duplicate
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Svc.Post(b, $"/bookings/{mid}/messages", new { messageId = m1 + "-2", text = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Svc.Post(b, "/bookings/nope/messages", new { messageId = m1 + "-3", text = "hi" })).StatusCode);
        var thread = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/bookings/{id}/messages"));
        Assert.Single(thread["messages"]!.AsArray());
        var reply = await b.Owner.Post($"/api/v1/seller/bookings/{id}/messages", new { text = "Yes, I'll bring one." });
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        var ev = Svc.Events(_f, b.Seller, "message.sent").Single().Data;
        Assert.Equal("booking", (string)ev["kind"]!); Assert.Equal(mid, (string)ev["booking"]!["marketplaceBookingId"]!);
        Assert.Equal(2, (await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/bookings/{id}/messages")))["messages"]!.AsArray().Count);
        // other accounts: 404; a viewer reads but cannot write; an empty message is invalid
        var (other, _) = await ProdApi.Approved(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Get($"/api/v1/seller/bookings/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/seller/bookings/{id}/messages", new { text = "x" })).StatusCode);
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.Get($"/api/v1/seller/bookings/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/seller/bookings/{id}/messages", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Owner.Post($"/api/v1/seller/bookings/{id}/messages", new { text = " " })).StatusCode);
        // unsigned inbound messages are refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().SendAsync(Orders.Signed(_f, "POST", $"/api/internal/v1/bookings/{mid}/messages", new { messageId = m1 + "-9", text = "x" }, sign: false))).StatusCode);
    }

    [Fact]
    public async Task Message_texts_are_removed_when_the_booking_is_redacted()
    {
        var b = await Svc.NewBiz(_f);
        var (mid, id, start) = await Svc.Book(b);
        await Svc.Post(b, $"/bookings/{mid}/messages", new { messageId = "M-" + Guid.NewGuid().ToString("N")[..8], text = "My door code is 1234" });
        await Svc.Act(b.Owner, id, "cancel", new { reason = "x" });
        _f.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/dev/bookings/maintenance/run", null)).StatusCode);
        var thread = await ProdApi.Node(await b.Owner.Get($"/api/v1/seller/bookings/{id}/messages"));
        Assert.DoesNotContain("1234", thread.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, (await b.Owner.Post($"/api/v1/seller/bookings/{id}/messages", new { text = "late" })).StatusCode);
        _ = start;
    }
}
