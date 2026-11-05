using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Reviews;

namespace Solvelt.Api.Tests;

/// <summary>C17 B and C: the seller reviews API and the review.reply_* events.</summary>
public class ReviewSellerTests(ReviewFactory f) : IClassFixture<ReviewFactory>
{
    [Fact]
    public async Task Reply_create_edit_delete_keeps_one_stable_reply_and_sends_ordered_events()
    {
        var s = await Orders.NewShop(f);
        var (id, rid) = await Rev.Ingest(f, s);

        var created = await Rev.Reply(s.Owner, id, "  Thank you for the kind words!  ");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var c = await Orders.Node(created);
        var replyId = (string)c["reply"]!["id"]!;
        Assert.Equal("Thank you for the kind words!", (string)c["reply"]!["text"]!);           // trimmed
        Assert.Null(c["reply"]!["editedAt"]);

        var edit = await Rev.Reply(s.Owner, id, "Thank you, we are glad you like it.");
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var e = await Orders.Node(edit);
        Assert.Equal(replyId, (string)e["reply"]!["id"]!);                                      // replyId is stable across edits
        Assert.NotNull(e["reply"]!["editedAt"]);
        Assert.Equal(1, f.WithDb(db => db.ReviewReplies.Count(r => r.ReviewId == id)));

        // the same text again changes nothing and sends nothing
        Assert.Equal(HttpStatusCode.OK, (await Rev.Reply(s.Owner, id, "Thank you, we are glad you like it.")).StatusCode);

        var del = await Rev.DeleteReply(s.Owner, id);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Null(Rev.Find(await Rev.List(s.Owner), id)!["reply"]);
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.DeleteReply(s.Owner, id)).StatusCode);            // nothing left to delete

        // events: post, edit, delete share one scope and have increasing OrderKeys (C14)
        var events = Rev.Outbox(f, id);
        Assert.Equal(["review.reply_posted", "review.reply_posted", "review.reply_removed"], events.Select(m => m.EventType).ToArray());
        Assert.All(events, m => Assert.Equal("review:" + rid, m.SequenceScope));
        Assert.Equal(new long?[] { 1, 2, 3 }, events.Select(m => m.OrderKey).ToArray());
        var posted = JsonNode.Parse(events[0].PayloadJson)!["data"]!; var edited = JsonNode.Parse(events[1].PayloadJson)!["data"]!; var removed = JsonNode.Parse(events[2].PayloadJson)!["data"]!;
        Assert.Equal(["editedAt", "repliedAt", "replyId", "reviewId", "text"], Rev.Keys(posted));
        Assert.Equal(rid, (string)posted["reviewId"]!); Assert.Equal(replyId, (string)posted["replyId"]!); Assert.Equal("Thank you for the kind words!", (string)posted["text"]!);
        Assert.Null(posted["editedAt"]);
        Assert.Equal(replyId, (string)edited["replyId"]!); Assert.NotNull(edited["editedAt"]); Assert.Equal("Thank you, we are glad you like it.", (string)edited["text"]!);
        Assert.Equal(["reason", "removedAt", "replyId", "reviewId"], Rev.Keys(removed));
        Assert.Equal("seller_deleted", (string)removed["reason"]!); Assert.Equal(replyId, (string)removed["replyId"]!);

        // after a delete a new reply is a new reply (new id), and the sequence continues
        var again = await Orders.Node(await Rev.Reply(s.Owner, id, "Second thoughts"));
        Assert.NotEqual(replyId, (string)again["reply"]!["id"]!);
        Assert.Equal(4, Rev.Outbox(f, id).Last().OrderKey);

        // every reply action is audit-logged without the text
        var audit = f.WithDb(db => db.AuditEvents.AsNoTracking().Where(a => a.Type.StartsWith("review.reply_") && a.Detail!.Contains(id.ToString())).Select(a => new { a.Type, a.Detail }).ToList());
        Assert.Equal(new[] { "review.reply_deleted", "review.reply_edited", "review.reply_posted", "review.reply_posted" }, audit.Select(a => a.Type).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(audit, a => a.Detail!.Contains("Thank you"));
    }

    [Fact]
    public async Task Role_matrix_and_brand_scope()
    {
        var s = await Orders.NewShop(f);
        var (otherBrand, otherProduct) = await Rev.OtherBrandProduct(s);
        var (id, _) = await Rev.Ingest(f, s);
        var (idOther, _) = await Rev.Ingest(f, s, listing: otherProduct);
        var (admin, _) = await TeamHelpers.JoinAs(f, s.Owner, "Admin");
        var (manager, _) = await TeamHelpers.JoinAs(f, s.Owner, "Manager", [s.Brand]);
        var (viewer, _) = await TeamHelpers.JoinAs(f, s.Owner, "Viewer", [s.Brand]);

        foreach (var (who, api) in new[] { ("owner", s.Owner), ("admin", admin), ("manager", manager) })
        {
            var res = await Rev.Reply(api, id, "Thanks from " + who);
            Assert.True(res.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, who + " " + res.StatusCode);
        }
        // Viewer (read-only "staff"): reads within the brand scope, cannot write
        var vlist = await Rev.List(viewer);
        Assert.NotNull(Rev.Find(vlist, id)); Assert.Null(Rev.Find(vlist, idOther));
        Assert.Equal(HttpStatusCode.Forbidden, (await Rev.Reply(viewer, id, "Not allowed")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Rev.DeleteReply(viewer, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Get("/api/v1/seller/reviews?view=hidden")).StatusCode);
        // Manager with brand scope: the other brand's review is invisible and every action on it is 404
        var mlist = await Rev.List(manager);
        Assert.NotNull(Rev.Find(mlist, id)); Assert.Null(Rev.Find(mlist, idOther));
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.Reply(manager, idOther, "Out of scope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.DeleteReply(manager, idOther)).StatusCode);
        // Owner and Admin see both brands; the summary and counters follow the scope
        Assert.Equal(2, (int)(await Rev.List(s.Owner))["summary"]!["count"]!);
        Assert.Equal(1, (int)(await Rev.List(manager))["summary"]!["count"]!);
        Assert.NotNull(Rev.Find(await Rev.List(admin), idOther));
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get("/api/v1/seller/reviews", auth: false)).StatusCode);
    }

    [Fact]
    public async Task Another_accounts_reviews_are_invisible_and_untouchable()
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var (idA, _) = await Rev.Ingest(f, a);
        var (idB, _) = await Rev.Ingest(f, b);
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(b.Owner, idB, "Mine")).StatusCode);
        Assert.Null(Rev.Find(await Rev.List(b.Owner), idA));                                        // not in the list
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.Reply(b.Owner, idA, "Not mine")).StatusCode);   // IDOR: 404, never 403
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.DeleteReply(b.Owner, idA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.DeleteReply(a.Owner, idB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Rev.Reply(b.Owner, Guid.NewGuid(), "Unknown")).StatusCode);
        Assert.Empty(Rev.Outbox(f, idA)); Assert.Single(Rev.Outbox(f, idB));
        Assert.Equal(0, f.WithDb(db => db.ReviewReplies.Count(r => r.ReviewId == idA)));
    }

    [Theory]
    [InlineData("Write to me at jane@example.com")]
    [InlineData("jane (at) example (dot) com")]
    [InlineData("Call 555-123-4567 any time")]
    [InlineData("Call +44 20 7946 0958")]
    [InlineData("See https://myshop.example/offer for more")]
    [InlineData("visit www.myshop.example")]
    [InlineData("order at myshop.com")]
    public async Task Contact_details_are_refused_with_422(string text)
    {
        var s = await Orders.NewShop(f);
        var (id, _) = await Rev.Ingest(f, s);
        var res = await Rev.Reply(s.Owner, id, text);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("contact_details_not_allowed", await Orders.Code(res));
        Assert.Empty(Rev.Outbox(f, id)); Assert.Equal(0, f.WithDb(db => db.ReviewReplies.Count(r => r.ReviewId == id)));
        // an edit that adds contact details is refused too and keeps the old reply
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(s.Owner, id, "Thank you!")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rev.Reply(s.Owner, id, text)).StatusCode);
        Assert.Equal("Thank you!", f.WithDb(db => db.ReviewReplies.AsNoTracking().Single(r => r.ReviewId == id).Text));
        Assert.Single(Rev.Outbox(f, id));
    }

    [Theory]
    [InlineData("")] [InlineData("   ")]
    public async Task Empty_and_overlong_replies_are_422(string text)
    {
        var s = await Orders.NewShop(f);
        var (id, _) = await Rev.Ingest(f, s);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rev.Reply(s.Owner, id, text)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rev.Reply(s.Owner, id, new string('a', 2001))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(s.Owner, id, new string('a', 2000))).StatusCode);       // the limit itself is fine
    }

    [Fact]
    public async Task List_filters_paging_and_summary()
    {
        var s = await Orders.NewShop(f);
        var second = await Orders.ActiveProduct(s.Owner, "Second mug", s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        var ids = new List<Guid>();
        foreach (var rating in new[] { 5, 5, 4, 1 }) ids.Add((await Rev.Ingest(f, s, rating: rating)).Id);
        var onSecond = (await Rev.Ingest(f, s, rating: 3, listing: second)).Id;
        await Rev.Reply(s.Owner, ids[0], "Thank you");
        var hiddenId = await Rev.Ingest(f, s, rating: 2);
        await Rev.State(f, hiddenId.ReviewId, new { state = "hidden" });

        var all = await Rev.List(s.Owner);
        Assert.Equal(5, (int)all["total"]!);
        var sum = all["summary"]!;
        Assert.Equal(5, (int)sum["count"]!); Assert.Equal(3.6, (double)sum["average"]!);                // (5+5+4+1+3)/5; the hidden review does not count
        Assert.Equal(new[] { 2, 1, 1, 0, 1 }, new[] { "5", "4", "3", "2", "1" }.Select(k => (int)sum["counts"]![k]!).ToArray());
        Assert.Equal(1, (int)all["hiddenCount"]!);

        Assert.Equal(2, (int)(await Rev.List(s.Owner, "?rating=5"))["total"]!);
        Assert.Equal(1, (int)(await Rev.List(s.Owner, "?replied=true"))["total"]!);
        Assert.Equal(4, (int)(await Rev.List(s.Owner, "?replied=false"))["total"]!);
        Assert.Equal(1, (int)(await Rev.List(s.Owner, $"?productId={second}"))["total"]!);
        Assert.Equal(3.0, (double)(await Rev.List(s.Owner, $"?productId={second}"))["summary"]!["average"]!);
        Assert.Equal(1, (int)(await Rev.List(s.Owner, "?rating=5&replied=true"))["total"]!);
        var p1 = await Rev.List(s.Owner, "?pageSize=2&page=1"); var p3 = await Rev.List(s.Owner, "?pageSize=2&page=3");
        Assert.Equal(2, p1["items"]!.AsArray().Count); Assert.Single(p3["items"]!.AsArray());
        var seen = new HashSet<string>();
        foreach (var page in new[] { 1, 2, 3 }) foreach (var i in (await Rev.List(s.Owner, $"?pageSize=2&page={page}"))["items"]!.AsArray()) Assert.True(seen.Add((string)i!["id"]!));
        Assert.Equal(5, seen.Count);
        foreach (var bad in new[] { "?rating=0", "?rating=6", "?replied=maybe", "?view=weird" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Owner.Get("/api/v1/seller/reviews" + bad)).StatusCode);
        Assert.Equal(onSecond.ToString(), (string)(await Rev.List(s.Owner, $"?productId={second}"))["items"]![0]!["id"]!);
    }

    [Fact]
    public async Task No_buyer_personal_data_beyond_the_display_name_in_the_API_or_the_events()
    {
        var s = await Orders.NewShop(f);
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Rev.Body(f, s, null, 4, "Nice", "Jane B."), Orders.J))!.AsObject();
        node["buyerId"] = "buyer-4711"; node["buyerEmail"] = "jane.buyer@example.com"; node["buyerPhoto"] = "https://cdn.example.com/jane.jpg"; node["photos"] = new JsonArray("https://cdn.example.com/p.jpg");
        var res = await Cat.PostSigned(f, Rev.Path, node);
        var id = Guid.Parse((string)(await Orders.Node(res))["id"]!);
        await Rev.Reply(s.Owner, id, "Thanks");
        await Rev.Reply(s.Owner, id, "Thanks a lot");
        await Rev.DeleteReply(s.Owner, id);

        var item = Rev.Find(await Rev.List(s.Owner), id)!;
        Assert.Equal(["buyerDisplayName", "createdAt", "editedAt", "id", "productId", "productTitle", "rating", "reply", "reviewId", "state", "text"], Rev.Keys(item));
        Assert.Equal("Jane B.", (string)item["buyerDisplayName"]!);
        var raw = string.Join("\n", Rev.Outbox(f, id).Select(m => m.PayloadJson)) + item.ToJsonString();
        foreach (var leak in new[] { "buyer-4711", "jane.buyer@example.com", "jane.jpg", "p.jpg", "buyerId", "photo" }) Assert.DoesNotContain(leak, raw, StringComparison.OrdinalIgnoreCase);
        foreach (var m in Rev.Outbox(f, id)) Assert.DoesNotContain("Jane", m.PayloadJson);                // the events do not even carry the display name
        var withReply = await Orders.Node(await Rev.Reply(s.Owner, id, "Back again"));
        Assert.Equal(["createdAt", "editedAt", "id", "text"], Rev.Keys(withReply["reply"]!));
        // a hidden review in the Owner's hidden view only adds the moderation fields
        var (hid, hrid) = await Rev.Ingest(f, s);
        await Rev.State(f, hrid, new { state = "hidden", reason = "r" });
        Assert.Equal(["buyerDisplayName", "createdAt", "editedAt", "id", "productId", "productTitle", "rating", "reply", "reviewId", "state", "stateChangedAt", "stateReason", "text"],
            Rev.Keys(Rev.Find(await Rev.List(s.Owner, "?view=hidden"), hid)!));
    }

    [Fact]
    public async Task Concurrent_replies_to_one_review_leave_one_reply_and_ordered_events()
    {
        var s = await Orders.NewShop(f);
        var (id, rid) = await Rev.Ingest(f, s);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Rev.Reply(s.Owner, id, "Parallel reply " + i)));
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Equal(1, f.WithDb(db => db.ReviewReplies.Count(r => r.ReviewId == id && r.RemovedAt == null)));
        var keys = Rev.Outbox(f, id).Select(m => m.OrderKey!.Value).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, keys.Count).Select(i => (long)i), keys);
        Assert.Equal(results.Count(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK), keys.Count);
    }
}
