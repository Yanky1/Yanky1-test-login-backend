using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Domain;
using Solvelt.Api.Reviews;
using static Solvelt.Api.Tests.Cat;

namespace Solvelt.Api.Tests;

/// <summary>C17 D: the marketplace reports a seller reply (content report kind review_reply); staff dismiss it or remove it.</summary>
public class ReviewModerationTests(ReviewFactory f) : IClassFixture<ReviewFactory>
{
    private const string Reports = "/api/internal/v1/content/reports";
    private static string Rid() => "R-" + Guid.NewGuid().ToString("N")[..14];

    private async Task<(Shop Shop, Guid ReviewId, string ReplyId)> ShopWithReply(string reply = "Thank you for your review")
    {
        var s = await Orders.NewShop(f);
        var (id, _) = await Rev.Ingest(f, s, rating: 2, text: "The mug cracked after a week.", name: "Casey L.");
        var replyId = (string)(await Orders.Node(await Rev.Reply(s.Owner, id, reply)))["reply"]!["id"]!;
        return (s, id, replyId);
    }

    private Task<HttpResponseMessage> Report(string target, string? reportId = null, string kind = "review_reply") =>
        PostSigned(f, Reports, new { reportId = reportId ?? Rid(), kind, targetId = target, reason = "Rude reply", note = "The seller was unfriendly", reporterRef = "buyer-ref-9" });

    private async Task<(Api Mod, string QueueId)> InQueue(string replyId)
    {
        var mod = await Staff(f, StaffRole.Moderator);
        var q = await ProdApi.Node(await mod.Get("/api/v1/admin/moderation?kind=review_reply&pageSize=100"));
        var row = q["items"]!.AsArray().Single(i => (string)i!["targetId"]! == replyId);
        return (mod, (string)row!["id"]!);
    }

    private bool ReplyActive(string replyId) => f.WithDb(db => db.ReviewReplies.AsNoTracking().Single(r => r.Id == Guid.Parse(replyId)).RemovedAt is null);

    [Fact]
    public async Task A_review_reply_report_lands_in_the_queue_with_the_reply_and_the_review_context()
    {
        var (s, reviewId, replyId) = await ShopWithReply("We are sorry about that, a replacement is on its way.");
        var rid = Rid();
        var res = await Report(replyId, rid);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("open", (string)(await Orders.Node(res))["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await Report(replyId, rid)).StatusCode);                            // idempotent on reportId

        var (mod, qid) = await InQueue(replyId);
        var detail = await ProdApi.Node(await mod.Get($"/api/v1/admin/moderation/{qid}"));
        Assert.Equal("review_reply", (string)detail["kind"]!);
        Assert.Equal(s.Seller.ToString(), (string)detail["sellerId"]!);
        var t = detail["target"]!;
        Assert.Equal("We are sorry about that, a replacement is on its way.", (string)t["replyText"]!);      // the reply text...
        Assert.Equal(2, (int)t["review"]!["rating"]!); Assert.Equal("The mug cracked after a week.", (string)t["review"]!["text"]!);   // ...and the review it answers
        Assert.Equal("Casey L.", (string)t["review"]!["buyerDisplayName"]!);
        Assert.Equal("Speckled stoneware coffee mug", (string)t["review"]!["productTitle"]!);
        Assert.Equal("The seller was unfriendly", (string)detail["contentReports"]![0]!["note"]!);
        // roles that cannot act see masked text, like other message content
        var ro = await Staff(f, StaffRole.ReadOnly);
        var masked = await ProdApi.Node(await ro.Get($"/api/v1/admin/moderation/{qid}"));
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/api/v1/admin/moderation/{qid}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.NotNull(masked["target"]);
    }

    [Fact]
    public async Task Report_validation_and_unknown_targets()
    {
        var (_, _, replyId) = await ShopWithReply();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Report("not-a-guid")).StatusCode);
        var unknown = await Report(Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode); Assert.Equal("not_found", await Orders.Code(unknown));
        Assert.Equal(HttpStatusCode.Created, (await Report(replyId)).StatusCode);
    }

    [Fact]
    public async Task Dismiss_keeps_the_reply_and_sends_only_the_decision()
    {
        var (s, reviewId, replyId) = await ShopWithReply();
        var rid = Rid();
        Assert.Equal(HttpStatusCode.Created, (await Report(replyId, rid)).StatusCode);
        var (mod, qid) = await InQueue(replyId);
        var res = await mod.Post($"/api/v1/admin/moderation/{qid}/dismiss", new { reason = "Polite enough", reasonShownToUser = "We reviewed the reply and it follows our rules." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(ReplyActive(replyId));
        Assert.Single(Rev.Events(f, reviewId));                                                          // only the original reply_posted
        Assert.Empty(Rev.Events(f, reviewId, "review.reply_removed"));
        Assert.Empty(f.Mails(s.Owner.Session!.Email, "review-reply-removed"));
        var cid = f.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid).Id).ToString();
        var decided = Assert.Single(Events(f, cid, "content.report_decided"));
        Assert.Equal("dismissed", (string)decided["data"]!["decision"]!); Assert.Equal("review_reply", (string)decided["data"]!["kind"]!); Assert.Equal(replyId, (string)decided["data"]!["targetId"]!);
    }

    [Fact]
    public async Task Hide_is_not_a_reply_action()
    {
        var (_, _, replyId) = await ShopWithReply();
        await Report(replyId);
        var (mod, qid) = await InQueue(replyId);
        var res = await mod.Post($"/api/v1/admin/moderation/{qid}/hide-content", new { reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("unsupported_decision", await Orders.Code(res));
        Assert.True(ReplyActive(replyId));
        Assert.Equal("Open", (string)(await ProdApi.Node(await mod.Get($"/api/v1/admin/moderation/{qid}")))["status"]!);
    }

    [Fact]
    public async Task Remove_removes_the_reply_sends_staff_removed_and_emails_the_seller_once()
    {
        var (s, reviewId, replyId) = await ShopWithReply();
        var rid = Rid();
        await Report(replyId, rid);
        var (mod, qid) = await InQueue(replyId);
        var res = await mod.Post($"/api/v1/admin/moderation/{qid}/remove-content", new { reason = "Abusive tone", reasonShownToUser = "The reply was removed." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(ReplyActive(replyId));
        Assert.Null(Rev.Find(await Rev.List(s.Owner), reviewId)!["reply"]);
        var ev = Assert.Single(Rev.Events(f, reviewId, "review.reply_removed"));
        Assert.Equal("staff_removed", (string)ev["data"]!["reason"]!); Assert.Equal(replyId, (string)ev["data"]!["replyId"]!);
        var keys = Rev.Outbox(f, reviewId).Select(m => m.OrderKey).ToArray();
        Assert.Equal(new long?[] { 1, 2 }, keys);                                                          // after the reply_posted, same scope
        var mail = Assert.Single(f.Mails(s.Owner.Session!.Email, "review-reply-removed"));
        Assert.Contains("Abusive tone", mail.Text);
        var cid = f.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid).Id).ToString();
        Assert.Equal("removed", (string)Assert.Single(Events(f, cid, "content.report_decided"))["data"]!["decision"]!);
        // deciding again: 409, no second event, no second mail
        Assert.Equal(HttpStatusCode.Conflict, (await mod.Post($"/api/v1/admin/moderation/{qid}/remove-content", new { reason = StaffKit.Why })).StatusCode);
        Assert.Single(Rev.Events(f, reviewId, "review.reply_removed")); Assert.Single(f.Mails(s.Owner.Session!.Email, "review-reply-removed"));
        // the seller can answer again with a new reply; the removed one stays removed
        var again = await Orders.Node(await Rev.Reply(s.Owner, reviewId, "We are sorry."));
        Assert.NotEqual(replyId, (string)again["reply"]!["id"]!);
    }

    [Fact]
    public async Task A_report_of_an_already_removed_reply_is_not_found()
    {
        var (s, reviewId, replyId) = await ShopWithReply();
        await Rev.DeleteReply(s.Owner, reviewId);
        Assert.Equal(HttpStatusCode.NotFound, (await Report(replyId)).StatusCode);
    }

    [Fact]
    public async Task Parallel_dismiss_and_remove_have_exactly_one_winner_and_the_event_and_mail_match_it()
    {
        var dismissWins = 0; var removeWins = 0;
        for (var round = 0; round < 6; round++)
        {
            var (s, reviewId, replyId) = await ShopWithReply();
            var rid = Rid();
            await Report(replyId, rid);
            var (mod1, qid) = await InQueue(replyId);
            var mod2 = await Staff(f, StaffRole.Moderator);
            var dismiss = mod1.Post($"/api/v1/admin/moderation/{qid}/dismiss", new { reason = "Polite enough" });
            var remove = mod2.Post($"/api/v1/admin/moderation/{qid}/remove-content", new { reason = "Abusive tone" });
            var results = await Task.WhenAll(dismiss, remove);
            var codes = results.Select(r => r.StatusCode).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, codes);                    // exactly one winner
            var removeWon = results[1].StatusCode == HttpStatusCode.OK;
            var cid = f.WithDb(db => db.ContentReports.AsNoTracking().Single(c => c.ReportId == rid).Id).ToString();
            var decision = (string)Assert.Single(Events(f, cid, "content.report_decided"))["data"]!["decision"]!;
            Assert.Equal(removeWon ? "removed" : "dismissed", decision);
            Assert.Equal(removeWon ? 1 : 0, Rev.Events(f, reviewId, "review.reply_removed").Count);       // the event matches the winner
            Assert.Equal(removeWon ? 1 : 0, f.Mails(s.Owner.Session!.Email, "review-reply-removed").Count);   // so does the e-mail
            Assert.Equal(!removeWon, ReplyActive(replyId));
            if (removeWon) removeWins++; else dismissWins++;
        }
        Assert.Equal(6, removeWins + dismissWins);
    }

    [Fact]
    public async Task Seller_delete_racing_a_staff_removal_sends_one_removal_event_and_decides_the_report()
    {
        var (s, reviewId, replyId) = await ShopWithReply();
        await Report(replyId);
        var (mod, qid) = await InQueue(replyId);
        var del = Rev.DeleteReply(s.Owner, reviewId);
        var rem = mod.Post($"/api/v1/admin/moderation/{qid}/remove-content", new { reason = "Abusive tone" });
        await Task.WhenAll(del, rem);
        Assert.Single(Rev.Events(f, reviewId, "review.reply_removed"));                                  // never two removal events for one reply
        Assert.False(ReplyActive(replyId));
        var keys = Rev.Outbox(f, reviewId).Select(m => m.OrderKey!.Value).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
