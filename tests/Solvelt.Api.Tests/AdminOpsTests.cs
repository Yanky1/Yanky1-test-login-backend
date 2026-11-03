using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class AdminModerationTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static async Task<JsonElement> Seller(Api a, Guid product) => await a.JsonBody(await a.Get($"/api/v1/seller/products/{product}"));

    private (Guid conv, Guid msg) AddMessage(Shop s, string text, bool reported, bool contactWarning = false)
    {
        var conv = new Conversation { SellerId = s.Seller, BrandId = s.Brand, Kind = ConversationKind.Order, BuyerName = "Jane Buyer", Subject = "Order question", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
        var m = new OrderMessage { ConversationId = conv.Id, SellerId = s.Seller, Direction = MessageDirection.Buyer, Text = text, Reported = reported, ReportReason = reported ? "Harassment" : null, ContactWarning = contactWarning, CreatedAt = DateTime.UtcNow };
        f.WithDb(db => { db.Conversations.Add(conv); db.Messages.Add(m); db.SaveChanges(); return 0; });
        return (conv.Id, m.Id);
    }

    [Fact]
    public async Task Hidden_listing_is_inactive_shows_the_reason_to_the_seller_and_cannot_be_reactivated_until_restored()
    {
        var shop = await Orders.NewShop(f);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mod.Post($"/api/v1/admin/listings/{shop.Product}/hide", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{shop.Product}/hide", new { reason = "Counterfeit brand name in the title" })).StatusCode);
        var d = await Seller(shop.Owner, shop.Product);
        Assert.Equal("Inactive", d.GetProperty("status").GetString());
        Assert.True(d.GetProperty("moderation").GetProperty("hidden").GetBoolean());
        Assert.Equal("Counterfeit brand name in the title", d.GetProperty("moderation").GetProperty("reason").GetString());
        Assert.Single(f.Mails(shop.Owner.Session!.Email, "listing-hidden"));
        var react = await shop.Owner.Post($"/api/v1/seller/products/{shop.Product}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.Conflict, react.StatusCode);
        Assert.Equal("hidden_by_staff", await StaffKit.Err(react));
        Assert.Equal("already_hidden", await StaffKit.Err(await mod.Post($"/api/v1/admin/listings/{shop.Product}/hide", new { reason = StaffKit.Why })));
        var view = await mod.JsonBody(await mod.Get($"/api/v1/admin/listings/{shop.Product}"));
        Assert.True(view.GetProperty("moderation").GetProperty("hidden").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/listings/{shop.Product}/unhide", new { reason = "Title corrected" })).StatusCode);
        d = await Seller(shop.Owner, shop.Product);
        Assert.Equal("Active", d.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("moderation").ValueKind);
        Assert.Equal("not_hidden", await StaffKit.Err(await mod.Post($"/api/v1/admin/listings/{shop.Product}/unhide", new { reason = StaffKit.Why })));
    }

    [Fact]
    public async Task Marketplace_reports_a_listing_through_a_signed_internal_endpoint_and_hiding_resolves_the_report()
    {
        var shop = await Orders.NewShop(f);
        var url = $"/api/internal/v1/listings/{shop.Product}/reports";
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(Orders.Signed(f, "POST", url, new { reason = "x" }, sign: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Orders.Inbound(f, url, new { reason = "x" }, secret: "wrong-secret-wrong-secret-wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Orders.Inbound(f, $"/api/internal/v1/listings/{Guid.NewGuid()}/reports", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, url, new { reason = "Counterfeit item reported by a buyer" })).StatusCode);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var q = await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?kind=listing"));
        var item = q.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("targetId").GetGuid() == shop.Product);
        Assert.Equal("Counterfeit item reported by a buyer", item.GetProperty("reason").GetString());
        Assert.Equal("marketplace", item.GetProperty("reporterKind").GetString());
        await mod.Post($"/api/v1/admin/listings/{shop.Product}/hide", new { reason = "Report confirmed" });
        var open = await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?kind=listing"));
        Assert.DoesNotContain(open.GetProperty("items").EnumerateArray(), i => i.GetProperty("targetId").GetGuid() == shop.Product);
        var done = await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?status=actioned&kind=listing"));
        Assert.Equal("hidden", done.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("targetId").GetGuid() == shop.Product).GetProperty("resolution").GetString());
        // a repeated report of an unresolved-then-resolved listing reopens it
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, url, new { reason = "Reported again" })).StatusCode);
        Assert.Contains((await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?kind=listing"))).GetProperty("items").EnumerateArray(), i => i.GetProperty("targetId").GetGuid() == shop.Product);
    }

    [Fact]
    public async Task Reported_and_contact_flagged_messages_queue_with_masked_summaries_and_can_be_dismissed_or_warned()
    {
        var shop = await Orders.NewShop(f);
        var (_, reported) = AddMessage(shop, "You idiot, call me at 555-123-4567 or mail jane@example.com", reported: true);
        var (_, flagged) = AddMessage(shop, "Let's talk outside the platform: jane@example.com", reported: false, contactWarning: true);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var q = await mod.JsonBody(await mod.Get($"/api/v1/admin/moderation?pageSize=100"));
        var items = q.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("sellerId").GetGuid() == shop.Seller).ToList();
        var r = items.Single(i => i.GetProperty("targetId").GetGuid() == reported);
        var c = items.Single(i => i.GetProperty("targetId").GetGuid() == flagged);
        Assert.Equal("message", r.GetProperty("kind").GetString()); Assert.Equal("buyer", r.GetProperty("reporterKind").GetString());
        Assert.Equal("contact_warning", c.GetProperty("kind").GetString());
        Assert.DoesNotContain("jane@example.com", r.GetRawText()); Assert.DoesNotContain("555-123-4567", r.GetRawText());
        // syncing twice does not duplicate
        Assert.Equal(2, (await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?pageSize=100"))).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("sellerId").GetGuid() == shop.Seller));
        var detail = await mod.JsonBody(await mod.Get($"/api/v1/admin/moderation/{r.GetProperty("id").GetGuid()}"));
        Assert.Contains("You idiot", detail.GetProperty("target").GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString());

        var id1 = r.GetProperty("id").GetGuid(); var id2 = c.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mod.Post($"/api/v1/admin/moderation/{id1}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{id1}/dismiss", new { reason = "Heated but not abusive" })).StatusCode);
        Assert.Equal("already_resolved", await StaffKit.Err(await mod.Post($"/api/v1/admin/moderation/{id1}/dismiss", new { reason = StaffKit.Why })));
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{id2}/warn-seller", new { reason = "Do not move buyers off the platform" })).StatusCode);
        var warn = f.Mails(shop.Owner.Session!.Email, "moderation-warning").Single();
        Assert.Contains("Do not move buyers off the platform", warn.Text);
        Assert.DoesNotContain(items.Select(i => i.GetProperty("id").GetGuid()), id => id == Guid.Empty);
        var open = await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?pageSize=100"));
        Assert.DoesNotContain(open.GetProperty("items").EnumerateArray(), i => i.GetProperty("targetId").GetGuid() == reported || i.GetProperty("targetId").GetGuid() == flagged);
    }

    [Fact]
    public async Task Escalating_a_report_suspends_the_seller_through_the_state_machine()
    {
        var shop = await Orders.NewShop(f);
        var (_, msg) = AddMessage(shop, "threat", reported: true);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var id = (await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?pageSize=100"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("targetId").GetGuid() == msg).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/moderation/{id}/escalate-suspend", new { reason = "Repeated threats against buyers" })).StatusCode);
        Assert.Equal("Suspended", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        Assert.Equal("suspended", (await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?status=actioned&pageSize=100"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id).GetProperty("resolution").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.Owner.Get("/api/v1/seller/products")).StatusCode);
    }

    [Fact]
    public async Task Booking_messages_are_reported_by_the_marketplace_too()
    {
        var shop = await Orders.NewShop(f);
        var m = new BookingMessage { BookingId = Guid.NewGuid(), SellerId = shop.Seller, Text = "pay me cash at jane@example.com", CreatedAt = DateTime.UtcNow };
        f.WithDb(db => { db.Database.OpenConnection(); db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF"); db.BookingMessages.Add(m); db.SaveChanges(); return 0; }); // the booking itself is not needed for the report
        Assert.Equal(HttpStatusCode.OK, (await Orders.Inbound(f, $"/api/internal/v1/booking-messages/{m.Id}/reports", new { reason = "Off-platform payment" })).StatusCode);
        var mod = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var item = (await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?kind=booking_message&pageSize=100"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("targetId").GetGuid() == m.Id);
        Assert.DoesNotContain("jane@example.com", item.GetRawText());
    }
}

public class AdminSupportLookupTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Order_lookups_mask_personal_data_by_default_and_reveal_is_reasoned_and_audited()
    {
        var shop = await Orders.NewShop(f);
        var (_, orderId, number, _) = await Orders.Place(shop);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        foreach (var q in new[] { number, number.ToLower(), orderId.ToString(), "relay-abc@relay.solvelt.example" })
        {
            var r = await support.JsonBody(await support.Get($"/api/v1/admin/lookups/orders?q={Uri.EscapeDataString(q)}"));
            Assert.True(r.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == orderId), "lookup " + q);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await support.Get("/api/v1/admin/lookups/orders?q=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.Get("/api/v1/admin/lookups/refunds?q=abcd")).StatusCode);
        var list = await (await support.Get($"/api/v1/admin/lookups/orders?q={number}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Jane Buyer", list); Assert.DoesNotContain("relay-abc@relay", list);
        var masked = await (await support.Get($"/api/v1/admin/lookups/orders/{orderId}")).Content.ReadAsStringAsync();
        foreach (var pii in new[] { "Jane Buyer", "relay-abc@relay.solvelt.example", "1 Main St", "Apt 2", "555 000 1111", "11201" }) Assert.DoesNotContain(pii, masked);
        Assert.Contains("J*** B***", masked); Assert.Contains("Brooklyn", masked);

        // reveal: reason required, role restricted, audited, rate limited elsewhere
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await support.Post($"/api/v1/admin/lookups/orders/{orderId}/reveal", new { })).StatusCode);
        foreach (var role in new[] { StaffRole.ReadOnly, StaffRole.Moderator })
            Assert.Equal(HttpStatusCode.Forbidden, (await (await StaffKit.LoginNew(f, role)).Post($"/api/v1/admin/lookups/orders/{orderId}/reveal", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(0, f.WithDb(db => db.AuditEvents.Count(e => e.Type == "admin.pii.revealed" && e.TargetId == orderId.ToString())));
        var rv = await support.Post($"/api/v1/admin/lookups/orders/{orderId}/reveal", new { reason = "Buyer phoned about a missing parcel, ticket 8812" });
        Assert.Equal(HttpStatusCode.OK, rv.StatusCode);
        var raw = await rv.Content.ReadAsStringAsync();
        Assert.Contains("Jane Buyer", raw); Assert.Contains("relay-abc@relay.solvelt.example", raw); Assert.Contains("1 Main St", raw);
        var audit = f.WithDb(db => db.AuditEvents.Single(e => e.Type == "admin.pii.revealed" && e.TargetId == orderId.ToString()));
        Assert.Equal("Support", audit.ActorRole); Assert.Equal("order", audit.TargetType); Assert.Equal("Buyer phoned about a missing parcel, ticket 8812", audit.Reason);
        Assert.DoesNotContain("Jane", audit.Detail); Assert.DoesNotContain("Main St", audit.Detail); // the audit row names the fields, never the values
        Assert.Equal(HttpStatusCode.NotFound, (await support.Post($"/api/v1/admin/lookups/orders/{Guid.NewGuid()}/reveal", new { reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Conversation_lookup_masks_contact_details_in_message_text_until_revealed()
    {
        var shop = await Orders.NewShop(f);
        var (_, orderId, number, _) = await Orders.Place(shop);
        var conv = new Conversation { SellerId = shop.Seller, BrandId = shop.Brand, Kind = ConversationKind.Order, OrderId = orderId, BuyerName = "Jane Buyer", Subject = "Where is my order", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow, MessageCount = 1 };
        f.WithDb(db => { db.Conversations.Add(conv); db.Messages.Add(new OrderMessage { ConversationId = conv.Id, SellerId = shop.Seller, Direction = MessageDirection.Buyer, Text = "Reach me on jane.buyer@example.com or +1 555 987 6543", CreatedAt = DateTime.UtcNow }); db.SaveChanges(); return 0; });
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        var byOrder = await support.JsonBody(await support.Get($"/api/v1/admin/lookups/conversations?q={number}"));
        Assert.Equal(conv.Id, byOrder.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        var masked = await (await support.Get($"/api/v1/admin/lookups/conversations/{conv.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("jane.buyer@example.com", masked); Assert.DoesNotContain("987 6543", masked); Assert.DoesNotContain("Jane Buyer", masked);
        var raw = await (await support.Post($"/api/v1/admin/lookups/conversations/{conv.Id}/reveal", new { reason = "Buyer dispute escalation" })).Content.ReadAsStringAsync();
        Assert.Contains("jane.buyer@example.com", raw);
        Assert.Single(f.WithDb(db => db.AuditEvents.Where(e => e.Type == "admin.pii.revealed" && e.TargetId == conv.Id.ToString()).ToList()));
    }

    [Fact]
    public async Task Reveal_is_rate_limited_per_staff_member()
    {
        using var g = new ApiFactory();
        g.Extra["Admin:RevealsPerMinute"] = "2";
        var support = await StaffKit.LoginNew(g, StaffRole.Support);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) codes.Add((await support.Post($"/api/v1/admin/lookups/orders/{Guid.NewGuid()}/reveal", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], codes);
    }
}

public class AdminIntegrationTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private Guid AddOutbox(OutboxStatus st, int attempts, string? error = null, string payload = "{\"orderId\":\"o-1\",\"buyer\":{\"displayName\":\"Jane Buyer\",\"relayEmail\":\"jane@example.com\"},\"shipTo\":{\"line1\":\"1 Main St\",\"postalCode\":\"11201\"},\"totalCents\":1999,\"note\":\"leave at the door\",\"phone\":\"+1 555 000 1111\"}")
    {
        var m = new OutboxMessage { EventType = "order.accepted", SellerId = Guid.NewGuid(), AggregateId = "agg-" + Guid.NewGuid().ToString("N")[..6], PayloadJson = payload, Status = st, Attempts = attempts, LastError = error, CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow.AddHours(1) };
        f.WithDb(db => { db.Outbox.Add(m); db.SaveChanges(); return 0; });
        return m.Id;
    }

    [Fact]
    public async Task Outbox_is_listed_by_status_and_payload_previews_are_redacted()
    {
        var dead = AddOutbox(OutboxStatus.Dead, 8, "HTTP 500 for jane@example.com");
        var retrying = AddOutbox(OutboxStatus.Pending, 2);
        var pending = AddOutbox(OutboxStatus.Pending, 0);
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        async Task<List<Guid>> Ids(string status) => (await ro.JsonBody(await ro.Get($"/api/v1/admin/integrations/outbox?status={status}&pageSize=100"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(dead, await Ids("dead")); Assert.DoesNotContain(retrying, await Ids("dead"));
        Assert.Contains(retrying, await Ids("retrying")); Assert.DoesNotContain(pending, await Ids("retrying"));
        Assert.Contains(pending, await Ids("pending")); Assert.DoesNotContain(retrying, await Ids("pending"));
        Assert.Equal(HttpStatusCode.BadRequest, (await ro.Get("/api/v1/admin/integrations/outbox?status=weird")).StatusCode);
        var list = await ro.JsonBody(await ro.Get("/api/v1/admin/integrations/outbox"));
        Assert.True(list.GetProperty("counts").GetProperty("dead").GetInt32() >= 1);
        var listText = list.GetRawText();
        Assert.DoesNotContain("jane@example.com", listText); // last error is masked in the list
        var d = await (await ro.Get($"/api/v1/admin/integrations/outbox/{dead}")).Content.ReadAsStringAsync();
        foreach (var pii in new[] { "Jane Buyer", "jane@example.com", "1 Main St", "11201", "leave at the door", "555 000 1111" }) Assert.DoesNotContain(pii, d);
        Assert.Contains("o-1", d); Assert.Contains("1999", d); Assert.Contains("[redacted]", d);
    }

    [Fact]
    public async Task Dead_letters_can_be_retried_by_support_and_discarded_by_super_admin_with_step_up_and_audit()
    {
        var a = AddOutbox(OutboxStatus.Dead, 8); var b = AddOutbox(OutboxStatus.Dead, 8);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await support.Post($"/api/v1/admin/integrations/outbox/{a}/retry", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.Post($"/api/v1/admin/integrations/outbox/{a}/retry", new { reason = "Marketplace endpoint is back" })).StatusCode);
        var row = f.WithDb(db => db.Outbox.Single(m => m.Id == a));
        Assert.Equal(OutboxStatus.Pending, row.Status); Assert.Equal(0, row.Attempts); Assert.True(row.NextAttemptAt <= f.Time.GetUtcNow().UtcDateTime);
        Assert.Equal("not_dead", await StaffKit.Err(await support.Post($"/api/v1/admin/integrations/outbox/{a}/retry", new { reason = StaffKit.Why })));
        var audit = f.WithDb(db => db.AuditEvents.Single(e => e.Type == "admin.outbox.retried" && e.TargetId == a.ToString()));
        Assert.Equal("Support", audit.ActorRole); Assert.Equal("Marketplace endpoint is back", audit.Reason);

        Assert.Equal(HttpStatusCode.Forbidden, (await support.Post($"/api/v1/admin/integrations/outbox/{b}/discard", new { reason = StaffKit.Why })).StatusCode);
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Post($"/api/v1/admin/integrations/outbox/{b}/discard", new { reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"/api/v1/admin/integrations/outbox/{b}/discard", new { reason = "Order was cancelled manually" })).StatusCode);
        Assert.Equal(OutboxStatus.Discarded, f.WithDb(db => db.Outbox.Single(m => m.Id == b).Status));
        Assert.Equal(HttpStatusCode.Conflict, (await sa.Post($"/api/v1/admin/integrations/outbox/{b}/discard", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sa.Post($"/api/v1/admin/integrations/outbox/{Guid.NewGuid()}/retry", new { reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Inbound_failures_are_counted_by_cause()
    {
        var path = $"/api/internal/v1/listings/{Guid.NewGuid()}/reports";
        await Orders.Inbound(f, path, new { reason = "x" }, secret: "wrong-secret-wrong-secret-wrong");                     // bad_signature
        await Orders.Inbound(f, path, new { reason = "x" }, ts: f.Time.GetUtcNow().ToUnixTimeSeconds() - 10_000);               // stale
        var nonce = "n-" + Guid.NewGuid().ToString("N");
        await Orders.Inbound(f, path, new { reason = "x" }, nonce: nonce);                                                      // accepted (404 listing), nonce recorded
        await Orders.Inbound(f, path, new { reason = "x" }, nonce: nonce);                                                      // replayed
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var j = await ro.JsonBody(await ro.Get("/api/v1/admin/integrations/inbound"));
        var day = j.GetProperty("last24h");
        Assert.True(day.GetProperty("bad_signature").GetInt32() >= 1);
        Assert.True(day.GetProperty("stale").GetInt32() >= 1);
        Assert.True(day.GetProperty("replayed").GetInt32() >= 1);
        Assert.DoesNotContain("wrong-secret", j.GetRawText());
    }

    [Fact]
    public async Task Phone_failures_and_background_jobs_are_visible()
    {
        f.WithDb(db => { db.AuditEvents.Add(new AuditEvent { Type = "sms.failed", Detail = "to=***6543;kind=Requested;error=HttpRequestException", At = DateTime.UtcNow }); db.SaveChanges(); return 0; });
        using (var scope = f.Services.CreateScope())
        {
            var rec = scope.ServiceProvider.GetRequiredService<JobRecorder>();
            await rec.RunAsync("test-reconciler", () => Task.FromResult(new { fixedRows = 3 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => rec.RunAsync<int>("test-failing", () => throw new InvalidOperationException("boom")));
        }
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var phone = await ro.JsonBody(await ro.Get("/api/v1/admin/integrations/phone"));
        Assert.True(phone.GetProperty("last24h").GetProperty("sms.failed").GetInt32() >= 1);
        Assert.Contains(phone.GetProperty("recentFailures").EnumerateArray(), e => e.GetProperty("type").GetString() == "sms.failed");
        var jobs = (await ro.JsonBody(await ro.Get("/api/v1/admin/integrations/jobs"))).GetProperty("items").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!);
        Assert.True(jobs["test-reconciler"].GetProperty("lastSucceeded").GetBoolean());
        Assert.Contains("fixedRows", jobs["test-reconciler"].GetProperty("lastResult").GetString());
        Assert.False(jobs["test-failing"].GetProperty("lastSucceeded").GetBoolean());
        Assert.Equal(1, jobs["test-failing"].GetProperty("failures").GetInt32());
    }

    [Fact]
    public async Task Dashboard_shows_counts_trends_and_queues()
    {
        var shop = await Orders.NewShop(f);
        await Orders.Place(shop);
        var seller = new Api(f); await seller.SignupVerified(); await seller.CompleteApplicationAndSubmit();
        AddOutbox(OutboxStatus.Dead, 8);
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var d = await ro.JsonBody(await ro.Get("/api/v1/admin/dashboard"));
        Assert.True(d.GetProperty("applications").GetProperty("waiting").GetInt32() >= 1);
        Assert.True(d.GetProperty("applications").GetProperty("pendingReview").GetInt32() >= 1);
        Assert.True(d.GetProperty("sellers").GetProperty("newLast7Days").GetInt32() >= 2);
        Assert.True(d.GetProperty("listings").GetProperty("active").GetInt32() >= 1);
        Assert.True(d.GetProperty("today").GetProperty("orders").GetInt32() >= 1);
        Assert.True(d.GetProperty("integrations").GetProperty("deadLetters").GetInt32() >= 1);
        Assert.Equal(14, d.GetProperty("trend").GetArrayLength());
        Assert.True(d.GetProperty("trend").EnumerateArray().Last().GetProperty("orders").GetInt32() >= 1);
        foreach (var k in new[] { "openReports", "phone", "generatedAt" }) Assert.True(d.TryGetProperty(k, out _), k);
    }
}
