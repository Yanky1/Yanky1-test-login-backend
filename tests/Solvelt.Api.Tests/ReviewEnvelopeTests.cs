using System.Net;
using System.Text.Json.Nodes;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Tests;

/// <summary>C17.1: every event with a sequence scope carries top-level <c>sequenceScope</c> and <c>sequence</c> in its signed body; events without a scope omit both. Review 422s use the order ingest error shape.</summary>
public static class Sink
{
    /// <summary>Dispatches until at least <paramref name="expect"/> delivered events (parsed signed bodies) match <paramref name="match"/>.</summary>
    public static async Task<List<JsonNode>> Wait(ApiFactory f, Func<JsonNode, bool> match, int expect)
    {
        var found = new List<JsonNode>();
        for (var i = 0; i < 40 && found.Count < expect; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)).StatusCode);
            var sink = await Orders.Node(await f.NewClient().GetAsync("/api/dev/outbox"));
            found = sink.AsArray().Select(x => JsonNode.Parse((string)x!["body"]!)!).Where(match).ToList();
        }
        return found;
    }
}

public class ReviewEnvelopeTests(ReviewFactory f) : IClassFixture<ReviewFactory>
{
    [Fact]
    public async Task Review_events_carry_the_scope_and_an_increasing_sequence()
    {
        var s = await Orders.NewShop(f);
        var (id, rid) = await Rev.Ingest(f, s);
        await Rev.Reply(s.Owner, id, "Thank you!");
        await Rev.Reply(s.Owner, id, "Thank you very much!");
        await Rev.DeleteReply(s.Owner, id);
        var events = await Sink.Wait(f, b => ((string?)b["type"] ?? "").StartsWith("review.") && (string?)b["data"]?["reviewId"] == rid, 3);
        Assert.Equal(["review.reply_posted", "review.reply_posted", "review.reply_removed"], events.Select(e => (string)e["type"]!).ToArray());
        Assert.All(events, e => Assert.Equal("review:" + rid, (string)e["sequenceScope"]!));
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(e => (int)e["sequence"]!).ToArray());
        Assert.Equal(["createdAt", "data", "id", "sequence", "sequenceScope", "type"], Rev.Keys(events[0]));       // next to id / type / createdAt / data
        Assert.Equal(System.Text.Json.JsonValueKind.Number, events[0]["sequence"]!.GetValueKind());
        // the stored payload itself is untouched: the fields are added when sending
        Assert.All(Rev.Outbox(f, id), m => { Assert.DoesNotContain("sequenceScope", m.PayloadJson); Assert.DoesNotContain("\"sequence\"", m.PayloadJson); });
    }

    [Fact]
    public async Task Seller_order_events_carry_the_scope_and_an_increasing_sequence()
    {
        var s = await Orders.NewShop(f);
        var (_, orderId, number, _) = await Orders.Place(s);
        await Orders.Ok(Orders.Act(s.Owner, orderId, "accept"));
        await Orders.Ok(Orders.Act(s.Owner, orderId, "ship", Orders.Ship()));
        var scope = OutboxScopes.Order(orderId);
        var events = await Sink.Wait(f, b => (string?)b["sequenceScope"] == scope, 2);
        Assert.True(events.Count >= 2, "accepted and shipped events expected");
        var seq = events.Select(e => (int)e["sequence"]!).ToList();
        Assert.Equal(seq.OrderBy(x => x), seq);                                    // delivered in sequence order
        Assert.Equal(seq.Count, seq.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, seq.Count), seq);
        Assert.Contains(events, e => (string)e["type"]! == "order.shipped");
    }

    [Fact]
    public async Task Events_without_a_scope_omit_both_fields()
    {
        var s = await Orders.NewShop(f);                                           // activating the product wrote a listing.upserted (a catalog event, no scope)
        var events = await Sink.Wait(f, b => (string?)b["type"] == "listing.upserted" && (string?)b["data"]?["listingId"] == s.Product.ToString(), 1);
        Assert.NotEmpty(events);
        Assert.All(events, e => { Assert.Null(e["sequenceScope"]); Assert.Null(e["sequence"]); Assert.Null(e["predecessorDead"]); });
    }

    [Fact]
    public void Booking_scoped_events_get_the_fields_and_predecessorDead_is_added_next_to_them()
    {
        var payload = """{"id":"e1","type":"booking.confirmed","createdAt":"2026-11-05T10:00:00Z","data":{"booking":{"bookingId":"b1"}}}""";
        var m = new OutboxMessage { EventType = "booking.confirmed", PayloadJson = payload, SequenceScope = OutboxScopes.Booking(Guid.Parse("11111111-1111-1111-1111-111111111111")), OrderKey = 4 };
        var plain = JsonNode.Parse(OutboxDispatcher.Envelope(m, false))!;
        Assert.Equal("bk:11111111111111111111111111111111", (string)plain["sequenceScope"]!); Assert.Equal(4, (int)plain["sequence"]!); Assert.Null(plain["predecessorDead"]);
        var dead = JsonNode.Parse(OutboxDispatcher.Envelope(m, true))!;
        Assert.Equal(4, (int)dead["sequence"]!); Assert.True((bool)dead["predecessorDead"]!);
        // no scope: untouched, even when a listing version sits in OrderKey
        var listing = new OutboxMessage { EventType = "listing.upserted", PayloadJson = payload, OrderKey = 9 };
        Assert.Equal(payload, OutboxDispatcher.Envelope(listing, false));
    }

    [Fact]
    public async Task Review_422_has_the_same_shape_as_the_order_422()
    {
        var s = await Orders.NewShop(f);
        var order = await Orders.Inbound(f, "/api/internal/v1/orders", new { marketplaceOrderId = "", placedAt = DateTime.UtcNow, currency = "USD", lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, order.StatusCode);
        var o = await Orders.Node(order);
        var bad = await Cat.PostSigned(f, Rev.Path, new { reviewId = Rev.Rid(), listingId = s.Product, sellerId = s.Seller, rating = 9, createdAt = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var r = await Orders.Node(bad);
        Assert.Equal(Rev.Keys(o), Rev.Keys(r));                                                         // type, title, status, code, errors
        Assert.Equal("invalid_review", (string)r["code"]!); Assert.Equal(422, (int)r["status"]!);
        var oe = o["errors"]!.AsArray(); var re = r["errors"]!.AsArray();
        Assert.NotEmpty(oe); Assert.NotEmpty(re);
        Assert.Equal(Rev.Keys(oe[0]!), Rev.Keys(re[0]!));                                               // code, index, message
        Assert.Equal(-1, (int)re[0]!["index"]!); Assert.Equal("rating", (string)re[0]!["code"]!); Assert.False(string.IsNullOrEmpty((string)re[0]!["message"]!));
        // the state endpoint and the listing mismatch use it too
        var other = await Rev.State(f, (await Rev.Ingest(f, s)).ReviewId, new { state = "banana" });
        Assert.Equal("state", (string)(await Orders.Node(other))["errors"]![0]!["code"]!);
        var mismatch = await Orders.Node(await Rev.Post(f, Rev.Body(f, s, listing: Guid.NewGuid())));
        Assert.Equal("listingId", (string)mismatch["errors"]![0]!["code"]!);
    }
}

/// <summary>Booking events through the real flow.</summary>
public class BookingEnvelopeTests(SvcFactory f) : IClassFixture<SvcFactory>
{
    [Fact]
    public async Task Booking_events_carry_the_scope_and_sequence_in_the_signed_body()
    {
        var b = await Svc.NewBiz(f);
        var (mid, id, _) = await Svc.Book(b);
        var scope = OutboxScopes.Booking(id);
        var events = await Sink.Wait(f, e => (string?)e["sequenceScope"] == scope, 1);
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.StartsWith("booking.", (string)e["type"]!));
        var seq = events.Select(e => (int)e["sequence"]!).ToList();
        Assert.Equal(seq.OrderBy(x => x), seq); Assert.Equal(1, seq[0]);
        _ = mid;
    }
}
