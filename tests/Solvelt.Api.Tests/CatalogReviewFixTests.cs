using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>Brief N3: the fixes from the review of the first marketplace contract PR (docs/DECISIONS.md M22 and following).</summary>
public class CatalogReviewFixTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static JsonNode? Entry(IEnumerable<JsonNode> items, Guid id) => items.LastOrDefault(i => (string)i["listingId"]! == id.ToString());

    // ---- 1. deleted listings ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_deleted_listing_is_reported_by_the_feed_to_a_reader_whose_cursor_is_behind_it()
    {
        var s = await Orders.NewShop(f);
        var cursor = (await ReadFeed(f)).Cursor;                                                        // the marketplace has seen the listing and holds cursor C
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Product}/status", new { status = "inactive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{s.Product}")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.Products.Count(p => p.Id == s.Product)));                     // the product row is really gone

        var (items, next) = await ReadFeed(f, cursor);                                                  // reading from C: the removal must still be there
        var gone = Entry(items, s.Product);
        Assert.NotNull(gone);
        Assert.False((bool)gone!["listed"]!); Assert.Equal("deleted", (string)gone["reason"]!); Assert.Null(gone["title"]);
        Assert.Equal(1, items.Count(i => (string)i["listingId"]! == s.Product.ToString()));             // once (the deactivation's tombstone went away with the row)
        Assert.True(long.Parse(next) >= (long)gone["version"]!);
        Assert.Null(await Listing(f, s.Product));                                                       // and the single-listing endpoint says 404
        var ev = ListingEvents(f, s.Product).Last();
        Assert.Equal("listing.unlisted", ev.Type); Assert.Equal("deleted", (string)ev.Data["reason"]!); Assert.Equal((long)gone["version"]!, (long)ev.Data["version"]!);
        // it is a version like any other: unique, and above everything before it
        Assert.Equal(1, f.WithDb(db => db.CatalogTombstones.Count(t => t.ListingId == s.Product && t.Version == (long)gone["version"]!)));
        Assert.Equal(0, f.WithDb(db => db.Products.Count(p => p.CatalogVersion == (long)gone["version"]!)));
    }

    [Fact]
    public async Task Deleting_a_listing_that_was_never_visible_leaves_nothing_behind_and_paging_with_tombstones_has_no_gaps_or_repeats()
    {
        var s = await Orders.NewShop(f);
        var draft = Guid.Parse((string)(await ProdApi.Create(s.Owner, "Never listed"))["id"]!);
        var cursor = (await ReadFeed(f)).Cursor;
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{draft}")).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.CatalogTombstones.Count(t => t.ListingId == draft)));
        Assert.Empty(ListingEvents(f, draft));
        Assert.Empty((await ReadFeed(f, cursor)).Items);

        var doomed = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await Orders.ActiveProduct(s.Owner, "Doomed " + i, s.Brand, "SKU-" + Guid.NewGuid().ToString("N")[..8]);
            await s.Owner.Post($"/api/v1/seller/products/{id}/status", new { status = "inactive" });
            Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/seller/products/{id}")).StatusCode);
            doomed.Add(id);
        }
        var seen = (await ReadFeed(f, cursor, limit: 1)).Items;                                         // one item per page, tombstones included
        foreach (var id in doomed) Assert.Equal("deleted", (string)Entry(seen, id)!["reason"]!);
        var versions = seen.Select(i => (long)i["version"]!).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions); Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(doomed.Count, seen.Count(i => doomed.Contains(Guid.Parse((string)i["listingId"]!))));
    }

    // ---- 2. concurrent decisions ------------------------------------------------------------------------------------------------------

    private async Task<string> Report(string kind, string target, string? rid = null)
    {
        rid ??= "R-" + Guid.NewGuid().ToString("N")[..14];
        var r = await PostSigned(f, "/api/internal/v1/content/reports", new { reportId = rid, kind, targetId = target, reason = "Counterfeit", reporterRef = "buyer-ref-" + rid });
        Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        return rid;
    }

    private Guid Queue(string rid) => f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.ModerationReportId).Single());
    private List<JsonNode> Decided(string rid) => Events(f, f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.Id).Single()).ToString(), "content.report_decided");

    private async Task<List<Api>> Moderators(int n)
    {
        var res = new List<Api>();
        for (var i = 0; i < n; i++) res.Add(await Staff(f, StaffRole.Moderator));
        return res;
    }

    [Fact]
    public async Task Six_moderators_dismissing_the_same_report_in_parallel_produce_one_decision_and_one_event()
    {
        var rid = await Report("review", "rev-race-" + Guid.NewGuid().ToString("N")[..8]);
        var queue = Queue(rid);
        var mods = await Moderators(6);
        var res = await Task.WhenAll(mods.Select(m => m.Post($"/api/v1/admin/moderation/{queue}/dismiss", new { reason = StaffKit.Why })));
        Assert.Equal(1, res.Count(r => r.StatusCode == HttpStatusCode.OK));                              // exactly one winner
        Assert.Equal(5, res.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var loser in res.Where(r => r.StatusCode == HttpStatusCode.Conflict)) Assert.Equal("already_resolved", await StaffKit.Err(loser));
        var ev = Assert.Single(Decided(rid));                                                            // ...and ONE event
        Assert.Equal("dismissed", (string)ev["data"]!["decision"]!);
        // the decision was audited once, too
        Assert.Equal(1, f.WithDb(db => db.AuditEvents.Count(a => a.Type == "admin.moderation.dismissed" && a.TargetId == queue.ToString())));
    }

    [Fact]
    public async Task Racing_different_decisions_on_one_report_has_one_winner_and_each_marketplace_report_is_decided_once()
    {
        var target = "rev-mixed-" + Guid.NewGuid().ToString("N")[..8];
        var r1 = await Report("review", target); var r2 = await Report("review", target);              // two reports, one queue row
        var queue = Queue(r1); Assert.Equal(queue, Queue(r2));
        var mods = await Moderators(6);
        string[] actions = ["dismiss", "hide-content", "remove-content", "dismiss", "hide-content", "remove-content"];
        var res = await Task.WhenAll(mods.Select((m, i) => m.Post($"/api/v1/admin/moderation/{queue}/{actions[i]}", new { reason = StaffKit.Why })));
        Assert.Equal(1, res.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, res.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var d1 = Assert.Single(Decided(r1)); var d2 = Assert.Single(Decided(r2));                       // one event per report, not one per click
        Assert.Equal((string)d1["data"]!["decision"]!, (string)d2["data"]!["decision"]!);              // both carry the winner's decision
        Assert.Equal(2, f.WithDb(db => db.Outbox.AsNoTracking().Count(m => m.EventType == "content.report_decided" && (m.PayloadJson.Contains(r1) || m.PayloadJson.Contains(r2)))));
    }

    // ---- 3. a report on a listing staff already hid -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_on_an_already_hidden_listing_is_decided_as_hidden_without_hiding_it_again()
    {
        var s = await Orders.NewShop(f);
        var mod = await Staff(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = "Counterfeit brand name in the title" })).StatusCode);
        var hiddenAt = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product)).ModeratedAt;
        var mails = f.Mails(s.Owner.Session!.Email, "listing-hidden").Count;
        var rid = await Report("listing", s.Product.ToString());                                         // the marketplace reports it AFTER staff hid it
        var queue = Queue(rid);
        var res = await mod.Post($"/api/v1/admin/moderation/{queue}/hide-content", new { reason = StaffKit.Why, reasonShownToUser = "Already hidden." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                                                 // was 409 already_hidden
        var d = Assert.Single(Decided(rid))["data"]!;
        Assert.Equal("hidden", (string)d["decision"]!); Assert.Equal("Already hidden.", (string)d["reasonShownToUser"]!);
        var after = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product));
        Assert.Equal(hiddenAt, after.ModeratedAt);                                                       // not hidden a second time
        Assert.Equal("Counterfeit brand name in the title", after.ModerationReason);
        Assert.Equal(mails, f.Mails(s.Owner.Session!.Email, "listing-hidden").Count);                    // and the seller is not mailed again
        Assert.Equal("Actioned", f.WithDb(db => db.ModerationReports.AsNoTracking().Single(r => r.Id == queue).Status.ToString()));
        // the explicit hide endpoint keeps saying so
        Assert.Equal("already_hidden", await StaffKit.Err(await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = StaffKit.Why })));
        // removing a hidden listing is still possible: it is archived and the report is decided as removed
        var rid2 = await Report("listing", s.Product.ToString());
        var q2 = Queue(rid2);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{q2}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("removed", (string)Assert.Single(Decided(rid2))["data"]!["decision"]!);
        Assert.Equal(ProductStatus.Archived, f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product).Status));
    }
}

