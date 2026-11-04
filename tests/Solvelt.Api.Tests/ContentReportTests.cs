using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

public class ContentReportTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private const string Path = "/api/internal/v1/content/reports";
    private static string Rid() => "R-" + Guid.NewGuid().ToString("N")[..14];

    private Task<HttpResponseMessage> Report(string kind, string target, string? reportId = null, string reason = "Counterfeit item", string? note = null, string reporter = "buyer-ref-77") =>
        PostSigned(f, Path, new { reportId = reportId ?? Rid(), kind, targetId = target, reason, note, reporterRef = reporter });

    private Guid ContentId(string reportId) => f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == reportId).Select(c => c.Id).Single());
    private List<JsonNode> Decided(string reportId) => Events(f, ContentId(reportId).ToString(), "content.report_decided");
    private Guid QueueId(string reportId) => f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == reportId).Select(c => c.ModerationReportId).Single());

    private async Task<(Api Mod, JsonNode Row)> InQueue(string reportId, string kind)
    {
        var mod = await Staff(f, StaffRole.Moderator);
        var q = await ProdApi.Node(await mod.Get($"/api/v1/admin/moderation?kind={kind}&pageSize=100"));
        var id = QueueId(reportId).ToString();
        return (mod, q["items"]!.AsArray().Single(i => (string)i!["id"]! == id)!);
    }

    [Fact]
    public async Task The_endpoint_is_signed_like_the_rest_of_the_internal_API()
    {
        var body = new { reportId = Rid(), kind = "review", targetId = "rev-1", reason = "spam", reporterRef = "u1" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", Path, body, sign: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", Path, body, secret: "wrong-secret-0123456789abcdef012345"))).StatusCode);
        var stale = await PostSigned(f, Path, body, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 3600);
        Assert.Equal("stale", await Orders.Code(stale));
        var nonce = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await PostSigned(f, Path, body, nonce)).StatusCode);
        var replay = await PostSigned(f, Path, body, nonce);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); Assert.Equal("replayed", await Orders.Code(replay));
    }

    [Fact]
    public async Task Review_report_to_staff_queue_to_decision_to_event()
    {
        var rid = Rid();
        var r = await Report("review", "review-4711", rid, "Contains personal data", "Posted the seller's phone number");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("open", (string)(await Orders.Node(r))["status"]!);

        var (mod, row) = await InQueue(rid, "review");                                         // it is in the existing staff queue
        Assert.Equal("review", (string)row["kind"]!); Assert.Equal("Open", (string)row["status"]!); Assert.Equal("marketplace", (string)row["reporterKind"]!);
        var detail = await ProdApi.Node(await mod.Get($"/api/v1/admin/moderation/{row["id"]}"));
        Assert.Equal("review-4711", (string)detail["target"]!["reviewId"]!);
        Assert.Equal("Posted the seller's phone number", (string)detail["contentReports"]![0]!["note"]!);
        Assert.Empty(Decided(rid));                                                            // nothing is emitted before staff decide

        var res = await mod.Post($"/api/v1/admin/moderation/{row["id"]}/hide-content", new { reason = "Phone number is personal data", reasonShownToUser = "The review was hidden because it shared personal details." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ev = Assert.Single(Decided(rid));
        Assert.Equal("content.report_decided", (string)ev["type"]!);
        var d = ev["data"]!;
        Assert.Equal(rid, (string)d["reportId"]!); Assert.Equal("review", (string)d["kind"]!); Assert.Equal("review-4711", (string)d["targetId"]!);
        Assert.Equal("hidden", (string)d["decision"]!); Assert.Equal("The review was hidden because it shared personal details.", (string)d["reasonShownToUser"]!);
        Assert.DoesNotContain("personal data", ev.ToJsonString().Replace("shared personal details", ""));      // the internal staff reason is never sent
        // deciding again is refused and emits nothing more
        Assert.Equal(HttpStatusCode.Conflict, (await mod.Post($"/api/v1/admin/moderation/{row["id"]}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Single(Decided(rid));
        // the same report is now decided: re-sending it is a no-op that says so
        var dup = await Orders.Node(await Report("review", "review-4711", rid));
        Assert.True((bool)dup["duplicate"]!); Assert.Equal("hidden", (string)dup["decision"]!);
        // the event reaches the marketplace signed with the outbound secret
        await DispatchAll(f);
        var sink = JsonNode.Parse(await (await f.NewClient().GetAsync("/api/dev/outbox")).Content.ReadAsStringAsync())!.AsArray();
        Assert.Contains(sink, x => ((string)x!["body"]!).Contains(rid) && ((string)x["body"]!).Contains("content.report_decided"));
    }

    [Theory]
    [InlineData("dismiss", "dismissed")]
    [InlineData("hide-content", "hidden")]
    [InlineData("remove-content", "removed")]
    public async Task Each_staff_decision_maps_to_one_decision_value(string action, string decision)
    {
        var rid = Rid();
        Assert.Equal(HttpStatusCode.Created, (await Report("review", "rev-" + rid, rid)).StatusCode);
        var (mod, row) = await InQueue(rid, "review");
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{row["id"]}/{action}", new { reason = StaffKit.Why })).StatusCode);
        var d = Assert.Single(Decided(rid))["data"]!;
        Assert.Equal(decision, (string)d["decision"]!); Assert.Null(d["reasonShownToUser"]);   // optional: absent unless staff wrote one
        Assert.Equal(decision, f.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid).Decision));
    }

    [Fact]
    public async Task Reports_are_idempotent_on_reportId_and_a_reused_id_for_something_else_is_a_conflict()
    {
        var rid = Rid();
        var a = await Report("review", "rev-idem", rid);
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var b = await Report("review", "rev-idem", rid);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode); Assert.True((bool)(await Orders.Node(b))["duplicate"]!);
        Assert.Equal(1, f.WithDb(db => db.ContentReports.Count(c => c.ReportId == rid)));
        var conflict = await Report("review", "a-different-review", rid);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal("report_id_conflict", await Orders.Code(conflict));
        // parallel deliveries of one reportId: exactly one row, one queue entry
        var rid2 = Rid();
        var res = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Report("review", "rev-parallel", rid2)));
        Assert.All(res, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal(1, f.WithDb(db => db.ContentReports.Count(c => c.ReportId == rid2)));
    }

    [Fact]
    public async Task Several_reports_of_one_target_share_a_queue_row_and_each_gets_its_own_decision_event()
    {
        var target = "rev-" + Rid();
        var r1 = Rid(); var r2 = Rid();
        await Report("review", target, r1, reporter: "u1"); await Report("review", target, r2, reporter: "u2");
        Assert.Equal(QueueId(r1), QueueId(r2));
        var (mod, row) = await InQueue(r1, "review");
        await mod.Post($"/api/v1/admin/moderation/{row["id"]}/remove-content", new { reason = StaffKit.Why });
        Assert.Equal("removed", (string)Assert.Single(Decided(r1))["data"]!["decision"]!);
        Assert.Equal("removed", (string)Assert.Single(Decided(r2))["data"]!["decision"]!);
        // a later report of the same target reopens the row; only the NEW report is decided next
        var r3 = Rid();
        await Report("review", target, r3, reporter: "u3");
        var (mod2, row2) = await InQueue(r3, "review");
        Assert.Equal("Open", (string)row2["status"]!);
        await mod2.Post($"/api/v1/admin/moderation/{row2["id"]}/dismiss", new { reason = StaffKit.Why });
        Assert.Equal("dismissed", (string)Assert.Single(Decided(r3))["data"]!["decision"]!);
        Assert.Single(Decided(r1)); Assert.Single(Decided(r2));
    }

    [Fact]
    public async Task Listing_reports_reuse_the_report_listing_path_and_hiding_or_removing_the_listing_decides_them()
    {
        var s = await Orders.NewShop(f);
        var rid = Rid();
        Assert.Equal(HttpStatusCode.Created, (await Report("listing", s.Product.ToString(), rid, "Counterfeit")).StatusCode);
        // the older "report listing" endpoint and the new one land in the SAME queue row
        Assert.Equal(HttpStatusCode.OK, (await PostSigned(f, $"/api/internal/v1/listings/{s.Product}/reports", new { reason = "Counterfeit (old path)" })).StatusCode);
        var (mod, row) = await InQueue(rid, "listing");
        Assert.Equal(s.Product.ToString(), (string)row["targetId"]!);
        Assert.Equal(1, f.WithDb(db => db.ModerationReports.Count(r => r.Kind == "listing" && r.TargetId == s.Product)));
        // staff hide the listing through the EXISTING admin endpoint: that decides the marketplace report as "hidden"
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{s.Product}/hide", new { reason = StaffKit.Why, reasonShownToUser = "Hidden: looks counterfeit." })).StatusCode);
        var d = Assert.Single(Decided(rid))["data"]!;
        Assert.Equal("hidden", (string)d["decision"]!); Assert.Equal("listing", (string)d["kind"]!); Assert.Equal(s.Product.ToString(), (string)d["targetId"]!); Assert.Equal("Hidden: looks counterfeit.", (string)d["reasonShownToUser"]!);
        Assert.Equal("listing.unlisted", ListingEvents(f, s.Product).Last().Type);

        // removing: the listing is archived and the report decided as "removed"
        var s2 = await Orders.NewShop(f);
        var rid2 = Rid();
        await Report("listing", s2.Product.ToString(), rid2);
        var (mod2, row2) = await InQueue(rid2, "listing");
        Assert.Equal(HttpStatusCode.OK, (await mod2.Post($"/api/v1/admin/moderation/{row2["id"]}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("removed", (string)Assert.Single(Decided(rid2))["data"]!["decision"]!);
        Assert.Equal(ProductStatus.Archived, f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s2.Product).Status));
        Assert.Null(await Listing(f, s2.Product));
        Assert.Equal("moderated", (string)ListingEvents(f, s2.Product).Last().Data["reason"]!);
    }

    [Fact]
    public async Task Message_reports_flag_the_message_through_the_existing_path_and_can_be_dismissed()
    {
        var s = await Orders.NewShop(f);
        var marketplaceMessageId = "mp-msg-" + Rid();
        f.WithDb(db =>
        {
            var conv = new Conversation { SellerId = s.Seller, BrandId = s.Brand, Kind = ConversationKind.Order, BuyerName = "Jane Buyer", Subject = "Order question", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
            db.Conversations.Add(conv);
            db.Messages.Add(new OrderMessage { ConversationId = conv.Id, SellerId = s.Seller, Direction = MessageDirection.Buyer, Text = "You are a fraud", MarketplaceMessageId = marketplaceMessageId });
            db.SaveChanges(); return 0;
        });
        var rid = Rid();
        Assert.Equal(HttpStatusCode.Created, (await Report("message", marketplaceMessageId, rid, "Harassment")).StatusCode);
        Assert.True(f.WithDb(db => db.Messages.AsNoTracking().Single(m => m.MarketplaceMessageId == marketplaceMessageId).Reported));
        var (mod, row) = await InQueue(rid, "message");
        Assert.Equal(s.Seller.ToString(), (string)row["sellerId"]!);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{row["id"]}/dismiss", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("dismissed", (string)Assert.Single(Decided(rid))["data"]!["decision"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Report("message", "does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Warning_the_seller_closes_the_report_as_dismissed_and_suspending_the_seller_as_removed()
    {
        var s = await Orders.NewShop(f);
        var rid = Rid(); var rid2 = Rid();
        Assert.Equal(HttpStatusCode.Created, (await Report("listing", s.Product.ToString(), rid)).StatusCode);
        var (mod, row) = await InQueue(rid, "listing");
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{row["id"]}/warn-seller", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("dismissed", (string)Assert.Single(Decided(rid))["data"]!["decision"]!);
        Assert.NotNull(await Listing(f, s.Product));                                           // the content stays up
        Assert.Equal(HttpStatusCode.Created, (await Report("listing", s.Product.ToString(), rid2)).StatusCode);   // reopens
        var (mod2, row2) = await InQueue(rid2, "listing");
        Assert.Equal(HttpStatusCode.OK, (await mod2.Post($"/api/v1/admin/moderation/{row2["id"]}/escalate-suspend", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal("removed", (string)Assert.Single(Decided(rid2))["data"]!["decision"]!);
        Assert.Null(await Listing(f, s.Product));
        // a review report has no seller on this portal: warning / suspending is refused
        var rid3 = Rid(); await Report("review", "rev-" + rid3, rid3);
        var (mod3, row3) = await InQueue(rid3, "review");
        Assert.Equal("no_seller", await StaffKit.Err(await mod3.Post($"/api/v1/admin/moderation/{row3["id"]}/warn-seller", new { reason = StaffKit.Why })));
        Assert.Equal("no_seller", await StaffKit.Err(await mod3.Post($"/api/v1/admin/moderation/{row3["id"]}/escalate-suspend", new { reason = StaffKit.Why })));
        Assert.Empty(Decided(rid3));
    }

    [Fact]
    public async Task Only_roles_that_may_act_on_moderation_can_decide_a_report()
    {
        var rid = Rid();
        await Report("review", "rev-" + rid, rid);
        var id = QueueId(rid);
        foreach (var role in new[] { StaffRole.ReadOnly, StaffRole.Support })
        {
            var staff = await Staff(f, role);
            Assert.Equal(HttpStatusCode.OK, (await staff.Get($"/api/v1/admin/moderation/{id}")).StatusCode);                              // may look
            foreach (var action in new[] { "dismiss", "hide-content", "remove-content" })
                Assert.Equal(HttpStatusCode.Forbidden, (await staff.Post($"/api/v1/admin/moderation/{id}/{action}", new { reason = StaffKit.Why })).StatusCode);
        }
        Assert.Empty(Decided(rid));
        var mod = await Staff(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{id}/hide-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Single(Decided(rid));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mod.Post($"/api/v1/admin/moderation/{id}/hide-content", new { })).StatusCode);   // a reason is always required (checked before the state)
    }

    [Theory]
    [InlineData("nonsense", "x", "reason")]
    [InlineData("review", "", "reason")]
    [InlineData("review", "x", "")]
    public async Task Invalid_reports_are_rejected_before_anything_is_stored(string kind, string target, string reason)
    {
        var rid = Rid();
        var r = await Report(kind, target, rid, reason);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, f.WithDb(db => db.ContentReports.Count(c => c.ReportId == rid)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostSigned(f, Path, new { kind = "review", targetId = "x", reason = "y", reporterRef = "u" })).StatusCode);                  // no reportId
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostSigned(f, Path, new { reportId = Rid(), kind = "review", targetId = "x", reason = "y" })).StatusCode);                  // no reporterRef
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Report("listing", "not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Report("listing", Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Report("review", "x", note: new string('n', 1001))).StatusCode);
    }
}
