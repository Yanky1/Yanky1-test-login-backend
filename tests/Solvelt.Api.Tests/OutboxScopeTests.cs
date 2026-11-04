using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

/// <summary>N12: the "superseded" rule belongs to listing events only; the events of a seller order or booking are ordered by their own scope and never dropped.</summary>
public class OutboxScopeTests : IClassFixture<WebhookFactory>
{
    private readonly WebhookFactory _f;
    public OutboxScopeTests(WebhookFactory f) { _f = f; f.Extra["Orders:OutboxBackoffSeconds"] = "1"; }

    private async Task<int> Dispatch() => (int)(await Node(await _f.NewClient().PostAsync("/api/dev/outbox/dispatch", null)))["delivered"]!;
    private async Task Drain() { for (var i = 0; i < 4; i++) if (await Dispatch() == 0) break; }
    private static JsonNode Body(string b) => JsonNode.Parse(b)!;
    private List<(string Type, string Number)> Sent() { lock (_f.Calls) return _f.Calls.Select(c => Body(c.Body)).Select(b => ((string)b["type"]!, (string?)b["data"]?["order"]?["sellerOrderNumber"] ?? "")).ToList(); }

    [Fact]
    public async Task Sibling_seller_orders_of_one_marketplace_order_never_drop_or_block_each_others_events()
    {
        var a = await NewShop(_f); var b = await NewShop(_f);
        var mid = Mid();
        var r = await Inbound(_f, "/api/internal/v1/orders", OrderBody(_f, mid, [Line(a.Seller, a.Brand, a.Product, 1, 1000, "LA"), Line(b.Seller, b.Brand, b.Product, 1, 1000, "LB")],
            [new { sellerId = a.Seller, brandId = a.Brand, amountCents = 500L }, new { sellerId = b.Seller, brandId = b.Brand, amountCents = 300L }]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var orders = (await Node(r))["orders"]!.AsArray().ToDictionary(x => Guid.Parse((string)x!["sellerId"]!), x => (Id: Guid.Parse((string)x!["sellerOrderId"]!), Number: (string)x["number"]!));
        await Drain();                                                                          // anything left from other tests
        await Ok(Act(b.Owner, orders[b.Seller].Id, "accept")); await Ok(Act(b.Owner, orders[b.Seller].Id, "hold", new { note = "waiting" }));
        await Drain();                                                                          // B's events (keys 1, 2 of B's scope) are delivered first
        await Ok(Act(a.Owner, orders[a.Seller].Id, "accept"));                                  // A's order.accepted has key 1, B's newer events are already Delivered
        await Drain();
        var rows = _f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.AggregateId == mid).OrderBy(m => m.CreatedAt).ToList());
        Assert.Equal(3, rows.Count);
        Assert.All(rows, m => Assert.Equal(OutboxStatus.Delivered, m.Status));                  // the reproduced bug: A's accepted was marked Dead "superseded by a newer version"
        Assert.DoesNotContain(rows, m => (m.LastError ?? "").Contains("superseded"));
        var sent = Sent();
        Assert.Contains(("order.accepted", orders[a.Seller].Number), sent);
        Assert.Equal(["order.accepted", "order.held"], sent.Where(x => x.Number == orders[b.Seller].Number).Select(x => x.Type).ToArray());   // in order per seller order
        Assert.NotEqual(rows.Select(m => m.SequenceScope).Distinct().Count(), 1);              // two scopes under one aggregate id
    }

    [Fact]
    public async Task A_requeued_dead_event_of_a_seller_order_is_delivered_not_dropped_as_superseded()
    {
        var s = await NewShop(_f);
        var (mid, id, number, _) = await Place(s);
        await Drain();
        _f.Handler = req => Body(req.Content!.ReadAsStringAsync().Result) is { } b && (string)b["type"]! == "order.accepted" && (string?)b["data"]?["order"]?["sellerOrderNumber"] == number
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : new HttpResponseMessage(HttpStatusCode.OK);
        await Ok(Act(s.Owner, id, "accept")); await Ok(Act(s.Owner, id, "ship", Ship()));          // accepted = key 1 (will die), shipped = key 2 (goes out once its predecessor is Dead)
        for (var i = 0; i < 8; i++) { await Dispatch(); _f.Time.Advance(TimeSpan.FromHours(2)); }
        var accepted = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.AggregateId == mid && m.EventType == "order.accepted"));
        var shipped = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.AggregateId == mid && m.EventType == "order.shipped"));
        Assert.Equal(OutboxStatus.Dead, accepted.Status); Assert.Equal(OutboxStatus.Delivered, shipped.Status);
        // an admin re-queues the dead event (what POST /admin/outbox/{id}/retry does) after its successor went out
        _f.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        _f.WithDb(db => db.Outbox.Where(m => m.Id == accepted.Id).ExecuteUpdate(u => u.SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.Attempts, 0).SetProperty(m => m.LastError, (string?)null).SetProperty(m => m.NextAttemptAt, _f.Time.GetUtcNow().UtcDateTime)));
        await Drain();
        var after = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.Id == accepted.Id));
        Assert.Equal(OutboxStatus.Delivered, after.Status);                                       // was killed as "superseded by a newer version"
        Assert.Null(after.LastError);
    }

    [Fact]
    public async Task Listing_events_are_still_superseded_when_a_newer_version_was_delivered_and_a_pending_older_one_is_not_sent_first()
    {
        var agg = Guid.NewGuid().ToString();
        OutboxMessage Row(long key, OutboxStatus st) => new() { Id = Guid.NewGuid(), EventType = "listing.upserted", AggregateId = agg, OrderKey = key, SequenceScope = null, Status = st, CreatedAt = _f.Time.GetUtcNow().UtcDateTime.AddSeconds(key), NextAttemptAt = _f.Time.GetUtcNow().UtcDateTime, PayloadJson = "{\"id\":\"" + Guid.NewGuid() + "\",\"type\":\"listing.upserted\",\"data\":{\"listingId\":\"" + agg + "\",\"version\":" + key + "}}" };
        var older = Row(1, OutboxStatus.Pending); var newer = Row(2, OutboxStatus.Delivered);
        _f.WithDb(db => { db.Outbox.AddRange(older, newer); db.SaveChanges(); return 0; });
        await Drain();
        var o = _f.WithDb(db => db.Outbox.AsNoTracking().Single(m => m.Id == older.Id));
        Assert.Equal(OutboxStatus.Dead, o.Status); Assert.Contains("superseded", o.LastError);                // the listing rule: the latest version wins
    }

    [Fact]
    public void The_unique_index_rejects_a_second_event_with_the_same_scope_and_number_but_not_unscoped_ones()
    {
        var scope = "so:" + Guid.NewGuid().ToString("N");
        OutboxMessage Row(string? sc, long key, string agg) => new() { Id = Guid.NewGuid(), EventType = "order.held", AggregateId = agg, OrderKey = key, SequenceScope = sc, CreatedAt = _f.Time.GetUtcNow().UtcDateTime, NextAttemptAt = _f.Time.GetUtcNow().UtcDateTime, Status = OutboxStatus.Delivered, PayloadJson = "{}" };
        _f.WithDb(db => { db.Outbox.Add(Row(scope, 1, "x")); db.SaveChanges(); return 0; });
        Assert.Throws<DbUpdateException>(() => _f.WithDb(db => { db.Outbox.Add(Row(scope, 1, "x")); db.SaveChanges(); return 0; }));         // the same number in one scope
        _f.WithDb(db => { db.Outbox.Add(Row(scope, 2, "x")); db.SaveChanges(); return 0; });                                                      // another number is fine
        var agg = Guid.NewGuid().ToString();
        _f.WithDb(db => { db.Outbox.AddRange(Row(null, 7, agg), Row(null, 7, agg)); db.SaveChanges(); return 0; });                                // unscoped events are not covered by the index
    }

    [Fact]
    public void The_migrations_renumber_sql_gives_every_scoped_event_a_unique_number_in_its_existing_order_and_leaves_unscoped_ones_alone()
    {
        using var fx = new WebhookFactory();
        var t0 = fx.Time.GetUtcNow().UtcDateTime;
        OutboxMessage Row(string? sc, long key, int sec, string agg) => new() { Id = Guid.NewGuid(), EventType = "order.held", AggregateId = agg, OrderKey = key, SequenceScope = sc, CreatedAt = t0.AddSeconds(sec), NextAttemptAt = t0, Status = OutboxStatus.Delivered, PayloadJson = "{}" };
        var s1 = "so:" + Guid.NewGuid().ToString("N"); var s2 = "so:" + Guid.NewGuid().ToString("N");
        var rows = new[] { Row(s1, 5, 3, "a"), Row(s1, 5, 1, "a"), Row(s1, 2, 2, "a"), Row(s2, 9, 1, "b"), Row(null, 7, 1, "listing"), Row(null, 7, 2, "listing") };
        fx.WithDb(db =>
        {
            db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_outbox_messages_SequenceScope_OrderKey");       // the state a database had before the unique index existed (duplicates possible)
            db.Outbox.AddRange(rows); db.SaveChanges();
            db.Database.ExecuteSqlRaw(OutboxScopeSql.RenumberScopes);
            return 0;
        });
        var after = fx.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.SequenceScope == s1 || m.SequenceScope == s2 || m.AggregateId == "listing").ToList());
        Assert.Equal([1L, 2L, 3L], after.Where(m => m.SequenceScope == s1).OrderBy(m => m.CreatedAt).Select(m => m.OrderKey!.Value).OrderBy(x => x).ToArray());   // unique, 1..n
        var order = after.Where(m => m.SequenceScope == s1).OrderBy(m => m.OrderKey).Select(m => m.Id).ToArray();
        Assert.Equal([rows[2].Id, rows[1].Id, rows[0].Id], order);                                         // key 2 first, then the two 5s by creation time: the delivery order is kept
        Assert.Equal(1, after.Single(m => m.SequenceScope == s2).OrderKey);
        Assert.All(after.Where(m => m.AggregateId == "listing"), m => Assert.Equal(7, m.OrderKey));        // listing versions untouched
    }
}
