using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Reviews;

namespace Solvelt.Api.Tests;

/// <summary>C17 A3: the demo fill seeds three reviews through the review engine; their replies send nothing; removal takes them away.</summary>
public class ReviewDemoTests(DemoFactory f) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task Fill_seeds_three_demo_reviews_one_with_a_reply_and_sends_no_events_and_removal_deletes_them()
    {
        var (api, seller) = await ProdApi.Approved(f);
        var before = DemoKit.TableCounts(f);
        var fill = await api.Post("/api/v1/seller/demo-data");
        Assert.Equal(HttpStatusCode.Created, fill.StatusCode);

        var list = await Rev.List(api);
        var items = list["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { 5, 3, 1 }, items.Select(i => (int)i!["rating"]!).OrderByDescending(x => x).ToArray());
        Assert.All(items, i => Assert.True((bool)i!["demo"]!));                                         // marked as demo (registry), the UI shows the pill
        var replied = items.Where(i => i!["reply"] is not null).ToList();
        Assert.Single(replied); Assert.Equal(5, (int)replied[0]!["rating"]!);
        Assert.Equal(3, f.WithDb(db => db.DemoRecords.Count(r => r.SellerId == seller && r.Kind == "review")));
        // no review event went to the marketplace, not even the one for the demo reply
        Assert.Equal(0, f.WithDb(db => db.Outbox.Count(m => m.SellerId == seller && m.EventType.StartsWith("review."))));

        // later actions on a demo review stay local too: edit and delete the demo reply, reply to the 3-star review
        var five = Guid.Parse((string)replied[0]!["id"]!);
        Assert.Equal(HttpStatusCode.OK, (await Rev.Reply(api, five, "Updated demo reply")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Rev.DeleteReply(api, five)).StatusCode);
        var three = Guid.Parse((string)items.Single(i => (int)i!["rating"]! == 3)!["id"]!);
        Assert.Equal(HttpStatusCode.Created, (await Rev.Reply(api, three, "Demo answer")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.Outbox.Count(m => m.SellerId == seller && m.EventType.StartsWith("review."))));

        // a review the marketplace sends for a demo product is accepted normally (and is not a demo record)
        var productId = f.WithDb(db => db.Products.AsNoTracking().First(p => p.SellerId == seller && p.Type == ListingType.Product).Id);
        var shop = new Shop(f, api, seller, Guid.Empty, productId, "");
        var real = await Rev.Post(f, Rev.Body(f, shop));
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        var realItem = Rev.Find(await Rev.List(api), Guid.Parse((string)(await Orders.Node(real))["id"]!))!;
        Assert.Null(realItem["demo"]);

        // removing the demo data removes the demo reviews, their replies, and any review the marketplace sent for a demo product (the product goes with it)
        var removed = await api.Delete("/api/v1/seller/demo-data");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(0, f.WithDb(db => db.DemoRecords.Count(r => r.SellerId == seller)));
        Assert.Equal(0, f.WithDb(db => db.ReviewReplies.Count(r => r.SellerId == seller)));
        Assert.Equal(0, f.WithDb(db => db.Reviews.Count(r => r.SellerId == seller)));
        Assert.Empty((await Rev.List(api))["items"]!.AsArray());
    }
}
