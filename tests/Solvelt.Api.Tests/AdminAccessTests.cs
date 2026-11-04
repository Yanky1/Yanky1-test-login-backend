using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class AdminAccessTests(OrdersFactory f) : IClassFixture<OrdersFactory>
{
    private static readonly string G = Guid.NewGuid().ToString();

    // Every admin endpoint with the permission it needs and a body that gets past validation (random ids answer 404/409/422, never a permission error).
    private static readonly (string Method, string Url, StaffPermission Perm, object? Body)[] Endpoints =
    [
        ("GET", "/api/v1/admin/auth/me", StaffPermission.DashboardView, null),
        ("GET", "/api/v1/admin/dashboard", StaffPermission.DashboardView, null),
        ("GET", "/api/v1/admin/applications", StaffPermission.ApplicationsView, null),
        ("GET", $"/api/v1/admin/applications/{G}", StaffPermission.ApplicationsView, null),
        ("GET", $"/api/v1/admin/applications/{G}/files/{G}", StaffPermission.ApplicationsView, null),
        ("POST", $"/api/v1/admin/applications/{G}/approve", StaffPermission.ApplicationsDecide, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/applications/{G}/needs-info", StaffPermission.ApplicationsDecide, new { message = "Please upload a clearer certificate" }),
        ("POST", $"/api/v1/admin/applications/{G}/reject", StaffPermission.ApplicationsDecide, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/applications/{G}/assign", StaffPermission.ApplicationsAssign, new { assignTo = (Guid?)null, reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/applications/{G}/notes", StaffPermission.ApplicationsDecide, new { text = "internal note" }),
        ("GET", "/api/v1/admin/sellers", StaffPermission.SellersView, null),
        ("GET", $"/api/v1/admin/sellers/{G}", StaffPermission.SellersView, null),
        ("POST", $"/api/v1/admin/sellers/{G}/suspend", StaffPermission.SellersSuspend, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/sellers/{G}/reactivate", StaffPermission.SellersSuspend, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/sellers/{G}/close", StaffPermission.SellersClose, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/sellers/{G}/resend-verification", StaffPermission.SellersResendVerification, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/sellers/{G}/transfer-ownership", StaffPermission.SellersTransferOwnership, new { newOwnerUserId = G, reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/users?q=abcd", StaffPermission.UsersView, null),
        ("GET", $"/api/v1/admin/users/{G}", StaffPermission.UsersView, null),
        ("POST", $"/api/v1/admin/users/{G}/sign-out-everywhere", StaffPermission.UsersAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/users/{G}/clear-lockout", StaffPermission.UsersAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/users/{G}/send-password-reset", StaffPermission.UsersAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/users/{G}/remove-phone", StaffPermission.UsersAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/users/{G}/disable-2fa", StaffPermission.UsersDisable2fa, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/moderation", StaffPermission.ModerationView, null),
        ("GET", $"/api/v1/admin/moderation/{G}", StaffPermission.ModerationView, null),
        ("POST", $"/api/v1/admin/moderation/{G}/dismiss", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/moderation/{G}/warn-seller", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/moderation/{G}/escalate-suspend", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("GET", $"/api/v1/admin/listings/{G}", StaffPermission.ModerationView, null),
        ("POST", $"/api/v1/admin/listings/{G}/hide", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/listings/{G}/unhide", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/lookups/orders?q=SO-2026", StaffPermission.LookupsView, null),
        ("GET", $"/api/v1/admin/lookups/orders/{G}", StaffPermission.LookupsView, null),
        ("POST", $"/api/v1/admin/lookups/orders/{G}/reveal", StaffPermission.LookupsReveal, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/outbox", StaffPermission.IntegrationsView, null),
        ("POST", $"/api/v1/admin/outbox/{G}/retry", StaffPermission.IntegrationsAct, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/messages/reported", StaffPermission.ModerationView, null),
        ("POST", $"/api/v1/admin/messages/{G}/dismiss-report", StaffPermission.ModerationAct, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/integrations/outbox", StaffPermission.IntegrationsView, null),
        ("GET", $"/api/v1/admin/integrations/outbox/{G}", StaffPermission.IntegrationsView, null),
        ("POST", $"/api/v1/admin/integrations/outbox/{G}/retry", StaffPermission.IntegrationsAct, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/integrations/outbox/{G}/discard", StaffPermission.IntegrationsDiscard, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/integrations/inbound", StaffPermission.IntegrationsView, null),
        ("GET", "/api/v1/admin/integrations/phone", StaffPermission.IntegrationsView, null),
        ("GET", "/api/v1/admin/integrations/jobs", StaffPermission.IntegrationsView, null),
        ("GET", "/api/v1/admin/audit", StaffPermission.AuditView, null),
        ("GET", "/api/v1/admin/audit/export", StaffPermission.AuditExport, null),
        ("GET", "/api/v1/admin/settings", StaffPermission.SettingsView, null),
        ("PUT", "/api/v1/admin/settings/fee", StaffPermission.SettingsEdit, new { feeBasisPoints = 1000, reason = StaffKit.Why }),
        ("PUT", "/api/v1/admin/settings/legal", StaffPermission.SettingsEdit, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/settings/seller-fields", StaffPermission.SettingsView, null),
        ("POST", "/api/v1/admin/settings/seller-fields/preview", StaffPermission.SettingsView, new { }),
        ("PUT", "/api/v1/admin/settings/seller-fields", StaffPermission.SettingsEdit, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/staff", StaffPermission.StaffView, null),
        ("POST", "/api/v1/admin/staff/invitations", StaffPermission.StaffManage, new { email = "matrix-invitee@example.com", role = "ReadOnly", reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/staff/invitations/{G}/revoke", StaffPermission.StaffManage, new { reason = StaffKit.Why }),
        ("PUT", $"/api/v1/admin/staff/{G}/role", StaffPermission.StaffManage, new { role = "Support", reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/staff/{G}/deactivate", StaffPermission.StaffManage, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/staff/{G}/reactivate", StaffPermission.StaffManage, new { reason = StaffKit.Why }),
        // payments (docs/PAYMENTS.md)
        ("GET", $"/api/v1/admin/payments/sellers/{G}", StaffPermission.PaymentsView, null),
        ("GET", $"/api/v1/admin/payments/sellers/{G}/ledger", StaffPermission.PaymentsView, null),
        ("POST", $"/api/v1/admin/payments/sellers/{G}/hold", StaffPermission.PaymentsHold, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/payments/sellers/{G}/hold/release", StaffPermission.PaymentsHold, new { reason = StaffKit.Why }),
        ("POST", $"/api/v1/admin/payments/sellers/{G}/adjustments", StaffPermission.PaymentsAdjust, new { direction = "credit", amountCents = 100, currency = "USD", reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/payments/transfers", StaffPermission.PaymentsView, null),
        ("POST", $"/api/v1/admin/payments/transfers/{G}/retry", StaffPermission.PaymentsRetry, new { reason = StaffKit.Why }),
        ("GET", "/api/v1/admin/payments/payouts", StaffPermission.PaymentsView, null),
        ("GET", "/api/v1/admin/payments/accounts/requirements-past-due", StaffPermission.PaymentsView, null),
        ("GET", "/api/v1/admin/payments/reconciliation", StaffPermission.PaymentsView, null),
        ("POST", "/api/v1/admin/payments/reconciliation/run", StaffPermission.PaymentsRetry, null),
        ("POST", $"/api/v1/admin/payments/reconciliation/{G}/resolve", StaffPermission.PaymentsRetry, new { reason = StaffKit.Why }),
        // shipping (docs/SHIPPING.md)
        ("GET", $"/api/v1/admin/shipping/sellers/{G}/shipments", StaffPermission.ShippingView, null),
        ("GET", $"/api/v1/admin/shipping/sellers/{G}/labels", StaffPermission.ShippingView, null),
        ("GET", "/api/v1/admin/shipping/refunds", StaffPermission.ShippingView, null),
        ("GET", "/api/v1/admin/shipping/exceptions", StaffPermission.ShippingView, null),
        ("GET", "/api/v1/admin/shipping/webhooks/failures", StaffPermission.ShippingView, null),
        ("POST", $"/api/v1/admin/shipping/labels/{G}/refund/retry", StaffPermission.ShippingRetry, new { reason = StaffKit.Why }),
    ];

    private static HttpRequestMessage Req((string Method, string Url, StaffPermission Perm, object? Body) e) =>
        new(new HttpMethod(e.Method), e.Url) { Content = e.Body is null ? null : JsonContent.Create(e.Body) };

    [Theory]
    [InlineData(StaffRole.SuperAdmin)] [InlineData(StaffRole.Support)] [InlineData(StaffRole.Moderator)] [InlineData(StaffRole.ReadOnly)]
    public async Task Role_matrix_every_role_against_every_endpoint(StaffRole role)
    {
        var (api, _) = await StaffKit.LoginSteppedUp(f, role);
        var wrong = new List<string>();
        foreach (var e in Endpoints)
        {
            var res = await api.Send(Req(e));
            var code = (int)res.StatusCode;
            if (StaffPermissions.Has(role, e.Perm)) { if (code is 401 or 403 or 429) wrong.Add($"{role} should be allowed {e.Method} {e.Url} but got {code}"); }
            else if (code != 403 || await StaffKit.Err(res) != "forbidden") wrong.Add($"{role} should be forbidden {e.Method} {e.Url} but got {code}");
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void Matrix_table_covers_every_admin_route()
    {
        static string Norm(string s) => Regex.Replace(Regex.Replace(Regex.Replace(s.Split('?')[0], "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}", "{}"), "lookups/(orders|bookings|quotes|conversations)", "lookups/{}"), @"\{[^}]*\}", "{}").Trim('/');
        var mapped = f.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/v1/admin/") && !e.RoutePattern.RawText.Contains("/auth/login") && !e.RoutePattern.RawText.Contains("/2fa/")
                && !e.RoutePattern.RawText.Contains("accept-invite") && !e.RoutePattern.RawText.Contains("step-up") && !e.RoutePattern.RawText.EndsWith("/logout")
                && !e.RoutePattern.RawText.Contains("/sellers/{id:guid}/status")) // the one legacy Admin-role API kept as it was (seller-status)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {Norm(e.RoutePattern.RawText!)}")).ToHashSet();
        var covered = Endpoints.Select(e => $"{e.Method} {Norm(e.Url)}").ToHashSet();
        Assert.Empty(mapped.Except(covered));
        Assert.Empty(covered.Except(mapped));
    }

    [Fact]
    public async Task Staff_sign_in_needs_the_second_factor_and_never_reveals_which_part_failed()
    {
        var api = new Api(f);
        var noMfa = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        var r1 = await api.Post("/api/v1/admin/auth/login", new { email = noMfa, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        Assert.Equal("two_factor_setup_required", await StaffKit.Err(r1));
        var email = await StaffKit.Create(f, StaffRole.Support);
        var r2 = await api.Post("/api/v1/admin/auth/login", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        Assert.True((await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffKit.TryLogin(f, api, email, code: "000000")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffKit.TryLogin(f, api, email, password: "Wr0ng-Password-Here!")).StatusCode);
        // a seller (not staff) with the right password gets the same generic answer
        var seller = new Api(f); var sellerEmail = await seller.SignupVerified();
        var r3 = await api.Post("/api/v1/admin/auth/login", new { email = sellerEmail, password = ApiFactory.Strong, totpCode = "123456" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StaffKit.TryLogin(f, api, email)).StatusCode);
    }

    [Fact]
    public async Task Staff_can_enroll_two_factor_with_password_then_sign_in()
    {
        var email = await StaffKit.Create(f, StaffRole.Moderator, twoFactor: false);
        var api = new Api(f);
        var setup = await api.Post("/api/v1/admin/auth/2fa/setup", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var j = await setup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("otpauth://totp/", j.GetProperty("otpAuthUri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/admin/auth/2fa/enable", new { email, password = ApiFactory.Strong, code = "000000" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/admin/auth/2fa/setup", new { email, password = "Wr0ng-Password-Here!" }, false)).StatusCode);
        var en = await api.Post("/api/v1/admin/auth/2fa/enable", new { email, password = ApiFactory.Strong, code = await StaffKit.Totp(f, email) }, false);
        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
        Assert.Equal(10, (await en.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await StaffKit.TryLogin(f, api, email)).StatusCode);
    }

    [Fact]
    public async Task Seller_and_legacy_admin_tokens_never_reach_admin_endpoints()
    {
        var shop = await Orders.NewShop(f);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.Owner.Get("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal("not_staff_session", await StaffKit.Err(await shop.Owner.Get("/api/v1/admin/applications")));
        // the seeded Admin-role user signing in the ordinary way gets a seller-kind session: still no admin access
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var res = await admin.Get("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("not_staff_session", await StaffKit.Err(res));
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get("/api/v1/admin/dashboard", false)).StatusCode);
        // ...while the existing admin seller-status API keeps working with that session
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/v1/admin/sellers/{shop.Seller}/status", new { status = "Suspended" })).StatusCode);
    }

    [Fact]
    public async Task Session_without_second_factor_is_refused()
    {
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var api = await StaffKit.Login(f, email);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);
        f.WithDb(db => { db.Sessions.Where(s => s.UserId == StaffKit.UserId(f, email) && s.Kind == "staff").ToList().ForEach(s => s.MfaAt = null); db.SaveChanges(); return 0; });
        var res = await api.Get("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("two_factor_required", await StaffKit.Err(res));
    }

    [Fact]
    public async Task Staff_session_is_separate_from_the_seller_context()
    {
        // a staff member who ALSO owns a seller account: the staff token carries no seller account and no seller roles
        var seller = new Api(f); var email = await seller.SignupVerified();
        await StaffKit.Create(f, StaffRole.Support, email);
        var staff = await StaffKit.Login(f, email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Get("/api/v1/seller/products")).StatusCode); // a staff token is valid on /admin only
        Assert.Equal(HttpStatusCode.OK, (await staff.Get("/api/v1/admin/dashboard")).StatusCode);
        // and the seller token of the same person cannot use admin
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.Get("/api/v1/admin/dashboard")).StatusCode);
        // staff sessions are never refreshed
        var sid = f.WithDb(db => db.Sessions.First(s => s.UserId == StaffKit.UserId(f, email) && s.Kind == "staff").Id);
        Assert.Equal(0, f.WithDb(db => db.RefreshTokens.Count(t => t.SessionId == sid)));
    }

    [Fact]
    public async Task Staff_sessions_have_a_short_idle_timeout_and_an_absolute_lifetime()
    {
        var email = await StaffKit.Create(f, StaffRole.Support);
        var api = await StaffKit.Login(f, email);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(16)); // idle limit 15 (seller sessions allow 20)
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/admin/dashboard")).StatusCode);

        var api2 = await StaffKit.Login(f, email);
        for (var i = 0; i < 5; i++) { f.Time.Advance(TimeSpan.FromMinutes(11)); Assert.Equal(HttpStatusCode.OK, (await api2.Get("/api/v1/admin/dashboard")).StatusCode); } // 55 min of continuous activity
        f.Time.Advance(TimeSpan.FromMinutes(6)); // 61 min: the session ends although it was never idle
        Assert.Equal(HttpStatusCode.Unauthorized, (await api2.Get("/api/v1/admin/dashboard")).StatusCode); // active, but past the absolute lifetime
    }

    [Fact]
    public async Task Sensitive_actions_need_a_recent_step_up()
    {
        var shop = await Orders.NewShop(f);
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var api = await StaffKit.Login(f, email);
        var res = await api.Post($"/api/v1/admin/sellers/{shop.Seller}/close", new { reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("step_up_required", await StaffKit.Err(res));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/admin/auth/step-up", new { code = "000000" })).StatusCode);
        await StaffKit.StepUp(f, api, email);
        var other = await Orders.NewShop(f);
        var closed = await api.Post($"/api/v1/admin/sellers/{other.Seller}/close", new { reason = StaffKit.Why });
        Assert.True(closed.StatusCode == HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        f.Time.Advance(TimeSpan.FromMinutes(6));
        var third = await Orders.NewShop(f);
        Assert.Equal("step_up_required", await StaffKit.Err(await api.Post($"/api/v1/admin/sellers/{third.Seller}/close", new { reason = StaffKit.Why })));
    }

    [Fact]
    public async Task Every_write_needs_a_reason()
    {
        var (api, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        foreach (var body in new object[] { new { }, new { reason = "" }, new { reason = "no" }, new { reason = new string('x', 501) } })
        {
            var res = await api.Post($"/api/v1/admin/users/{G}/sign-out-everywhere", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.StartsWith("reason_", await StaffKit.Err(res));
        }
    }

    [Fact]
    public async Task Staff_writes_are_audited_with_actor_role_target_and_reason()
    {
        var email = await StaffKit.Create(f, StaffRole.Support);
        var api = await StaffKit.Login(f, email);
        var seller = new Api(f); var target = await seller.SignupVerified();
        var tid = StaffKit.UserId(f, target);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Post($"/api/v1/admin/users/{tid}/sign-out-everywhere", new { reason = "Reported stolen laptop" })).StatusCode);
        var row = f.WithDb(db => db.AuditEvents.OrderByDescending(e => e.Id).First(e => e.Type == "admin.user.signed_out_everywhere"));
        Assert.Equal(StaffKit.UserId(f, email), row.UserId);
        Assert.Equal("Support", row.ActorRole);
        Assert.Equal("user", row.TargetType);
        Assert.Equal(tid.ToString(), row.TargetId);
        Assert.Equal("Reported stolen laptop", row.Reason);
        Assert.Equal(HttpStatusCode.Unauthorized, (await seller.Get("/api/v1/seller/application")).StatusCode); // sessions really ended
    }

    [Fact]
    public async Task Legacy_admin_role_is_backfilled_to_super_admin()
    {
        Assert.Equal(StaffRole.SuperAdmin, f.WithDb(db => db.StaffMembers.Single(s => s.UserId == db.Users.First(u => u.Email == ApiFactory.AdminEmail).Id).Role));
        // the seeded admin has no 2FA yet: staff sign-in asks for enrollment
        var r = await new Api(f).Post("/api/v1/admin/auth/login", new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword }, false);
        Assert.Equal("two_factor_setup_required", await StaffKit.Err(r));
    }
}

public class AdminIpAndRateTests
{
    [Fact]
    public async Task Ip_allowlist_blocks_staff_sign_in_and_endpoints_from_other_networks()
    {
        using var f = new ApiFactory();
        f.Extra["Admin:IpAllowlist:0"] = "203.0.113.0/24";
        var email = await StaffKit.Create(f, StaffRole.Support);
        var api = new Api(f);
        var res = await StaffKit.TryLogin(f, api, email);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("ip_not_allowed", await StaffKit.Err(res));
        Assert.Equal("ip_not_allowed", await StaffKit.Err(await api.Get("/api/v1/admin/dashboard", false)));
    }

    [Fact]
    public async Task Admin_writes_and_lookups_are_rate_limited()
    {
        using var f = new ApiFactory();
        f.Extra["Admin:WritesPerMinute"] = "3"; f.Extra["Admin:ReadsPerMinute"] = "5";
        var api = await StaffKit.LoginNew(f, StaffRole.Support);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await api.Post($"/api/v1/admin/users/{Guid.NewGuid()}/clear-lockout", new { reason = StaffKit.Why })).StatusCode);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.NotFound));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        var reads = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) reads.Add((await api.Get("/api/v1/admin/users?q=nobody")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, reads);
    }
}
