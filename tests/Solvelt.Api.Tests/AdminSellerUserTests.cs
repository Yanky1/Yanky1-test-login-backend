using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class AdminApplicationTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    /// <summary>A seller who finished the wizard (with a kosher certificate when asked) and is waiting for review.</summary>
    private async Task<(Api api, Guid seller, string email)> Submitted(bool certificate)
    {
        var (api, email, seller) = await TeamHelpers.NewOwner(f);
        if (certificate)
        {
            Assert.True((await api.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string> { ["sells_food"] = "yes", ["food_types"] = "Bakery", ["is_kosher"] = "yes", ["kosher_agency"] = "OU" } })).IsSuccessStatusCode);
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "cert.pdf");
            Assert.True((await api.Send(new HttpRequestMessage(HttpMethod.Post, "/api/v1/seller/application/kosher/certificate") { Content = form })).IsSuccessStatusCode);
            var s = await api.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true });
            Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        }
        else await api.CompleteApplicationAndSubmit();
        return (api, seller, email);
    }

    private static async Task<string> Status(Api seller) => (await seller.JsonBody(await seller.Get("/api/v1/seller/application"))).GetProperty("status").GetString()!;

    [Fact]
    public async Task Queue_filters_and_detail_show_everything_the_wizard_collected_and_the_certificate_is_served_safely()
    {
        var (_, seller, email) = await Submitted(certificate: true);
        f.Time.Advance(TimeSpan.FromDays(2));
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        var q = await mod.JsonBody(await mod.Get("/api/v1/admin/applications"));
        var row = q.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == seller);
        Assert.Equal("PendingReview", row.GetProperty("status").GetString());
        Assert.True(row.GetProperty("ageDays").GetDouble() >= 2);
        Assert.Equal(email, row.GetProperty("ownerEmail").GetString());
        string Ids(JsonElement j) => string.Join(',', j.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Contains(seller.ToString(), Ids(await mod.JsonBody(await mod.Get("/api/v1/admin/applications?country=us&minAgeDays=2"))));
        Assert.DoesNotContain(seller.ToString(), Ids(await mod.JsonBody(await mod.Get("/api/v1/admin/applications?country=FR"))));
        Assert.DoesNotContain(seller.ToString(), Ids(await mod.JsonBody(await mod.Get("/api/v1/admin/applications?minAgeDays=30"))));
        Assert.DoesNotContain(seller.ToString(), Ids(await mod.JsonBody(await mod.Get("/api/v1/admin/applications?status=Approved"))));

        var d = await mod.JsonBody(await mod.Get($"/api/v1/admin/applications/{seller}"));
        Assert.Equal("Acme Co", d.GetProperty("business").GetProperty("vendorName").GetString());
        Assert.Equal("Brooklyn", d.GetProperty("business").GetProperty("city").GetString());
        Assert.Equal("2026-01", d.GetProperty("legal").GetProperty("termsVersion").GetString());
        Assert.Equal("2026-01", d.GetProperty("legal").GetProperty("sellerAgreementVersion").GetString());
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("legal").GetProperty("sellerAgreementAcceptedAt").ValueKind);
        var answers = d.GetProperty("kosher").EnumerateArray().ToDictionary(k => k.GetProperty("key").GetString()!, k => k.GetProperty("answer").GetString());
        Assert.Equal("OU", answers["kosher_agency"]); Assert.Equal("yes", answers["is_kosher"]);
        var fileId = d.GetProperty("files").EnumerateArray().Single().GetProperty("id").GetGuid();

        var file = await mod.Get($"/api/v1/admin/applications/{seller}/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.StartsWith("inline;", file.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
        Assert.True(f.WithDb(db => db.AuditEvents.Any(e => e.Type == "admin.seller.file_viewed" && e.TargetId == fileId.ToString())));
        // another seller's file id is not reachable through this application
        var (_, other, _) = await Submitted(certificate: false);
        Assert.Equal(HttpStatusCode.NotFound, (await mod.Get($"/api/v1/admin/applications/{other}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mod.Get($"/api/v1/admin/applications/{seller}/files/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Approve_needs_info_and_reject_go_through_the_seller_state_machine()
    {
        var (api, seller, email) = await Submitted(false);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mod.Post($"/api/v1/admin/applications/{seller}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mod.Post($"/api/v1/admin/applications/{seller}/needs-info", new { message = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/needs-info", new { message = "Please upload a clearer certificate." })).StatusCode);
        Assert.Equal("NeedsInfo", await Status(api));
        Assert.Contains("Please upload a clearer certificate.", f.Mails(email, "needs-info").Single().Text);

        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/approve", new { reason = "Certificate verified with the agency" })).StatusCode);
        Assert.Equal("Approved", await Status(api));
        Assert.Single(f.Mails(email, "approved"));
        var again = await mod.Post($"/api/v1/admin/applications/{seller}/approve", new { reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // Approved -> Approved is not a transition
        Assert.Equal("invalid_transition", await StaffKit.Err(again));
        var audit = f.WithDb(db => db.AuditEvents.Single(e => e.Type == "admin.application.approved" && e.TargetId == seller.ToString()));
        Assert.Equal("Moderator", audit.ActorRole); Assert.Equal("Certificate verified with the agency", audit.Reason);
        Assert.Contains("PendingReview", f.WithDb(db => db.AuditEvents.Single(e => e.Type == "admin.application.needs_info" && e.TargetId == seller.ToString()).Detail));

        var (api2, seller2, email2) = await Submitted(false);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller2}/reject", new { reason = "Could not verify the business" })).StatusCode);
        Assert.Equal("Rejected", f.WithDb(db => db.Sellers.Single(x => x.Id == seller2).Status.ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api2.Get("/api/v1/seller/application")).StatusCode); // a rejected account ends its members' only session
        Assert.Single(f.Mails(email2, "rejected"));
        Assert.Equal(HttpStatusCode.Conflict, (await mod.Post($"/api/v1/admin/applications/{seller2}/approve", new { reason = StaffKit.Why })).StatusCode); // Rejected -> Approved is not allowed
        Assert.Equal(HttpStatusCode.NotFound, (await mod.Post($"/api/v1/admin/applications/{Guid.NewGuid()}/approve", new { reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Applications_can_be_assigned_to_reviewers_and_carry_internal_notes()
    {
        var (_, seller, _) = await Submitted(false);
        var modEmail = await StaffKit.Create(f, StaffRole.Moderator);
        var mod = await StaffKit.Login(f, modEmail);
        var supportId = StaffKit.UserId(f, await StaffKit.Create(f, StaffRole.Support));
        Assert.Equal(HttpStatusCode.BadRequest, (await mod.Post($"/api/v1/admin/applications/{seller}/assign", new { assignTo = supportId, reason = StaffKit.Why })).StatusCode);
        var me = StaffKit.UserId(f, modEmail);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/assign", new { assignTo = me, reason = "I'll take this one" })).StatusCode);
        var mine = await mod.JsonBody(await mod.Get("/api/v1/admin/applications?assigned=me"));
        Assert.Contains(mine.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == seller);
        var none = await mod.JsonBody(await mod.Get("/api/v1/admin/applications?assigned=none"));
        Assert.DoesNotContain(none.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == seller);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/applications/{seller}/notes", new { text = "Phoned the agency, waiting for a call back" })).StatusCode);
        var d = await mod.JsonBody(await mod.Get($"/api/v1/admin/applications/{seller}"));
        Assert.Equal(me, d.GetProperty("assignment").GetProperty("assignedTo").GetGuid());
        Assert.Contains("agency", d.GetProperty("detail").GetProperty("notes").EnumerateArray().Single().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Draft_applications_are_not_in_the_review_queue()
    {
        var (_, _, seller) = await TeamHelpers.NewOwner(f);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.NotFound, (await mod.Get($"/api/v1/admin/applications/{seller}")).StatusCode);
    }
}

public class AdminSellerAccountTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    [Fact]
    public async Task Search_and_detail_show_members_brands_counts_and_status_history()
    {
        var shop = await Orders.NewShop(f);
        await Orders.Place(shop);
        var (admin, _) = await TeamHelpers.JoinAs(f, shop.Owner, "Admin");
        var ro = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        var byId = await ro.JsonBody(await ro.Get($"/api/v1/admin/sellers?q={shop.Seller}"));
        Assert.Equal(shop.Seller, byId.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Contains(shop.Seller.ToString(), (await ro.Get("/api/v1/admin/sellers?q=acme&status=Approved")).Content.ReadAsStringAsync().Result);
        Assert.Equal(HttpStatusCode.BadRequest, (await ro.Get("/api/v1/admin/sellers?status=Nope")).StatusCode);
        var d = await ro.JsonBody(await ro.Get($"/api/v1/admin/sellers/{shop.Seller}"));
        Assert.Equal(2, d.GetProperty("members").GetArrayLength());
        Assert.Contains(d.GetProperty("members").EnumerateArray(), m => m.GetProperty("role").GetString() == "Owner");
        Assert.Equal(1, d.GetProperty("counts").GetProperty("orders").GetInt32());
        Assert.Equal(1, d.GetProperty("counts").GetProperty("activeListings").GetInt32());
        Assert.True(d.GetProperty("brands").GetArrayLength() >= 1);
        Assert.Contains(d.GetProperty("statusHistory").EnumerateArray(), h => h.GetProperty("type").GetString() == "seller.status_changed" && h.GetProperty("detail").GetString()!.Contains("->Approved"));
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("recentActivity").GetProperty("lastOrderAt").ValueKind);
    }

    [Fact]
    public async Task Suspend_ends_member_sessions_and_reactivate_restores_the_previous_status()
    {
        var shop = await Orders.NewShop(f);
        var mod = await StaffKit.LoginNew(f, StaffRole.Moderator);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = "Counterfeit complaints" })).StatusCode);
        Assert.Equal("Suspended", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.Owner.Get("/api/v1/seller/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = StaffKit.Why })).StatusCode); // already suspended
        Assert.Equal(HttpStatusCode.OK, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = "Appeal accepted" })).StatusCode);
        Assert.Equal("Approved", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));

        // an account that was Live goes back to Live
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        await admin.Post($"/api/v1/admin/sellers/{shop.Seller}/status", new { status = "Live" });
        await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = "Second incident" });
        await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = "Resolved" });
        Assert.Equal("Live", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await mod.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = StaffKit.Why })).StatusCode);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.Post($"/api/v1/admin/sellers/{shop.Seller}/suspend", new { reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Close_account_needs_step_up_ends_access_and_emails_the_owners()
    {
        var shop = await Orders.NewShop(f);
        var ownerEmail = shop.Owner.Session!.Email;
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/close", new { reason = "Owner requested closure by phone" })).StatusCode);
        Assert.Equal("Closed", f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).Status.ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.Owner.Get("/api/v1/seller/products")).StatusCode);
        Assert.Single(f.Mails(ownerEmail, "account-closed-by-staff"));
        Assert.Equal(HttpStatusCode.Conflict, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/close", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/reactivate", new { reason = StaffKit.Why })).StatusCode); // closed stays closed
    }

    [Fact]
    public async Task Emergency_ownership_transfer_emails_both_owners_and_needs_step_up()
    {
        var shop = await Orders.NewShop(f);
        var (member, memberEmail) = await TeamHelpers.JoinAs(f, shop.Owner, "Admin");
        var memberId = StaffKit.UserId(f, memberEmail);
        var ownerEmail = shop.Owner.Session!.Email;
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/transfer-ownership", new { newOwnerUserId = memberId, reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/transfer-ownership", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/transfer-ownership", new { newOwnerUserId = Guid.NewGuid(), reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/transfer-ownership", new { newOwnerUserId = memberId, reason = "Owner unreachable, ticket 77" })).StatusCode);
        var roles = f.WithDb(db => db.Memberships.Where(m => m.SellerId == shop.Seller).ToDictionary(m => m.UserId, m => m.Role));
        Assert.Equal(AccountRole.Owner, roles[memberId]);
        Assert.Equal(AccountRole.Admin, roles[StaffKit.UserId(f, ownerEmail)]);
        Assert.Equal(memberId, f.WithDb(db => db.Sellers.Single(s => s.Id == shop.Seller).UserId));
        Assert.Single(f.Mails(memberEmail, "team-ownership").Where(m => m.Text.Contains("now an Owner")));
        Assert.Single(f.Mails(ownerEmail, "team-ownership").Where(m => m.Text.Contains("was transferred")));
        Assert.Equal(HttpStatusCode.Conflict, (await sa.Post($"/api/v1/admin/sellers/{shop.Seller}/transfer-ownership", new { newOwnerUserId = memberId, reason = StaffKit.Why })).StatusCode);
    }

    [Fact]
    public async Task Resend_owner_verification_only_for_unverified_owners()
    {
        var api = new Api(f); var email = await api.UnverifiedSession();
        var seller = f.WithDb(db => db.Sellers.Single(s => s.UserId == db.Users.First(u => u.Email == email).Id).Id);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        var before = f.Mails(email, "verify-code").Count;
        var tooSoon = await support.Post($"/api/v1/admin/sellers/{seller}/resend-verification", new { reason = "Seller says code never arrived" });
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode); // the 60 s resend cooldown of the sign-up mail still applies
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/sellers/{seller}/resend-verification", new { reason = "Seller says code never arrived" })).StatusCode);
        Assert.True(f.Mails(email, "verify-code").Count > before);
        var shop = await Orders.NewShop(f);
        Assert.Equal("already_verified", await StaffKit.Err(await support.Post($"/api/v1/admin/sellers/{shop.Seller}/resend-verification", new { reason = StaffKit.Why })));
    }
}

public class AdminUserTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private void Db(Action<AppDbContext> a) => f.WithDb(db => { a(db); db.SaveChanges(); return 0; });

    [Fact]
    public async Task Lookup_by_email_phone_and_id_and_detail_never_leaks_secrets()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var id = StaffKit.UserId(f, email);
        var phone = "+15551230" + Random.Shared.Next(100, 999);
        Db(db => { var u = db.Users.First(x => x.Id == id); u.PhoneNumber = phone; u.PhoneNumberConfirmed = true; });
        var support = await StaffKit.LoginNew(f, StaffRole.ReadOnly);
        foreach (var q in new[] { email, email[..12], phone, id.ToString() })
        {
            var j = await support.JsonBody(await support.Get($"/api/v1/admin/users?q={Uri.EscapeDataString(q)}"));
            Assert.True(j.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == id), "lookup by: " + q + " -> " + j);
        }
        Assert.Empty((await support.JsonBody(await support.Get("/api/v1/admin/users?q=ab"))).GetProperty("items").EnumerateArray()); // too short to scan
        var resp = await support.Get($"/api/v1/admin/users/{id}");
        var text = await resp.Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(text).RootElement;
        Assert.Equal(email, d.GetProperty("email").GetString());
        Assert.DoesNotContain(phone, text);                                  // phone is masked
        Assert.EndsWith(phone[^2..], d.GetProperty("phone").GetString());
        Assert.Equal(1, d.GetProperty("memberships").GetArrayLength());
        Assert.True(d.GetProperty("sessions").GetArrayLength() >= 1);
        Assert.False(d.GetProperty("security").GetProperty("twoFactorEnabled").GetBoolean());
        Assert.False(d.GetProperty("lockout").GetProperty("locked").GetBoolean());
        Assert.Contains(d.GetProperty("recentSecurityEvents").EnumerateArray(), e => e.GetProperty("type").GetString() == "email.verified");
        var hash = f.WithDb(db => db.Users.First(x => x.Id == id).PasswordHash!);
        foreach (var secret in new[] { hash, f.WithDb(db => db.Users.First(x => x.Id == id).SecurityStamp!), "passwordHash", "securityStamp", "authenticatorKey", "recoveryCode", "tokenHash", "refreshToken" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_all_sessions()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var second = new Api(f); await second.LoginAs(email, ApiFactory.Strong);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/users/{StaffKit.UserId(f, email)}/sign-out-everywhere", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await seller.Get("/api/v1/seller/application")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.Get("/api/v1/seller/application")).StatusCode);
    }

    [Fact]
    public async Task Clear_lockout_lets_a_locked_user_sign_in_again()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var c = new Api(f);
        for (var i = 0; i < 5; i++) await c.Post("/api/v1/auth/login", new { email, password = "Wr0ng-Password-Here!", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false)).StatusCode);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        var id = StaffKit.UserId(f, email);
        Assert.True((await support.JsonBody(await support.Get($"/api/v1/admin/users/{id}"))).GetProperty("lockout").GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/users/{id}/clear-lockout", new { reason = "Verified identity by phone" })).StatusCode);
        Assert.False((await support.JsonBody(await support.Get($"/api/v1/admin/users/{id}"))).GetProperty("lockout").GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await c.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false)).StatusCode);
    }

    [Fact]
    public async Task Send_password_reset_mails_the_user_and_the_link_works()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/users/{StaffKit.UserId(f, email)}/send-password-reset", new { reason = StaffKit.Why })).StatusCode);
        var mail = f.Mails(email, "reset-password").Single();
        var token = System.Text.RegularExpressions.Regex.Match(mail.Text, @"token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.Equal(HttpStatusCode.NoContent, (await new Api(f).Post("/api/v1/auth/reset-password", new { email, token, newPassword = "Brand-N3w-Passphrase!" }, false)).StatusCode);
    }

    [Fact]
    public async Task Remove_phone_deletes_number_and_pin_and_audits_only_the_masked_number()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var id = StaffKit.UserId(f, email);
        var phone = "+15559870" + Random.Shared.Next(100, 999);
        Db(db => { var u = db.Users.First(x => x.Id == id); u.PhoneNumber = phone; u.PhoneNumberConfirmed = true; db.PhoneCredentials.Add(new PhoneCredential { UserId = id, PinHash = "x", SetAt = DateTime.UtcNow }); });
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.NoContent, (await support.Post($"/api/v1/admin/users/{id}/remove-phone", new { reason = StaffKit.Why })).StatusCode);
        Assert.Null(f.WithDb(db => db.Users.First(x => x.Id == id).PhoneNumber));
        Assert.False(f.WithDb(db => db.PhoneCredentials.Any(c => c.UserId == id)));
        Assert.Equal(HttpStatusCode.Conflict, (await support.Post($"/api/v1/admin/users/{id}/remove-phone", new { reason = StaffKit.Why })).StatusCode);
        Assert.DoesNotContain(f.WithDb(db => db.AuditEvents.Where(e => e.Type == "admin.user.phone_removed").Select(e => e.Detail).ToList()), d => d!.Contains(phone));
        Assert.Single(f.Mails(email, "phone-removed"));
    }

    [Fact]
    public async Task Disable_two_factor_is_last_resort_super_admin_step_up_reason_and_the_user_is_emailed()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var id = StaffKit.UserId(f, email);
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.Post($"/api/v1/admin/users/{id}/disable-2fa", new { reason = StaffKit.Why })).StatusCode);
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Post($"/api/v1/admin/users/{id}/disable-2fa", new { reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal("not_enabled", await StaffKit.Err(await sa.Post($"/api/v1/admin/users/{id}/disable-2fa", new { reason = StaffKit.Why })));
        // enable 2FA the normal way, then lose the device
        var setup = await seller.JsonBody(await seller.Post("/api/v1/account/2fa/setup"));
        var code = StaffKit.Rfc6238(setup.GetProperty("sharedKey").GetString()!, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await seller.Post("/api/v1/account/2fa/enable", new { code })).StatusCode);
        Assert.True(f.WithDb(db => db.Users.First(x => x.Id == id).TwoFactorEnabled));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Post($"/api/v1/admin/users/{id}/disable-2fa", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sa.Post($"/api/v1/admin/users/{id}/disable-2fa", new { reason = "Lost phone, identity verified with ID" })).StatusCode);
        Assert.False(f.WithDb(db => db.Users.First(x => x.Id == id).TwoFactorEnabled));
        Assert.Single(f.Mails(email, "2fa-disabled-by-staff"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await seller.Get("/api/v1/seller/application")).StatusCode); // signed out everywhere
        var self = await sa.Post($"/api/v1/admin/users/{StaffKit.UserId(f, saEmail)}/disable-2fa", new { reason = StaffKit.Why });
        Assert.Equal("self_change", await StaffKit.Err(self));
    }
}
