using System.Net;
using System.Text.Json;
using Solvelt.Api.Admin;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the security review of the admin panel.</summary>
public class AdminReviewFixTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Seller_kind_admin_sessions_cannot_use_the_older_admin_routes()
    {
        var legacy = new Api(f); await legacy.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        foreach (var (m, u) in new[] { ("GET", "/api/v1/admin/outbox"), ("GET", "/api/v1/admin/messages/reported"),
                     ("POST", $"/api/v1/admin/outbox/{Guid.NewGuid()}/retry"), ("POST", $"/api/v1/admin/messages/{Guid.NewGuid()}/dismiss-report") })
        {
            var res = await legacy.Send(new HttpRequestMessage(new HttpMethod(m), u) { Content = m == "POST" ? System.Net.Http.Json.JsonContent.Create(new { reason = StaffKit.Why }) : null });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("not_staff_session", await StaffKit.Err(res));
        }
    }

    [Fact]
    public async Task Support_cannot_act_on_staff_accounts_but_a_stepped_up_super_admin_can()
    {
        var targetEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var target = StaffKit.UserId(f, targetEmail);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        foreach (var action in new[] { "sign-out-everywhere", "clear-lockout", "send-password-reset", "remove-phone" })
        {
            var res = await support.Post($"/api/v1/admin/users/{target}/{action}", new { reason = StaffKit.Why });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("staff_target", await StaffKit.Err(res));
        }
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Post($"/api/v1/admin/users/{target}/clear-lockout", new { reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await sa.Post($"/api/v1/admin/users/{target}/clear-lockout", new { reason = StaffKit.Why })).StatusCode);
        // an ordinary (non-staff) user is still Support's to help
        var seller = new Api(f); var email = await seller.SignupVerified();
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/users/{StaffKit.UserId(f, email)}/clear-lockout", new { reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Moderation_detail_masks_message_text_for_roles_that_cannot_act()
    {
        var shop = await Orders.NewShop(f);
        var conv = new Solvelt.Api.Domain.Conversation { SellerId = shop.Seller, BrandId = shop.Brand, Kind = Solvelt.Api.Domain.ConversationKind.Order, BuyerName = "Jane Buyer", Subject = "Q", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
        var msg = new Solvelt.Api.Domain.OrderMessage { ConversationId = conv.Id, SellerId = shop.Seller, Direction = Solvelt.Api.Domain.MessageDirection.Buyer, Text = "mail me at jane.buyer@example.com or +1 555 987 6543", Reported = true, ReportReason = "spam", CreatedAt = DateTime.UtcNow };
        f.WithDb(db => { db.Conversations.Add(conv); db.Messages.Add(msg); db.SaveChanges(); return 0; });
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var q = await mod.JsonBody(await mod.Get("/api/v1/admin/moderation?pageSize=100"));
        var id = q.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("targetId").GetGuid() == msg.Id).GetProperty("id").GetGuid();
        var full = await (await mod.Get($"/api/v1/admin/moderation/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("jane.buyer@example.com", full);
        foreach (var role in new[] { StaffRole.ReadOnly, StaffRole.Support })
        {
            var api = await StaffKit.LoginNew(f, role);
            var text = await (await api.Get($"/api/v1/admin/moderation/{id}")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("jane.buyer@example.com", text); Assert.DoesNotContain("987 6543", text);
            Assert.Contains(msg.Id.ToString(), text, StringComparison.OrdinalIgnoreCase); // ids and dates stay intact
            var legacy = await (await api.Get("/api/v1/admin/messages/reported")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("jane.buyer@example.com", legacy);
        }
        Assert.Contains("jane.buyer@example.com", await (await mod.Get("/api/v1/admin/messages/reported")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_internal_reason_never_reaches_the_seller()
    {
        const string secret = "INTERNAL ticket 99 chargeback ring";
        var shop = await Orders.NewShop(f);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        async Task<string> Seen() { var j = await shop.Owner.Get("/api/v1/seller/application"); return await j.Content.ReadAsStringAsync(); }
        string NoteOf(Guid id) => f.WithDb(db => db.Sellers.Single(s => s.Id == id).StatusNote ?? "");
        string Note() => NoteOf(shop.Seller);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = secret })).StatusCode);
        Assert.DoesNotContain("INTERNAL", Note());
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = secret })).StatusCode);
        Assert.DoesNotContain("INTERNAL", Note());
        Assert.DoesNotContain("INTERNAL", await Seen());
        // the reason is kept for staff in the audit log
        Assert.Contains(f.WithDb(db => db.AuditEvents.Where(e => e.Type == "admin.seller.suspended").ToList()), e => e.Reason == secret);
        // applications: approve keeps the reason internal; needs-info shows the message written FOR the seller; reject only shows an explicit message
        var (api, _, seller) = await TeamHelpers.NewOwner(f); await api.CompleteApplicationAndSubmit();
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/needs-info", new { message = "Please upload a clearer certificate." })).StatusCode);
        Assert.Contains("clearer certificate", NoteOf(seller));
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/approve", new { reason = secret })).StatusCode);
        Assert.DoesNotContain("INTERNAL", NoteOf(seller));
        var (api2, _, seller2) = await TeamHelpers.NewOwner(f); await api2.CompleteApplicationAndSubmit();
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller2}/reject", new { reason = secret })).StatusCode);
        Assert.DoesNotContain("INTERNAL", f.WithDb(db => db.Sellers.Single(s => s.Id == seller2).StatusNote ?? ""));
    }

    [Fact]
    public async Task Staff_tokens_work_on_admin_routes_only()
    {
        var staff = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        Assert.Equal(HttpStatusCode.OK, (await staff.Get("/api/v1/admin/auth/me")).StatusCode);
        foreach (var url in new[] { "/api/v1/auth/session", "/api/v1/account/me", "/api/v1/account/sessions", "/api/v1/seller/application", "/api/v1/team/members" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Post("/api/v1/session/account", new { sellerId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Post("/api/v1/account/seller-accounts", new { acceptTerms = true })).StatusCode);
    }
}
