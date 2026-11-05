using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Reviews;

namespace Solvelt.Api.Tests;

/// <summary>C17 A: the signed marketplace -> portal review endpoints.</summary>
public class ReviewIngestTests(ReviewFactory f) : IClassFixture<ReviewFactory>
{
    [Fact]
    public async Task Both_endpoints_are_signed_with_stale_and_replay_protection()
    {
        var s = await Orders.NewShop(f);
        var body = Rev.Body(f, s);
        var statePath = $"{Rev.Path}/unknown/state";
        foreach (var (path, payload) in new[] { (Rev.Path, body), (statePath, (object)new { state = "hidden" }) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", path, payload, sign: false))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", path, payload, secret: "wrong-secret-0123456789abcdef012345"))).StatusCode);
            var stale = await Cat.PostSigned(f, path, payload, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600);
            Assert.Equal("stale", await Orders.Code(stale));
        }
        // replay: the same nonce is refused on the second delivery (ingest: first one is created)
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await Rev.Post(f, body, nonce)).StatusCode);
        var replay = await Rev.Post(f, body, nonce);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Orders.Code(replay));
        var n2 = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NotFound, (await Cat.PostSigned(f, statePath, new { state = "hidden" }, n2)).StatusCode);
        Assert.Equal("replayed", await Orders.Code(await Cat.PostSigned(f, statePath, new { state = "hidden" }, n2)));
    }

    [Fact]
    public async Task Create_is_idempotent_and_a_newer_editedAt_updates_while_older_or_equal_is_ignored()
    {
        var s = await Orders.NewShop(f);
        var id = Rev.Rid();
        var t0 = f.Time.GetUtcNow().UtcDateTime.AddHours(-3);
        var first = await Rev.Post(f, Rev.Body(f, s, id, 4, "Good", "Sam", t0));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await Orders.Node(first);
        Assert.False((bool)created["duplicate"]!); Assert.Equal("visible", (string)created["state"]!);

        var same = await Rev.Post(f, Rev.Body(f, s, id, 4, "Good", "Sam", t0));            // the same body again
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var dup = await Orders.Node(same);
        Assert.True((bool)dup["duplicate"]!); Assert.Equal((string)created["id"]!, (string)dup["id"]!);

        var e1 = t0.AddHours(1);
        var edit = await Rev.Post(f, Rev.Body(f, s, id, 2, "Changed my mind", "Sam T.", t0, e1));     // newer editedAt: updates
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var upd = await Orders.Node(edit);
        Assert.True((bool)upd["updated"]!); Assert.False((bool)upd["duplicate"]!);
        Review Row() => f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.ExternalId == id));
        Assert.Equal((2, "Changed my mind", "Sam T."), (Row().Rating, Row().Text, Row().BuyerDisplayName));

        foreach (var older in new[] { e1, e1.AddMinutes(-30), t0 })        // equal, older, and the original: ignored with 200, unchanged
        {
            var res = await Rev.Post(f, Rev.Body(f, s, id, 5, "Stale text", "Old name", t0, older));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True((bool)(await Orders.Node(res))["duplicate"]!);
        }
        Assert.Equal((2, "Changed my mind", "Sam T."), (Row().Rating, Row().Text, Row().BuyerDisplayName));
        Assert.Equal(e1, Row().EditedAt);
        Assert.Equal(1, f.WithDb(db => db.Reviews.Count(r => r.ExternalId == id)));
    }

    [Fact]
    public async Task Listing_or_seller_mismatch_is_422_and_nothing_is_stored_across_sellers()
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        // seller A's review for seller B's product (cross-seller injection), with either seller id
        foreach (var body in new[] { Rev.Body(f, a, listing: b.Product), Rev.Body(f, a, seller: b.Seller),
                                     Rev.Body(f, a, listing: Guid.NewGuid()), Rev.Body(f, a, seller: Guid.NewGuid()) })
        {
            var res = await Rev.Post(f, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Equal("invalid_review", await Orders.Code(res));
        }
        Assert.Equal(0, f.WithDb(db => db.Reviews.Count(r => r.SellerId == a.Seller || r.SellerId == b.Seller)));
        // an existing reviewId cannot be moved to another listing either
        var other = await Orders.ActiveProduct(a.Owner, "Another mug", a.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        var id = Rev.Rid();
        Assert.Equal(HttpStatusCode.Created, (await Rev.Post(f, Rev.Body(f, a, id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rev.Post(f, Rev.Body(f, a, id, listing: other))).StatusCode);
    }

    [Theory]
    [InlineData("rating", 0)] [InlineData("rating", 6)] [InlineData("rating", 4.5)] [InlineData("rating", "5")] [InlineData("rating", null)]
    [InlineData("text", 5001)] [InlineData("buyerDisplayName", 61)] [InlineData("reviewId", 51)] [InlineData("reviewId", "")] [InlineData("createdAt", null)] [InlineData("createdAt", "yesterday")]
    public async Task Invalid_fields_are_422(string field, object? value)
    {
        var s = await Orders.NewShop(f);
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Rev.Body(f, s), Orders.J))!.AsObject();
        node[field] = value is int n && field is "text" or "buyerDisplayName" or "reviewId" ? new string('x', n) : value is null ? null : JsonValue.Create(value);
        var res = await Cat.PostSigned(f, Rev.Path, node);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await Orders.Node(res);
        Assert.Equal("invalid_review", (string)body["code"]!);
        Assert.NotNull(body["errors"]![field]);
    }

    [Fact]
    public async Task Boundary_values_are_accepted_and_only_the_display_name_is_kept_about_the_buyer()
    {
        var s = await Orders.NewShop(f);
        var id = new string('r', 50);
        id = id[..34] + Guid.NewGuid().ToString("N")[..16];
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Rev.Body(f, s, id, 1, new string('t', 5000), new string('n', 60)), Orders.J))!.AsObject();
        node["buyerId"] = "buyer-4711"; node["buyerEmail"] = "jane@example.com"; node["photos"] = new JsonArray("https://cdn.example.com/p1.jpg");   // never stored
        var res = await Cat.PostSigned(f, Rev.Path, node);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var row = f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.ExternalId == id));
        Assert.Equal(5000, row.Text!.Length); Assert.Equal(new string('n', 60), row.BuyerDisplayName);
        // the stored record has no place for anything else about the buyer
        Assert.DoesNotContain(typeof(Review).GetProperties(), p => p.Name.Contains("Buyer", StringComparison.OrdinalIgnoreCase) && p.Name != nameof(Review.BuyerDisplayName));
        Assert.DoesNotContain("jane@example.com", System.Text.Json.JsonSerializer.Serialize(row));
        // the seller sees only the allowed keys, in the normal list and in the events
        var list = await Rev.List(s.Owner);
        Assert.Equal(new[] { "buyerDisplayName", "createdAt", "editedAt", "id", "productId", "productTitle", "rating", "reply", "reviewId", "state", "text" }, Rev.Keys(Rev.Find(list, row.Id)!));
    }

    [Fact]
    public async Task Hidden_and_removed_reviews_leave_the_normal_list_and_block_replies()
    {
        var s = await Orders.NewShop(f);
        var (id, rid) = await Rev.Ingest(f, s, text: "Not great.");
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (mgr, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager");
        Assert.NotNull(Rev.Find(await Rev.List(s.Owner), id));

        var hide = await Rev.State(f, rid, new { state = "hidden", reason = "Under review" });
        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        Assert.Equal("hidden", (string)(await Orders.Node(hide))["state"]!);
        Assert.Null(Rev.Find(await Rev.List(s.Owner), id));                                     // out of the normal list...
        var hidden = await Rev.List(s.Owner, "?view=hidden");                                    // ...and in the "Hidden by marketplace" view for Owner and Admin
        Assert.Equal("Under review", (string)Rev.Find(hidden, id)!["stateReason"]!);
        Assert.NotNull(Rev.Find(await Rev.List(admin, "?view=hidden"), id));
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Get("/api/v1/seller/reviews?view=hidden")).StatusCode);
        var blocked = await Rev.Reply(s.Owner, id, "Thanks for the feedback");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); Assert.Equal("review_not_visible", await Orders.Code(blocked));
        Assert.Empty(Rev.Outbox(f, id));

        Assert.Equal(HttpStatusCode.OK, (await Rev.State(f, rid, new { state = "hidden" })).StatusCode);       // the same state again: unchanged
        Assert.Equal(HttpStatusCode.OK, (await Rev.State(f, rid, new { state = "visible" })).StatusCode);      // back
        Assert.NotNull(Rev.Find(await Rev.List(s.Owner), id));
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(s.Owner, id, "Thanks for the feedback")).StatusCode);

        // an update delivered while hidden is stored but stays hidden
        Assert.Equal(HttpStatusCode.OK, (await Rev.State(f, rid, new { state = "hidden" })).StatusCode);
        var upd = await Rev.Post(f, Rev.Body(f, s, rid, 2, "Edited while hidden", edited: f.Time.GetUtcNow().UtcDateTime));
        Assert.True((bool)(await Orders.Node(upd))["updated"]!);
        Assert.Equal("hidden", f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.ExternalId == rid).State.ToString().ToLowerInvariant()));
    }

    [Fact]
    public async Task Removed_redacts_text_and_name_removes_the_reply_and_sends_the_event_and_is_final()
    {
        var s = await Orders.NewShop(f);
        var (id, rid) = await Rev.Ingest(f, s, rating: 2);
        var replyId = (string)(await Orders.Node(await Rev.Reply(s.Owner, id, "Sorry to hear that")))["reply"]!["id"]!;
        Assert.Equal(HttpStatusCode.OK, (await Rev.State(f, rid, new { state = "removed", reason = "Policy" })).StatusCode);

        var row = f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.Id == id));
        Assert.Null(row.Text); Assert.Null(row.BuyerDisplayName); Assert.Equal(2, row.Rating);
        var rep = f.WithDb(db => db.ReviewReplies.AsNoTracking().Single(r => r.Id == Guid.Parse(replyId)));
        Assert.NotNull(rep.RemovedAt); Assert.Equal("review_removed", rep.RemovedReason); Assert.Equal("", rep.Text);
        var ev = Assert.Single(Rev.Events(f, id, "review.reply_removed"));
        Assert.Equal(rid, (string)ev["data"]!["reviewId"]!); Assert.Equal(replyId, (string)ev["data"]!["replyId"]!); Assert.Equal("review_removed", (string)ev["data"]!["reason"]!);
        Assert.NotNull(ev["data"]!["removedAt"]);
        Assert.Equal(new[] { "removedAt", "reason", "replyId", "reviewId" }.OrderBy(x => x, StringComparer.Ordinal), Rev.Keys(ev["data"]!));

        Assert.Null(Rev.Find(await Rev.List(s.Owner), id));
        var h = Rev.Find(await Rev.List(s.Owner, "?view=hidden"), id)!;                         // visible to Owner/Admin as redacted
        Assert.Null((string?)h["text"]); Assert.Null((string?)h["buyerDisplayName"]); Assert.Null(h["reply"]);
        var again = await Rev.State(f, rid, new { state = "visible" });                          // removed is final
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); Assert.Equal("review_removed", await Orders.Code(again));
        Assert.Equal(HttpStatusCode.OK, (await Rev.State(f, rid, new { state = "removed" })).StatusCode);        // repeating it is a no-op
        Assert.Single(Rev.Events(f, id, "review.reply_removed"));
        // a later edit from the marketplace cannot bring the text back
        var upd = await Rev.Post(f, Rev.Body(f, s, rid, 5, "Back again", "Jane", edited: f.Time.GetUtcNow().UtcDateTime));
        Assert.True((bool)(await Orders.Node(upd))["duplicate"]!);
        Assert.Null(f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.Id == id).Text));
    }

    [Fact]
    public async Task State_endpoint_404_and_422()
    {
        var s = await Orders.NewShop(f);
        var (_, rid) = await Rev.Ingest(f, s);
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.State(f, "no-such-review", new { state = "hidden" })).StatusCode);
        foreach (var bad in new object[] { new { state = "banana" }, new { }, new { state = "hidden", reason = new string('x', 301) }, new { state = "hidden", reason = 5 } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rev.State(f, rid, bad)).StatusCode);
        Assert.Equal("visible", f.WithDb(db => db.Reviews.AsNoTracking().Single(r => r.ExternalId == rid).State.ToString().ToLowerInvariant()));
    }
}

/// <summary>The per-caller rate limit of the review endpoints.</summary>
public class ReviewLimitTests(ReviewTightFactory f) : IClassFixture<ReviewTightFactory>
{
    [Fact]
    public async Task Review_calls_are_limited_per_caller_and_forged_requests_cannot_drain_it()
    {
        var s = await Orders.NewShop(f);
        // failed verification only spends the per-IP bucket: a flood of forged calls does not make the signed ones fail
        for (var i = 0; i < 10; i++) await f.NewClient().SendAsync(Orders.Signed(f, "POST", Rev.Path, Rev.Body(f, s), sign: false));
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.Created, (await Rev.Post(f, Rev.Body(f, s))).StatusCode);
        var limited = await Rev.Post(f, Rev.Body(f, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await Orders.Code(limited));
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Rev.State(f, "any", new { state = "hidden" })).StatusCode);      // the same bucket
    }
}

/// <summary>The per-account reply limit (3 per hour here).</summary>
public class ReviewReplyLimitTests(ReviewReplyLimitFactory f) : IClassFixture<ReviewReplyLimitFactory>
{
    [Fact]
    public async Task Replies_are_limited_per_account_and_hour()
    {
        var s = await Orders.NewShop(f);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await Rev.Ingest(f, s)).Id);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(s.Owner, ids[i], "Thanks " + i)).StatusCode);
        var over = await Rev.Reply(s.Owner, ids[0], "Thanks again");            // an edit counts too
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("rate_limited", await Orders.Code(over));
        Assert.Single(Rev.Events(f, ids[0], "review.reply_posted"));            // nothing was written by the refused call
        // another account has its own allowance
        var other = await Orders.NewShop(f);
        var (oid, _) = await Rev.Ingest(f, other);
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(other.Owner, oid, "Thank you")).StatusCode);
    }
}
