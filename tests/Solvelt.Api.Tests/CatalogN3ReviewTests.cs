using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>Brief N10: the review of the N3 fixes (listing report decision race, a retried question after deactivation, searchTerms phone false positives, redelivery after redaction).</summary>
public class CatalogN3ReviewTests(CatalogFactory f) : IClassFixture<CatalogFactory>
{
    private static string Rid() => "R-" + Guid.NewGuid().ToString("N")[..14];

    // ---- A: a concurrent dismiss and hide-content on a LISTING report ---------------------------------------------------------------

    [Fact]
    public async Task Dismiss_and_hide_content_racing_on_one_listing_report_have_one_winner_and_the_listing_the_event_and_the_audit_agree()
    {
        var mods = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Staff(f, StaffRole.Moderator)));
        for (var round = 0; round < 3; round++)                                              // a race: repeat it, the unfixed code failed every round
        {
            var s = await Orders.NewShop(f);
            var rid = Rid();
            Assert.Equal(HttpStatusCode.Created, (await PostSigned(f, "/api/internal/v1/content/reports", new { reportId = rid, kind = "listing", targetId = s.Product.ToString(), reason = "Counterfeit", reporterRef = "u1" })).StatusCode);
            var queue = f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.ModerationReportId).Single());
            var content = f.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.Id).Single());
            string[] actions = ["dismiss", "hide-content", "dismiss", "hide-content", "dismiss", "hide-content"];
            var res = await Task.WhenAll(mods.Select((m, i) => m.Post($"/api/v1/admin/moderation/{queue}/{actions[i]}", new { reason = StaffKit.Why })));
            var winners = res.Select((r, i) => (r.StatusCode, Action: actions[i])).Where(x => x.StatusCode == HttpStatusCode.OK).ToList();
            Assert.Single(winners);                                                            // exactly one decision wins; the others are 409 already_resolved
            Assert.Equal(5, res.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            var ev = Assert.Single(Events(f, content.ToString(), "content.report_decided"));
            var decision = (string)ev["data"]!["decision"]!;
            var hidden = f.WithDb(db => db.Products.AsNoTracking().Single(p => p.Id == s.Product)).ModeratedAt is not null;
            Assert.Equal(winners[0].Action == "hide-content", hidden);                         // the listing follows the winner...
            Assert.Equal(winners[0].Action == "hide-content" ? "hidden" : "dismissed", decision);   // ...and so does the event
            Assert.Equal(hidden ? 1 : 0, f.WithDb(db => db.AuditEvents.Count(a => a.Type == "admin.listing.hidden" && a.TargetId == s.Product.ToString())));   // and the hide audit
        }
    }

    // ---- B: a retried product question after the product was deactivated -----------------------------------------------------------

    [Fact]
    public async Task A_retried_product_question_is_still_a_duplicate_after_the_product_was_deactivated_and_another_products_id_is_not()
    {
        var s = await Orders.NewShop(f); var other = await Orders.NewShop(f);
        var mid = "q-" + Guid.NewGuid().ToString("N")[..10];
        object Body(Guid product, string id) => new { messageId = id, conversationId = "conv-" + id, kind = "product", productId = product, buyerName = "Jane B.", text = "Does it come in blue?" };
        Assert.Equal(HttpStatusCode.Created, (await Orders.Inbound(f, "/api/internal/v1/messages", Body(s.Product, mid))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Post($"/api/v1/seller/products/{s.Product}/status", new { status = "inactive" })).StatusCode);
        var again = await Orders.Inbound(f, "/api/internal/v1/messages", Body(s.Product, mid));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); Assert.True((bool)(await Orders.Node(again))["duplicate"]!);                 // was 404 after the deactivation
        Assert.Equal(1, f.WithDb(db => db.Messages.Count(m => m.MarketplaceMessageId == mid)));
        // the same id for ANOTHER, inactive product is not taken for a duplicate: it is the 404 it always was
        Assert.Equal(HttpStatusCode.OK, (await other.Owner.Post($"/api/v1/seller/products/{other.Product}/status", new { status = "inactive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Orders.Inbound(f, "/api/internal/v1/messages", Body(other.Product, mid))).StatusCode);
    }

    [Fact]
    public async Task A_receipt_for_a_message_id_that_matches_several_messages_is_refused_as_ambiguous_and_a_unique_one_is_applied()
    {
        var a = await Orders.NewShop(f); var b = await Orders.NewShop(f);
        var (midA, idA, numA, _) = await Orders.Place(a); var (midB, idB, numB, _) = await Orders.Place(b);
        var shared = "rcpt-" + Guid.NewGuid().ToString("N")[..8];
        // the sellers each wrote a message; the marketplace gave both the same id (ids are unique per seller only)
        foreach (var (shop, mid, number) in new[] { (a, midA, numA), (b, midB, numB) })
        {
            var opened = await Orders.Inbound(f, "/api/internal/v1/messages", new { messageId = "open-" + Guid.NewGuid().ToString("N")[..8], conversationId = "conv-" + Guid.NewGuid().ToString("N")[..8], kind = "order", marketplaceOrderId = mid, sellerOrderNumber = number, buyerName = "Jane B.", text = "Where is my order?" });
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            var conv = (string)(await Orders.Node(opened))["conversationId"]!;
            var sent = await shop.Owner.Post($"/api/v1/seller/messages/{conv}/messages", new { text = "Your order shipped today." });
            Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        }
        f.WithDb(db =>
        {
            foreach (var m in db.Messages.Where(m => m.Direction == MessageDirection.Seller && (m.SellerId == a.Seller || m.SellerId == b.Seller))) m.MarketplaceMessageId = shared;
            db.SaveChanges(); return 0;
        });
        var amb = await Orders.Inbound(f, $"/api/internal/v1/messages/{shared}/receipts", new { status = "delivered" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, amb.StatusCode); Assert.Equal("ambiguous_message", await Orders.Code(amb));
        f.WithDb(db => { var m = db.Messages.First(x => x.MarketplaceMessageId == shared && x.SellerId == b.Seller); m.MarketplaceMessageId = shared + "-b"; db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/messages/{shared}/receipts", new { status = "delivered" })).StatusCode);   // now unique
        Assert.Equal(DeliveryState.Delivered, f.WithDb(db => db.Messages.AsNoTracking().Single(x => x.MarketplaceMessageId == shared)).Delivery);
    }

    // ---- C: phone false positives ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("fits 2019-2023 models")]
    [InlineData("2019 2020 2021")]
    [InlineData("100 200 300 ml set")]
    [InlineData("1.5 2.5 3.5 4.5 mm")]
    [InlineData("model 3000")]
    [InlineData("2024-05-21 batch")]
    [InlineData("compatible 2018 - 2022, 2023-2024")]
    public void Terms_with_years_decimals_and_short_number_lists_are_not_contact_data(string term)
    {
        Assert.False(SearchTerms.IsContactData(term));
        Assert.Null(SearchTerms.Validate(term));
    }

    [Theory]
    [InlineData("+1 555 123 4567")]
    [InlineData("(555) 123-4567")]
    [InlineData("555-123-4567")]
    [InlineData("555 123 4567")]
    [InlineData("call 5551234567")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("UPC 036000291452")]                  // a 12-digit number is rejected by documented intent (M42)
    [InlineData("write me@example.com")]
    [InlineData("me@example.com")]
    public void Real_phone_numbers_long_numbers_and_e_mail_addresses_are_still_contact_data(string term)
    {
        Assert.True(SearchTerms.IsContactData(term));
        Assert.NotNull(SearchTerms.Validate(term));
    }

    // ---- minor: redelivery after redaction ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_redelivered_after_its_free_text_was_redacted_is_a_duplicate_not_a_conflict_while_another_reporter_after_no_redaction_still_conflicts()
    {
        using var fx = new CatalogFactory();
        var rid = Rid();
        object Body(string reporter = "buyer-ref-1") => new { reportId = rid, kind = "review", targetId = "rev-redelivery", reason = "Contains my phone number", note = "line 3", reporterRef = reporter };
        Assert.Equal(HttpStatusCode.Created, (await PostSigned(fx, "/api/internal/v1/content/reports", Body())).StatusCode);
        var queue = fx.WithDb(db => db.ContentReports.AsNoTracking().Where(c => c.ReportId == rid).Select(c => c.ModerationReportId).Single());
        Assert.Equal(HttpStatusCode.Conflict, (await PostSigned(fx, "/api/internal/v1/content/reports", Body("somebody-else"))).StatusCode);   // before redaction: a different reporter is a conflict
        var mod = await Staff(fx, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{queue}/dismiss", new { reason = StaffKit.Why })).StatusCode);
        fx.Time.Advance(TimeSpan.FromDays(100));
        Assert.Equal(1, (int)(await Orders.Node(await fx.NewClient().PostAsync("/api/dev/maintenance/run", null)))["contentReportsRedacted"]!);
        Assert.Equal("[redacted]", fx.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid)).ReporterRef);
        var again = await PostSigned(fx, "/api/internal/v1/content/reports", Body());                                                               // the marketplace delivers the same report again
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);                                                                                          // was 409 report_id_conflict
        var other = await PostSigned(fx, "/api/internal/v1/content/reports", new { reportId = rid, kind = "listing", targetId = Guid.NewGuid().ToString(), reason = "x", reporterRef = "buyer-ref-1" });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);                                                                                    // a different kind/target is still a conflict
        Assert.Single(Events(fx, fx.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid)).Id.ToString(), "content.report_decided"));    // and no second event
    }
}
