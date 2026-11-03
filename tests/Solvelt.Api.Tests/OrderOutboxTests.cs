using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class OrderOutboxSinkTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Rejected_actions_write_no_outbox_row_and_accepted_ones_write_exactly_one_in_the_same_commit()
    {
        var s = await NewShop(f);
        var (mid, id, _, _) = await Place(s);
        Assert.Empty(OutboxFor(f, mid));                                          // buyer-originated: nothing goes back
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "ship", Ship())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(s.Owner, id, "cancel", new { reason = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(s.Owner, id, "accept", new { version = 99 })).StatusCode);
        Assert.Empty(OutboxFor(f, mid));
        await Ok(Act(s.Owner, id, "accept"));
        var rows = OutboxFor(f, mid);
        Assert.Single(rows);
        Assert.Equal(OutboxStatus.Pending, rows[0].Status);
        // the state change and its event are in the same commit: the timeline entry exists too
        Assert.Contains((await Detail(s.Owner, id))["timeline"]!.AsArray(), e => (string)e!["type"]! == "order.accepted");
    }

    [Fact]
    public async Task Dispatch_delivers_signed_events_to_the_dev_sink_with_a_separate_secret()
    {
        var s = await NewShop(f);
        var (mid, id, number, _) = await Place(s);
        await Ok(Act(s.Owner, id, "accept"));
        var http = f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/dev/outbox/dispatch", null)).StatusCode);
        Assert.All(OutboxFor(f, mid), m => Assert.Equal(OutboxStatus.Delivered, m.Status));
        var sink = JsonNode.Parse(await (await http.GetAsync("/api/dev/outbox")).Content.ReadAsStringAsync())!.AsArray();
        var hit = sink.First(x => ((string)x!["body"]!).Contains(mid))!;
        var body = (string)hit["body"]!; var ts = (string)hit["timestamp"]!; var sig = (string)hit["signature"]!;
        Assert.Equal(Signing.SignOutbound(OrdersFactory.OutSecret, ts, body), sig);
        Assert.NotEqual(Signing.SignOutbound(OrdersFactory.InSecret, ts, body), sig);   // the inbound secret cannot forge outbound events
        var ev = JsonNode.Parse(body)!;
        Assert.Equal("order.accepted", (string)ev["type"]!); Assert.Equal(number, (string)ev["data"]!["order"]!["sellerOrderNumber"]!);
        Assert.Equal(mid, (string)ev["data"]!["order"]!["marketplaceOrderId"]!); Assert.NotNull(ev["id"]); Assert.NotNull(ev["createdAt"]);
    }

    [Fact]
    public void Inbound_and_outbound_secrets_must_differ_and_are_required_in_production()
    {
        // both rules are enforced at start-up (OrdersModule.AddOrders); simulate the configuration check with the production host builder
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Orders:InboundSecret"] = "same-secret-value", ["Orders:OutboundSecret"] = "same-secret-value" });
        Assert.Throws<InvalidOperationException>(() => { b.AddOrders(); });
        var b2 = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        Assert.Throws<InvalidOperationException>(() => { b2.AddOrders(); });
    }
}

public class OrderOutboxWebhookTests : IClassFixture<WebhookFactory>
{
    private readonly WebhookFactory _f;
    public OrderOutboxWebhookTests(WebhookFactory f) { _f = f; f.Extra["Orders:OutboxBackoffSeconds"] = "1"; }

    private async Task<int> Dispatch() => (int)(await Node(await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)))["delivered"]!;
    private OutboxMessage Row(Guid id) => _f.WithDb(db => db.Outbox.AsNoTracking().First(m => m.Id == id));

    [Fact]
    public async Task Webhook_failures_back_off_exponentially_then_dead_letter_and_admins_can_retry()
    {
        var s = await NewShop(_f);
        var (mid, id, _, _) = await Place(s);
        await Dispatch();   // flush anything pending from other tests so the counts below are about this order
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await Ok(Act(s.Owner, id, "accept"));
        var msg = OutboxFor(_f, mid).Single();
        var delays = new List<double>();
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await Dispatch();
            var r = Row(msg.Id);
            Assert.Equal(attempt, r.Attempts);
            if (attempt < 8)
            {
                Assert.Equal(OutboxStatus.Pending, r.Status);
                delays.Add((r.NextAttemptAt - _f.Time.GetUtcNow().UtcDateTime).TotalSeconds);
                Assert.Equal(0, await Dispatch());               // not due yet: no extra attempt
                Assert.Equal(attempt, Row(msg.Id).Attempts);
                _f.Time.Advance(TimeSpan.FromSeconds(delays[^1] + 1));
            }
        }
        Assert.Equal([1, 2, 4, 8, 16, 32, 64], delays.Select(d => Math.Round(d)).ToArray());   // exponential
        var dead = Row(msg.Id);
        Assert.Equal(OutboxStatus.Dead, dead.Status); Assert.Equal("HTTP 500", dead.LastError);

        // dead letters are visible to platform admins only
        var legacyAdmin = new Api(_f); await legacyAdmin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacyAdmin.Get("/api/v1/admin/outbox?status=dead")).StatusCode); // seller-kind session: not staff
        var admin = await StaffKit.LoginNew(_f, Solvelt.Api.Admin.StaffRole.Support);
        var list = await Node(await admin.Get("/api/v1/admin/outbox?status=dead"));
        Assert.Contains(list["items"]!.AsArray(), x => (string)x!["id"]! == msg.Id.ToString());
        Assert.True((int)list["counts"]!["dead"]! >= 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Get("/api/v1/admin/outbox")).StatusCode);

        // recovery: the marketplace is back, an admin re-queues the message
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacyAdmin.Post($"/api/v1/admin/outbox/{msg.Id}/retry", new { reason = "Marketplace is back" })).StatusCode); // the seller-kind session cannot retry
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.Post($"/api/v1/admin/outbox/{msg.Id}/retry", new { })).StatusCode); // a reason is required
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post($"/api/v1/admin/outbox/{msg.Id}/retry", new { reason = "Marketplace is back" })).StatusCode);
        Assert.Contains(_f.WithDb(db => db.AuditEvents.Where(e => e.Type == "admin.outbox.retried").ToList()), e => e.TargetId == msg.Id.ToString());
        await Dispatch();
        Assert.Equal(OutboxStatus.Delivered, Row(msg.Id).Status);
    }

    [Fact]
    public async Task Webhook_requests_are_signed_carry_ids_and_network_errors_are_retried()
    {
        var s = await NewShop(_f);
        var (mid, id, _, _) = await Place(s);
        await Dispatch();
        _f.Handler = _ => throw new HttpRequestException("connection refused");
        await Ok(Act(s.Owner, id, "accept"));
        await Dispatch();
        var msg = OutboxFor(_f, mid).Single();
        Assert.Equal(1, msg.Attempts); Assert.Equal(OutboxStatus.Pending, msg.Status); Assert.Equal("HttpRequestException", msg.LastError);
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        _f.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await Dispatch() >= 1);
        (HttpRequestMessage Req, string Body) call;
        lock (_f.Calls) call = _f.Calls.Last(c => c.Body.Contains(mid));
        Assert.Equal("https://marketplace.test/hooks/solvelt", call.Req.RequestUri!.ToString());
        var ts = call.Req.Headers.GetValues(Signing.TimestampHeader).Single();
        Assert.Equal(Signing.SignOutbound(OrdersFactory.OutSecret, ts, call.Body), call.Req.Headers.GetValues(Signing.SignatureHeader).Single());
        Assert.Equal(msg.Id.ToString(), call.Req.Headers.GetValues(Signing.EventIdHeader).Single());
        Assert.Equal("order.accepted", call.Req.Headers.GetValues(Signing.EventTypeHeader).Single());
        Assert.Equal(OutboxStatus.Delivered, Row(msg.Id).Status);
    }
}
