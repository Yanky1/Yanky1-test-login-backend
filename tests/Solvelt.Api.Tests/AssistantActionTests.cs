using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Assistant;
using Solvelt.Api.Domain;
using static Solvelt.Api.Tests.Orders;

namespace Solvelt.Api.Tests;

public class AssistantActionTests : IClassFixture<AssistantFactory>
{
    private readonly AssistantFactory _f;
    public AssistantActionTests(AssistantFactory f) { _f = f; f.Model.Reset(); }

    private int Products(Guid seller, string title) => _f.WithDb(db => db.Products.Count(p => p.SellerId == seller && p.Title == title));

    private async Task<(Shop S, JsonNode Chat, string Id)> ProposeChallah()
    {
        var s = await NewShop(_f);
        var chat = await Asst.Chat(s.Owner, "Add a draft product for a challah board, $25, 10 in stock");
        return (s, chat, Asst.ActionId(chat));
    }

    // ---------------------------------------------------------------- propose only
    [Fact]
    public async Task Proposing_creates_a_card_with_summary_diff_risk_and_expiry_and_changes_nothing()
    {
        var (s, chat, id) = await ProposeChallah();
        var a = Asst.Action(chat);
        Assert.Equal("pending", (string)a["status"]!); Assert.Equal("propose_create_product", (string)a["type"]!); Assert.Equal("low", (string)a["risk"]!);
        Assert.Contains("Challah board", (string)a["summary"]!); Assert.Contains("$25.00", (string)a["summary"]!);
        var diff = a["diff"]!.AsArray(); Assert.Contains(diff, d => (string)d!["field"]! == "Price" && (string)d["after"]! == "$25.00");
        Assert.Contains(diff, d => (string)d!["field"]! == "Stock" && (string)d["after"]! == "10"); Assert.Contains(diff, d => (string)d!["field"]! == "Status" && ((string)d["after"]!).StartsWith("Draft"));
        var exp = DateTime.Parse((string)a["expiresAt"]!).ToUniversalTime();
        Assert.InRange((exp - _f.Time.GetUtcNow().UtcDateTime).TotalMinutes, 9, 10.1);
        Assert.Equal(0, Products(s.Seller, "Challah board"));
        Assert.Contains("Nothing has changed", Asst.Text(chat));
    }

    [Fact]
    public async Task Every_action_tool_only_creates_pending_actions()
    {
        var s = await NewShop(_f);
        var (_, order, number, _) = await Place(s);
        var (conv, _) = await Asst.BuyerMessage(s, order, number, "Where is my order?");
        Asst.Script(_f.Model,
            ("propose_update_listing", new { productId = s.Product, title = "Renamed mug" }),
            ("propose_set_price_stock", new { productId = s.Product, price = 31.5, quantity = 4 }),
            ("propose_set_listing_status", new { productId = s.Product, status = "inactive" }),
            ("propose_accept_order", new { orderId = order }),
            ("propose_buyer_reply", new { conversationId = conv, text = "It ships tomorrow." }));
        var chat = await Asst.Chat(s.Owner, "do several things");
        Assert.Equal(5, chat["message"]!["actions"]!.AsArray().Count);
        var p = await ProdApi.Get(s.Owner, s.Product);
        Assert.Equal("Speckled stoneware coffee mug", (string)p["title"]!); Assert.Equal("Active", (string)p["status"]!);
        Assert.Equal("New", St(await Detail(s.Owner, order)));
        Assert.Empty(_f.WithDb(db => db.Messages.Where(m => m.ConversationId == conv && m.Direction == MessageDirection.Seller).ToList()));
    }

    [Fact]
    public async Task Asking_twice_for_the_same_change_gives_one_card()
    {
        var s = await NewShop(_f);
        Asst.Script(_f.Model, ("propose_set_price_stock", new { productId = s.Product, price = 40 }), ("propose_set_price_stock", new { productId = s.Product, price = 40 }));
        await Asst.Chat(s.Owner, "price");
        Assert.Equal(1, _f.WithDb(db => db.AssistantActions.Count(a => a.SellerId == s.Seller)));
    }

    // ---------------------------------------------------------------- confirm
    [Fact]
    public async Task Confirm_creates_the_draft_product_once_and_records_the_result_and_an_audit_row_via_assistant()
    {
        var (s, chat, id) = await ProposeChallah();
        var r = await Asst.Confirm(s.Owner, id);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Node(r);
        Assert.True((bool)body["ok"]!); Assert.Equal("confirmed", (string)body["action"]!["status"]!); Assert.Equal("product", (string)body["entity"]!["kind"]!);
        Assert.Equal(1, Products(s.Seller, "Challah board"));
        var prod = _f.WithDb(db => db.Products.AsNoTracking().First(p => p.SellerId == s.Seller && p.Title == "Challah board"));
        Assert.Equal(ProductStatus.Draft, prod.Status); Assert.Equal(2500, prod.PriceCents); Assert.Equal(10, prod.Stock);
        Assert.Contains(Asst.Audit(_f, "assistant.action.confirmed"), a => a.Detail!.Contains("via assistant") && a.Detail.Contains(id));
        // the result is appended to the conversation
        var conv = await Node(await s.Owner.Get($"/api/v1/assistant/conversations/{chat["conversationId"]}"));
        Assert.Contains(conv["messages"]!.AsArray(), m => (string)m!["role"]! == "note" && ((string)m["text"]!).StartsWith("Done:"));
        // the owner can see it in the normal product list
        var list = await Node(await s.Owner.Get("/api/v1/seller/products?status=draft"));
        Assert.Contains(list["items"]!.AsArray(), i => (string)i!["title"]! == "Challah board");
    }

    [Fact]
    public async Task Confirming_twice_or_in_parallel_executes_exactly_once()
    {
        var (s, _, id) = await ProposeChallah();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Asst.Confirm(s.Owner, id)));
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        var again = await Asst.Confirm(s.Owner, id);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); Assert.True((bool)(await Node(again))["alreadyDone"]!);
        Assert.Equal(1, Products(s.Seller, "Challah board"));
    }

    [Fact]
    public async Task Confirm_needs_the_csrf_header()
    {
        var (s, _, id) = await ProposeChallah();
        var r = await Asst.Confirm(s.Owner, id, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode); Assert.Equal("csrf", await r.ErrCode());
        Assert.Equal(0, Products(s.Seller, "Challah board"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Asst.Cancel(s.Owner, id, csrf: false)).StatusCode);
    }

    [Fact]
    public async Task Confirm_is_refused_with_a_foreign_origin()
    {
        var (s, _, id) = await ProposeChallah();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assistant/actions/{id}/confirm") { Content = System.Net.Http.Json.JsonContent.Create(new { }) };
        req.Headers.Add("X-CSRF-Token", s.Owner.Csrf!); req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.Send(req)).StatusCode);
        Assert.Equal(0, Products(s.Seller, "Challah board"));
    }

    [Fact]
    public async Task The_model_has_no_way_to_confirm_its_own_action()
    {
        var s = await NewShop(_f);
        Asst.Script(_f.Model, ("propose_create_product", new { title = "Self confirmed" }), ("confirm_action", new { id = Guid.NewGuid() }), ("propose_create_product", new { title = "Self confirmed", price = 1 }));
        var chat = await Asst.Chat(s.Owner, "go");
        Assert.All(_f.WithDb(db => db.AssistantActions.Where(a => a.SellerId == s.Seller).ToList()), a => Assert.Equal(PendingStatus.Pending, a.Status));
        Assert.Equal(0, Products(s.Seller, "Self confirmed"));
        // even a request carrying the user's token but no CSRF header (as a tool-triggered call would) is refused
        Assert.Equal(HttpStatusCode.Forbidden, (await Asst.Confirm(s.Owner, Asst.ActionId(chat), csrf: false)).StatusCode);
    }

    // ---------------------------------------------------------------- permission re-check
    [Fact]
    public async Task Role_removed_after_proposal_confirm_is_forbidden_and_nothing_changes()
    {
        var s = await NewShop(_f);
        var (mgr, email) = await TeamHelpers.JoinAs(_f, s.Owner, "manager");
        var chat = await Asst.Chat(mgr, "Add a draft product for a challah board, $25, 10 in stock");
        var id = Asst.ActionId(chat);
        Asst.SetRole(_f, s.Seller, email, AccountRole.Viewer); // demoted after the proposal
        var r = await Asst.Confirm(mgr, id);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode); Assert.Equal("forbidden", await r.ErrCode());
        Assert.Equal(0, Products(s.Seller, "Challah board"));
        // and it stays unusable, even after the role comes back
        Asst.SetRole(_f, s.Seller, email, AccountRole.Manager);
        Assert.NotEqual(HttpStatusCode.OK, (await Asst.Confirm(mgr, id)).StatusCode);
        Assert.Equal(0, Products(s.Seller, "Challah board"));
    }

    [Fact]
    public async Task Brand_removed_after_proposal_confirm_cannot_reach_the_order()
    {
        var s = await NewShop(_f);
        var (mgr, email) = await TeamHelpers.JoinAs(_f, s.Owner, "manager", [s.Brand]);
        var (_, order, number, _) = await Place(s);
        var chat = await Asst.Chat(mgr, $"accept order {number}");
        var id = Asst.ActionId(chat);
        _f.WithDb(db => { var uid = db.Users.First(u => u.Email == email).Id; var mid = db.Memberships.First(m => m.SellerId == s.Seller && m.UserId == uid).Id; db.MembershipBrands.RemoveRange(db.MembershipBrands.Where(b => b.MembershipId == mid)); db.SaveChanges(); return 0; });
        var r = await Asst.Confirm(mgr, id);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("New", St(await Detail(s.Owner, order)));
    }

    // ---------------------------------------------------------------- state changed => refreshed proposal
    [Fact]
    public async Task Target_changed_after_proposal_returns_a_refreshed_proposal_and_does_not_execute()
    {
        var s = await NewShop(_f);
        var chat = await Asst.Chat(s.Owner, $"set the price of {s.Product} to $44");
        var id = Asst.ActionId(chat);
        // somebody else edits the listing in the meantime
        var cur = await ProdApi.Get(s.Owner, s.Product);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.Patch($"/api/v1/seller/products/{s.Product}/quick", new { version = (int)cur["version"]!, price = 12.5m })).StatusCode);
        var r = await Asst.Confirm(s.Owner, id);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await Node(r);
        Assert.Equal("target_changed", (string)body["code"]!);
        Assert.Equal("superseded", (string)body["action"]!["status"]!);
        var fresh = body["refreshed"]!;
        Assert.Equal("pending", (string)fresh["status"]!);
        Assert.Contains(fresh["diff"]!.AsArray(), d => (string)d!["field"]! == "Price" && (string)d["before"]! == "$12.50" && (string)d["after"]! == "$44.00");
        Assert.Equal(1250, _f.WithDb(db => db.Products.AsNoTracking().First(p => p.Id == s.Product).PriceCents)); // not executed
        // confirming the refreshed proposal works
        var ok = await Asst.Confirm(s.Owner, (string)fresh["id"]!);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(4400, _f.WithDb(db => db.Products.AsNoTracking().First(p => p.Id == s.Product).PriceCents));
        // the stale one stays dead
        Assert.Equal(HttpStatusCode.Conflict, (await Asst.Confirm(s.Owner, id)).StatusCode);
    }

    [Fact]
    public async Task Order_state_changed_after_proposal_is_not_accepted_twice()
    {
        var s = await NewShop(_f);
        var (_, order, number, _) = await Place(s);
        var chat = await Asst.Chat(s.Owner, $"accept order {number}");
        Assert.Equal(HttpStatusCode.OK, (await Act(s.Owner, order, "accept")).StatusCode); // the seller accepts it by hand first
        var r = await Asst.Confirm(s.Owner, Asst.ActionId(chat));
        Assert.True(r.StatusCode is HttpStatusCode.Conflict, r.StatusCode.ToString());
        Assert.Equal("Accepted", St(await Detail(s.Owner, order)));
    }

    // ---------------------------------------------------------------- expiry, binding, cancel
    [Fact]
    public async Task Proposals_expire_after_ten_minutes()
    {
        var (s, _, id) = await ProposeChallah();
        _f.Time.Advance(TimeSpan.FromMinutes(9));
        var gets = await Node(await s.Owner.Get($"/api/v1/assistant/actions/{id}"));
        Assert.Equal("pending", (string)gets["status"]!);
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        var r = await Asst.Confirm(s.Owner, id);
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode); Assert.Equal("action_expired", await r.ErrCode());
        Assert.Equal(0, Products(s.Seller, "Challah board"));
    }

    [Fact]
    public async Task Actions_are_bound_to_user_and_account_anyone_else_gets_404()
    {
        var (s, _, id) = await ProposeChallah();
        var (mate, _) = await TeamHelpers.JoinAs(_f, s.Owner, "admin");  // another user, same account
        var other = await NewShop(_f);                                   // another account
        foreach (var who in new[] { mate, other.Owner })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Asst.Confirm(who, id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Asst.Cancel(who, id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.Get($"/api/v1/assistant/actions/{id}")).StatusCode);
        }
        Assert.Equal(0, Products(s.Seller, "Challah board"));
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, id)).StatusCode);
    }

    [Fact]
    public async Task Cancel_changes_nothing_and_blocks_a_later_confirm()
    {
        var (s, chat, id) = await ProposeChallah();
        var c = await Asst.Cancel(s.Owner, id);
        Assert.Equal(HttpStatusCode.OK, c.StatusCode); Assert.Equal("cancelled", (string)(await Node(c))["action"]!["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Asst.Confirm(s.Owner, id)).StatusCode);
        Assert.Equal(0, Products(s.Seller, "Challah board"));
        Assert.Equal(HttpStatusCode.OK, (await Asst.Cancel(s.Owner, id)).StatusCode); // idempotent
        Assert.Contains(Asst.Audit(_f, "assistant.action.cancelled"), a => a.Detail!.Contains("via assistant"));
    }

    [Fact]
    public async Task Deleting_a_conversation_removes_its_pending_actions()
    {
        var (s, chat, id) = await ProposeChallah();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.Delete($"/api/v1/assistant/conversations/{chat["conversationId"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Asst.Confirm(s.Owner, id)).StatusCode);
        Assert.Equal(0, Products(s.Seller, "Challah board"));
    }

    // ---------------------------------------------------------------- each action executes through its service
    [Fact]
    public async Task Accept_and_ship_orders_through_the_order_service()
    {
        var s = await NewShop(_f);
        var (_, order, number, _) = await Place(s);
        var c1 = await Asst.Chat(s.Owner, $"accept order {number}");
        Assert.Equal("low", (string)Asst.Action(c1)["risk"]!);
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, Asst.ActionId(c1))).StatusCode);
        Assert.Equal("Accepted", St(await Detail(s.Owner, order)));
        var c2 = await Asst.Chat(s.Owner, $"ship order {number} with UPS tracking 1Z999AA10123456784");
        Assert.Contains("1Z999AA10123456784", (string)Asst.Action(c2)["summary"]!);
        Assert.Equal("Accepted", St(await Detail(s.Owner, order)));
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, Asst.ActionId(c2))).StatusCode);
        var d = await Detail(s.Owner, order);
        Assert.Equal("Shipped", St(d)); Assert.Equal("1Z999AA10123456784", (string)d["shipments"]![0]!["trackingNumber"]!);
        Assert.Contains(Asst.Audit(_f, "assistant.action.confirmed"), a => a.Detail!.Contains("propose_mark_shipped"));
    }

    [Fact]
    public async Task Bad_tracking_numbers_are_rejected_at_proposal_time()
    {
        var s = await NewShop(_f);
        var (_, order, number, _) = await Place(s);
        Assert.Equal(HttpStatusCode.OK, (await Act(s.Owner, order, "accept")).StatusCode);
        var chat = await Asst.Chat(s.Owner, $"ship order {number} with UPS tracking 123");
        Assert.Empty(chat["message"]!["actions"]!.AsArray());
        Assert.Contains("tracking", Asst.LastToolResults(_f.Model));
    }

    [Fact]
    public async Task Listing_changes_execute_through_the_product_endpoints_rules()
    {
        var s = await NewShop(_f);
        Asst.Script(_f.Model, ("propose_update_listing", new { productId = s.Product, title = "Renamed mug", bullets = new[] { "Dishwasher safe", "Handmade" }, description = "Great for **coffee**." }),
            ("propose_set_price_stock", new { productId = s.Product, price = 31.5, quantity = 7 }));
        var chat = await Asst.Chat(s.Owner, "update my listing");
        // both cards target the same listing: the second one finds it changed by the first and comes back as a refreshed proposal
        var ids = chat["message"]!["actions"]!.AsArray().Select(a => (string)a!["id"]!).ToList();
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, ids[0])).StatusCode);
        var second = await Asst.Confirm(s.Owner, ids[1]);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, (string)(await Node(second))["refreshed"]!["id"]!)).StatusCode);
        var p = await ProdApi.Get(s.Owner, s.Product);
        Assert.Equal("Renamed mug", (string)p["title"]!); Assert.Equal(31.5m, (decimal)p["price"]!); Assert.Equal(7, (int)p["quantity"]!);
        Assert.Equal(2, p["bullets"]!.AsArray().Count);
        // unsafe values are refused up front: price below zero, bullets over the limit
        Asst.Script(_f.Model, ("propose_set_price_stock", new { productId = s.Product, price = -3 }), ("propose_update_listing", new { productId = s.Product, bullets = new[] { "1", "2", "3", "4", "5", "6" } }));
        _f.Model.Requests.ToList(); var bad = await Asst.Chat(s.Owner, "break it");
        Assert.Empty(bad["message"]!["actions"]!.AsArray());
    }

    [Fact]
    public async Task Deactivate_and_activate_a_listing_with_readiness_and_account_checks()
    {
        var s = await NewShop(_f);
        var c = await Asst.Chat(s.Owner, $"deactivate {s.Product}");
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, Asst.ActionId(c))).StatusCode);
        Assert.Equal("Inactive", (string)(await ProdApi.Get(s.Owner, s.Product))["status"]!);
        var c2 = await Asst.Chat(s.Owner, $"activate {s.Product}");
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, Asst.ActionId(c2))).StatusCode);
        Assert.Equal("Active", (string)(await ProdApi.Get(s.Owner, s.Product))["status"]!);
        // an unready draft cannot be activated: no card, an explanation
        var draft = (Guid)Guid.Parse((string)(await ProdApi.Create(s.Owner, "Bare draft"))["id"]!);
        var c3 = await Asst.Chat(s.Owner, $"activate {draft}");
        Assert.Empty(c3["message"]!["actions"]!.AsArray()); Assert.Contains("isn't ready", Asst.LastToolResults(_f.Model));
    }

    [Fact]
    public async Task Buyer_reply_is_sent_only_to_an_existing_conversation_with_the_exact_text_after_confirm()
    {
        var s = await NewShop(_f);
        var (_, order, number, _) = await Place(s);
        var (conv, _) = await Asst.BuyerMessage(s, order, number, "Where is my order?");
        var chat = await Asst.Chat(s.Owner, $"reply to conversation {conv}: Thanks for your patience, it ships tomorrow.");
        var a = Asst.Action(chat);
        Assert.Equal("medium", (string)a["risk"]!);
        Assert.Contains(a["diff"]!.AsArray(), d => (string)d!["after"]! == "Thanks for your patience, it ships tomorrow.");
        Assert.Empty(_f.WithDb(db => db.Messages.Where(m => m.ConversationId == conv && m.Direction == MessageDirection.Seller).ToList()));
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(s.Owner, (string)a["id"]!)).StatusCode);
        var sent = _f.WithDb(db => db.Messages.AsNoTracking().Where(m => m.ConversationId == conv && m.Direction == MessageDirection.Seller).ToList());
        Assert.Single(sent); Assert.Equal("Thanks for your patience, it ships tomorrow.", sent[0].Text);
        Assert.Single(_f.WithDb(db => db.Outbox.AsNoTracking().Where(m => m.EventType == "message.sent" && m.SellerId == s.Seller).ToList()));
        // a made-up conversation id is not proposed at all; contact details are refused
        Asst.Script(_f.Model, ("propose_buyer_reply", new { conversationId = Guid.NewGuid(), text = "hi" }), ("propose_buyer_reply", new { conversationId = conv, text = "Call me on +1 212 555 0199 or pay at https://pay.example/x" }));
        var bad = await Asst.Chat(s.Owner, "x");
        Assert.Empty(bad["message"]!["actions"]!.AsArray());
    }

    [Fact]
    public async Task Booking_requests_can_be_approved_or_declined_and_calendar_blocks_added()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id1, start) = await Svc.Book(b, 10);
        var (_, id2, _) = await Svc.Book(b, 14);
        var c1 = await Asst.Chat(b.Owner, $"approve booking {id1}");
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(b.Owner, Asst.ActionId(c1))).StatusCode);
        Assert.Equal("Confirmed", (string)(await Svc.Detail(b.Owner, id1))["status"]!);
        var c2 = await Asst.Chat(b.Owner, $"decline booking {id2}");
        Assert.Equal("Requested", (string)(await Svc.Detail(b.Owner, id2))["status"]!);
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(b.Owner, Asst.ActionId(c2))).StatusCode);
        Assert.Equal("Declined", (string)(await Svc.Detail(b.Owner, id2))["status"]!);
        // calendar block on the owner's own provider calendar
        var day = start.AddDays(14).ToString("yyyy-MM-dd");
        Asst.Script(_f.Model, ("propose_calendar_block", new { kind = "timeoff", date = day, note = "Holiday" }));
        var c3 = await Asst.Chat(b.Owner, "block a day");
        Assert.Equal(0, _f.WithDb(db => db.CalendarBlocks.Count(x => x.SellerId == b.Seller)));
        Assert.Equal(HttpStatusCode.OK, (await Asst.Confirm(b.Owner, Asst.ActionId(c3))).StatusCode);
        Assert.Equal(1, _f.WithDb(db => db.CalendarBlocks.Count(x => x.SellerId == b.Seller && x.Note == "Holiday")));
    }

    [Fact]
    public async Task Booking_tools_respect_who_may_act_on_the_booking()
    {
        var b = await Svc.NewBiz(_f, d => d["bookingMode"] = "request");
        var (_, id, _) = await Svc.Book(b, 10);
        var (viewer, _) = await TeamHelpers.JoinAs(_f, b.Owner, "viewer");
        Asst.Script(_f.Model, ("propose_decide_booking", new { bookingId = id, decision = "approve" }), ("propose_calendar_block", new { kind = "busy", date = "2030-01-01" }));
        var chat = await Asst.Chat(viewer, "approve it");
        Assert.Empty(chat["message"]!["actions"]!.AsArray());
        Assert.Equal("Requested", (string)(await Svc.Detail(b.Owner, id))["status"]!);
    }
}
