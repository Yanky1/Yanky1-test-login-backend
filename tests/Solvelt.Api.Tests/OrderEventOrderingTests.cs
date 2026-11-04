using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>C14: the events of one seller order (or booking) are delivered in the order they were written, and <c>order.cancelled</c> names the refunds it already accounts for.</summary>
public class OrderEventOrderingTests : IClassFixture<WebhookFactory>
{
    private readonly WebhookFactory _f;
    public OrderEventOrderingTests(WebhookFactory f) { _f = f; f.Extra["Orders:OutboxBackoffSeconds"] = "1"; }

    private async Task<int> Dispatch() => (int)(await Node(await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)))["delivered"]!;
    private async Task Drain() { for (var i = 0; i < 6; i++) { if (await Dispatch() == 0) break; } }
    private static JsonNode Body(string body) => JsonNode.Parse(body)!;

    /// <summary>Webhook calls (type, order number) that the marketplace received after <paramref name="from"/>, in order; failures count too unless <paramref name="okOnly"/>.</summary>
    private List<(string Type, string Number, JsonNode Body)> Received(int from = 0, bool okOnly = true)
    {
        lock (_f.Calls) return _f.Calls.Skip(from).Select(c => Body(c.Body)).Select(b => ((string)b["type"]!, (string?)b["data"]?["order"]?["sellerOrderNumber"] ?? "", b)).ToList();
    }

    private void FailOnly(string type, string? number = null) =>
        _f.Handler = req => Body(req.Content!.ReadAsStringAsync().Result) is { } b && (string)b["type"]! == type && (number is null || (string?)b["data"]?["order"]?["sellerOrderNumber"] == number)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>An order of 3 units where 2 shipped, accepted and shipped, everything delivered so far.</summary>
    private async Task<(string Mid, Guid Id, string Number, Shop S, Guid Line)> PartlyShipped()
    {
        var s = await NewShop(_f);
        var (mid, id, number, _) = await Place(s, qty: 3, price: 1000, shipping: 500, tax: 90);
        await Ok(Act(s.Owner, id, "accept"));
        var line = await LineId(s.Owner, id);
        await Ok(Act(s.Owner, id, "ship", Ship(line, 2)));
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        await Drain();
        return (mid, id, number, s, line);
    }

    [Fact]
    public async Task A_cancel_is_not_sent_while_an_earlier_refund_of_the_same_seller_order_is_still_undelivered()
    {
        var (mid, id, number, s, line) = await PartlyShipped();
        FailOnly("refund.issued", number);
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));          // the unshipped remainder: order.cancelled, enqueued AFTER the refund
        int before; lock (_f.Calls) before = _f.Calls.Count;
        await Dispatch(); await Dispatch();
        var got = Received(before);
        Assert.Equal(["refund.issued"], got.Where(x => x.Number == number).Select(x => x.Type).Distinct().ToArray());   // only the failing refund was tried; the cancel waits
        var cancel = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.AggregateId == mid && m.EventType == "order.cancelled"));
        Assert.Equal(OutboxStatus.Pending, cancel.Status); Assert.Equal(0, cancel.Attempts);

        // the marketplace recovers: refund first, then the cancel
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        _f.Time.Advance(TimeSpan.FromMinutes(5));
        int mark; lock (_f.Calls) mark = _f.Calls.Count;
        await Drain();
        Assert.Equal(["refund.issued", "order.cancelled"], Received(mark).Where(x => x.Number == number).Select(x => x.Type).ToArray());
    }

    [Fact]
    public async Task A_dead_predecessor_does_not_block_forever_the_successor_is_sent_marked_predecessorDead()
    {
        var (mid, id, number, s, line) = await PartlyShipped();
        FailOnly("refund.issued", number);
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        for (var i = 0; i < 8; i++) { await Dispatch(); _f.Time.Advance(TimeSpan.FromHours(2)); }   // 8 attempts: the refund event is Dead
        var refund = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.AggregateId == mid && m.EventType == "refund.issued"));
        Assert.Equal(OutboxStatus.Dead, refund.Status);
        int mark; lock (_f.Calls) mark = _f.Calls.Count;
        await Drain();
        var cancel = Received(mark).Single(x => x.Type == "order.cancelled" && x.Number == number);
        Assert.True((bool)cancel.Body["predecessorDead"]!);                                       // part of the signed body
        var row = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.AggregateId == mid && m.EventType == "order.cancelled"));
        Assert.Equal(OutboxStatus.Delivered, row.Status);
        Assert.DoesNotContain("predecessorDead", row.PayloadJson);                                // the stored payload is unchanged; the marker is added when sending
        // an order without a dead predecessor never carries it
        var clean = Received().Where(x => x.Type == "order.shipped" && x.Number == number).ToList();
        Assert.All(clean, x => Assert.Null(x.Body["predecessorDead"]));
    }

    [Fact]
    public async Task Cancel_names_the_refunds_it_already_accounts_for_and_their_sum_including_tax_and_shipping()
    {
        var (mid, id, number, s, line) = await PartlyShipped();
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } }, shippingCents = 200 }));
        await Ok(Act(s.Owner, id, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = line, quantity = 1 } } }));
        var refunds = _f.WithDb(db => db.Refunds.AsNoTracking().Where(r => r.OrderId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList());
        Assert.Equal(2, refunds.Count);
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        var c = JsonNode.Parse(OutboxFor(_f, mid).Single(m => m.EventType == "order.cancelled").PayloadJson)!["data"]!;
        Assert.True((bool)c["partial"]!);
        var ids = c["refundIdsAccounted"]!.AsArray().Select(x => Guid.Parse((string)x!)).ToList();
        Assert.Equal(refunds.Select(r => r.Id).OrderBy(x => x), ids.OrderBy(x => x));            // the two refunds, not the cancel's own remainder refund
        Assert.Equal(refunds.Sum(r => r.AmountCents), (long)c["previouslyRefundedCents"]!);
        Assert.True((long)c["previouslyRefundedCents"]! > 2 * 1000);                              // includes line tax and the shipping
    }

    [Fact]
    public async Task A_cancel_without_earlier_refunds_lists_none()
    {
        var s = await NewShop(_f);
        var (mid, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "cancel", new { reason = "other" }));
        var c = JsonNode.Parse(OutboxFor(_f, mid).Single(m => m.EventType == "order.cancelled").PayloadJson)!["data"]!;
        Assert.Empty(c["refundIdsAccounted"]!.AsArray()); Assert.Equal(0, (long)c["previouslyRefundedCents"]!);
    }

    [Fact]
    public async Task Events_of_different_seller_orders_still_flow_while_one_order_is_blocked()
    {
        var (midA, idA, numberA, sA, lineA) = await PartlyShipped();
        var sB = await NewShop(_f);
        var (midB, idB, numberB, _) = await Place(sB);
        FailOnly("refund.issued", numberA);
        await Ok(Act(sA.Owner, idA, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = lineA, quantity = 1 } } }));
        await Ok(Act(sA.Owner, idA, "cancel", new { reason = "other" }));               // A: refund (failing) then cancel (held back)
        await Ok(Act(sB.Owner, idB, "accept"));                                          // B: unrelated order, written after A's events
        int mark; lock (_f.Calls) mark = _f.Calls.Count;
        await Dispatch(); await Dispatch();
        var got = Received(mark);
        Assert.Contains(got, x => x.Type == "order.accepted" && x.Number == numberB);   // no global blocking
        Assert.DoesNotContain(got, x => x.Type == "order.cancelled" && x.Number == numberA);
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        _f.Time.Advance(TimeSpan.FromMinutes(5)); await Drain();
    }

    [Fact]
    public async Task Every_event_of_a_seller_order_gets_the_next_sequence_number_of_its_own_scope()
    {
        var s = await NewShop(_f);
        var (mid, id, _, _) = await Place(s);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship())); await Ok(Act(s.Owner, id, "deliver"));
        var rows = _f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == mid).OrderBy(m => m.CreatedAt).ThenBy(m => m.OrderKey).ToList());
        Assert.True(rows.Count >= 3);
        Assert.Single(rows.Select(r => r.SequenceScope).Distinct());
        Assert.StartsWith("so:", rows[0].SequenceScope);
        Assert.Equal(Enumerable.Range(1, rows.Count).Select(i => (long)i), rows.Select(r => r.OrderKey!.Value));   // 1, 2, 3, ... with no ties
        var (mid2, id2, _, _) = await Place(s);
        await Ok(Act(s.Owner, id2, "accept"));
        var other = _f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == mid2).OrderBy(m => m.OrderKey).ToList());
        Assert.NotEqual(rows[0].SequenceScope, other[0].SequenceScope); Assert.Equal(1, other[0].OrderKey);        // its own sequence
    }

    [Fact]
    public async Task Many_blocked_events_of_one_order_do_not_starve_the_events_of_other_orders_in_a_dispatch_batch()
    {
        var (midA, idA, numberA, sA, lineA) = await PartlyShipped();
        FailOnly("refund.issued", numberA);
        await Ok(Act(sA.Owner, idA, "refunds", new { reason = "goodwill", lines = new[] { new { lineId = lineA, quantity = 1 } } }));
        await Dispatch();                                                                    // the refund fails once and goes into backoff
        for (var i = 0; i < 26; i++) { await Ok(Act(sA.Owner, idA, "hold", new { note = "n" + i })); await Ok(Act(sA.Owner, idA, "resume")); }   // 52 later events of the same order: all due, all blocked behind the refund
        var sB = await NewShop(_f);
        var (midB, idB, numberB, _) = await Place(sB);
        await Ok(Act(sB.Owner, idB, "accept"));                                              // written after all of them
        int mark; lock (_f.Calls) mark = _f.Calls.Count;
        await Dispatch();                                                                    // ONE batch (50): it must not be filled with the blocked events
        Assert.Contains(Received(mark), x => x.Type == "order.accepted" && x.Number == numberB);
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        _f.Time.Advance(TimeSpan.FromMinutes(5)); await Drain();
    }
}
