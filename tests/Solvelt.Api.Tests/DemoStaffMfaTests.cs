using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Api.Admin;
using Solvelt.Api.Demo;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

/// <summary>Test sites only: Demo:Enabled AND Demo:AllowStaffMfaSkip let ONE staff session skip two-factor after the password step (docs/ADMIN.md, DECISIONS Q1-Q6).</summary>
public class DemoStaffMfaFactory : ApiFactory
{
    public DemoStaffMfaFactory() { Extra["Demo:Enabled"] = "true"; Extra["Demo:AllowStaffMfaSkip"] = "true"; Extra["Demo:PerHour"] = "100"; }
}

public static class MfaSkipKit
{
    public const string Route = "/api/v1/admin/auth/demo-mfa";

    public static async Task<(Api api, HttpResponseMessage res)> Skip(ApiFactory f, string email, string? password = null)
    {
        var api = new Api(f);
        var res = await api.Post(Route, new { email, password = password ?? ApiFactory.Strong }, false);
        if (res.StatusCode == HttpStatusCode.OK)
        {
            var j = await res.Content.ReadFromJsonAsync<JsonElement>();
            api.SetSession(new Login("Bearer", j.GetProperty("accessToken").GetString()!, j.GetProperty("expiresIn").GetInt32(), "", Guid.Empty, email, "", "Active", []));
        }
        return (api, res);
    }

    public static int Deactivate(ApiFactory f, string email)
    {
        var uid = StaffKit.UserId(f, email);
        return f.WithDb(db => db.StaffMembers.Where(s => s.UserId == uid).ExecuteUpdate(s => s.SetProperty(x => x.Active, false)));
    }

    public static int Sessions(ApiFactory f, string email, string? method = null)
    {
        var uid = StaffKit.UserId(f, email);
        return f.WithDb(db => db.Sessions.Count(s => s.UserId == uid && (method == null || s.Method == method)));
    }

    public static DemoOptions Options(ApiFactory f) => f.Services.GetRequiredService<IOptions<DemoOptions>>().Value;

    public static (bool TwoFactor, string? Key) TwoFactorState(ApiFactory f, string email)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = users.FindByEmailAsync(email).GetAwaiter().GetResult()!;
        return (u.TwoFactorEnabled, users.GetAuthenticatorKeyAsync(u).GetAwaiter().GetResult());
    }
}

public class DemoStaffMfaGateTests
{
    private sealed class ProdFactory : ApiFactory
    {
        public ProdFactory(params (string, string)[] extra) { foreach (var (k, v) in extra) Extra[k] = v; }
        protected override void ConfigureWebHost(IWebHostBuilder builder) { base.ConfigureWebHost(builder); builder.UseEnvironment("Production"); }
    }

    [Theory]
    [InlineData("Demo:AllowStaffMfaSkip", "Demo:AllowStaffMfaSkip must never be true in the Production environment")]          // the skip flag alone
    [InlineData("both", "must never be true in the Production environment")]                                                    // both flags
    [InlineData("Demo:Enabled", "Demo:Enabled must never be true in the Production environment")]                              // the demo flag alone (unchanged)
    public void The_app_refuses_to_start_in_Production_with_either_flag_on(string which, string message)
    {
        var extra = which == "both" ? new[] { ("Demo:Enabled", "true"), ("Demo:AllowStaffMfaSkip", "true") } : new[] { (which, "true") };
        using var prod = new ProdFactory(extra);
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        Assert.Contains(message, ex.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]      // Demo:Enabled alone is not enough
    [InlineData(false, true)]      // neither is the skip flag alone
    public async Task The_endpoints_do_not_exist_and_the_config_says_so_unless_both_flags_are_on(bool demo, bool skip)
    {
        using var f = new ApiFactory();
        if (demo) f.Extra["Demo:Enabled"] = "true";
        if (skip) f.Extra["Demo:AllowStaffMfaSkip"] = "true";
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin, twoFactor: false);
        var api = new Api(f);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(MfaSkipKit.Route, new { email, password = ApiFactory.Strong }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post("/api/v1/admin/auth/demo-step-up", new { }, false)).StatusCode);
        var cfg = await api.JsonBody(await api.Get("/api/v1/public-config", false));
        Assert.False(cfg.GetProperty("staffMfaSkipEnabled").GetBoolean());
        Assert.Equal(demo, cfg.GetProperty("demoEnabled").GetBoolean());
    }

    [Fact]
    public async Task The_config_reports_the_skip_only_when_both_flags_are_on()
    {
        using var f = new DemoStaffMfaFactory();
        var cfg = await new Api(f).JsonBody(await new Api(f).Get("/api/v1/public-config", false));
        Assert.True(cfg.GetProperty("staffMfaSkipEnabled").GetBoolean());
        Assert.True(cfg.GetProperty("demoEnabled").GetBoolean());
    }
}

public class DemoStaffMfaTests(DemoStaffMfaFactory f) : IClassFixture<DemoStaffMfaFactory>
{
    [Fact]
    public async Task Password_then_skip_opens_the_admin_and_satisfies_step_up_for_that_session_only_without_touching_the_account()
    {
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin, twoFactor: false);       // no authenticator at all
        Assert.Equal((false, null), MfaSkipKit.TwoFactorState(f, email));
        // without the skip a staff member without 2FA cannot sign in
        Assert.Equal("two_factor_setup_required", await StaffKit.Err(await new Api(f).Post("/api/v1/admin/auth/login", new { email, password = ApiFactory.Strong }, false)));

        var (api, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await api.JsonBody(await api.Get("/api/v1/admin/auth/me"))).GetProperty("mfaSkipped").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);
        // a step-up action works with no code (settings edit needs a recent step-up for a normal session)
        Assert.Equal(HttpStatusCode.OK, (await api.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1100, reason = StaffKit.Why })).StatusCode);
        // the account's two-factor state is unchanged: still no secret, still not enabled
        Assert.Equal((false, null), MfaSkipKit.TwoFactorState(f, email));
        // and the skip did not turn that account into one that can sign in normally
        Assert.Equal("two_factor_setup_required", await StaffKit.Err(await new Api(f).Post("/api/v1/admin/auth/login", new { email, password = ApiFactory.Strong }, false)));
        // the session is marked, not the user
        Assert.Equal(1, MfaSkipKit.Sessions(f, email, "staff-demo-mfa"));
        Assert.Equal(0, MfaSkipKit.Sessions(f, email, "staff-2fa"));
    }

    [Fact]
    public async Task A_normal_session_of_the_same_account_still_needs_its_code_for_step_up()
    {
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);                          // real 2FA
        var before = MfaSkipKit.TwoFactorState(f, email);
        var (skipped, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await skipped.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1200, reason = StaffKit.Why })).StatusCode);
        var normal = await StaffKit.Login(f, email);
        Assert.Equal("step_up_required", await StaffKit.Err(await normal.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1300, reason = StaffKit.Why })));
        Assert.False((await normal.JsonBody(await normal.Get("/api/v1/admin/auth/me"))).GetProperty("mfaSkipped").GetBoolean());
        Assert.Equal(before, MfaSkipKit.TwoFactorState(f, email));
    }

    [Fact]
    public async Task The_password_is_still_required_and_checked_like_the_normal_sign_in()
    {
        var email = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        var (_, wrong) = await MfaSkipKit.Skip(f, email, "Wrong-Passw0rd-Test!x");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var (_, unknown) = await MfaSkipKit.Skip(f, "nobody-" + Guid.NewGuid().ToString("N") + "@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post(MfaSkipKit.Route, new { email }, false)).StatusCode);
        Assert.Equal(0, MfaSkipKit.Sessions(f, email));
    }

    [Fact]
    public async Task A_seller_who_is_not_staff_and_a_deactivated_staff_member_are_refused()
    {
        var seller = $"seller-{Guid.NewGuid():N}@example.com";
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.CreateAsync(new AppUser { UserName = seller, Email = seller, EmailConfirmed = true, HasPassword = true, CreatedAt = DateTime.UtcNow }, ApiFactory.Strong)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await MfaSkipKit.Skip(f, seller)).res.StatusCode);

        var email = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        Assert.Equal(1, MfaSkipKit.Deactivate(f, email));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MfaSkipKit.Skip(f, email)).res.StatusCode);
    }

    [Fact]
    public async Task Deactivating_staff_ends_a_skipped_session()
    {
        var email = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        var (api, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal(1, MfaSkipKit.Deactivate(f, email));
        Assert.Equal("not_staff", await StaffKit.Err(await api.Get("/api/v1/admin/dashboard")));
    }

    [Theory]
    [InlineData("Demo")]                // Demo:Enabled turned off
    [InlineData("Skip")]                // Demo:AllowStaffMfaSkip turned off
    public async Task A_skipped_session_stops_working_the_moment_either_flag_is_turned_off(string flag)
    {
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin, twoFactor: false);
        var (api, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var o = MfaSkipKit.Options(f);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);
        try
        {
            if (flag == "Demo") o.Enabled = false; else o.AllowStaffMfaSkip = false;
            Assert.Equal("two_factor_required", await StaffKit.Err(await api.Get("/api/v1/admin/dashboard")));
            Assert.Equal("two_factor_required", await StaffKit.Err(await api.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1400, reason = StaffKit.Why })));
        }
        finally { o.Enabled = true; o.AllowStaffMfaSkip = true; }
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode);   // back on: the same session works again (nothing was revoked)
    }

    [Fact]
    public async Task A_normal_two_factor_session_is_not_affected_by_the_flags()
    {
        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var api = await StaffKit.Login(f, email);
        var o = MfaSkipKit.Options(f);
        try { o.AllowStaffMfaSkip = false; Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/admin/dashboard")).StatusCode); }
        finally { o.AllowStaffMfaSkip = true; }
    }

    [Fact]
    public async Task The_skip_is_audited_with_its_own_actor_and_the_staff_user_id_and_no_secrets()
    {
        var email = await StaffKit.Create(f, StaffRole.Moderator, twoFactor: false);
        var uid = StaffKit.UserId(f, email);
        var (api, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ev = f.WithDb(db => db.AuditEvents.AsNoTracking().Where(a => a.Type == "admin.demo_mfa_skipped" && a.UserId == uid).ToList());
        var one = Assert.Single(ev);
        Assert.Contains("actor=demo-mfa-skip", one.Detail);
        Assert.Contains($"staffUser={uid}", one.Detail);
        Assert.DoesNotContain(ApiFactory.Strong, one.Detail);
        Assert.DoesNotContain("accessToken", one.Detail);
        // the step-up route (for the step-up prompt of a normal session) is audited the same way
        var email2 = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var normal = await StaffKit.Login(f, email2);
        Assert.Equal(HttpStatusCode.OK, (await normal.Post("/api/v1/admin/auth/demo-step-up", new { })).StatusCode);
        var uid2 = StaffKit.UserId(f, email2);
        Assert.Contains("step_up=true", f.WithDb(db => db.AuditEvents.AsNoTracking().Single(a => a.Type == "admin.demo_mfa_skipped" && a.UserId == uid2).Detail));
        Assert.Equal(HttpStatusCode.OK, (await normal.Put("/api/v1/admin/settings/fee", new { feeBasisPoints = 1000, reason = StaffKit.Why })).StatusCode);
        Assert.True(MfaSkipKit.TwoFactorState(f, email2).TwoFactor);                             // the real account keeps its 2FA
    }

    [Fact]
    public async Task A_skipped_session_can_act_on_a_staff_account_without_a_code_but_a_normal_session_still_needs_one()
    {
        var target = StaffKit.UserId(f, await StaffKit.Create(f, StaffRole.Support));
        var path = $"/api/v1/admin/users/{target}/clear-lockout";

        var email = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var (skipped, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await skipped.Post(path, new { reason = StaffKit.Why })).StatusCode);       // no code, no step-up

        var normal = await StaffKit.Login(f, email);                                                                // same account, a real two-factor session
        Assert.Equal("step_up_required", await StaffKit.Err(await normal.Post(path, new { reason = StaffKit.Why })));
        await StaffKit.StepUp(f, normal, email);
        Assert.Equal(HttpStatusCode.NoContent, (await normal.Post(path, new { reason = StaffKit.Why })).StatusCode);

        // the role rule is untouched: only a Super admin may act on a staff account, skipped or not
        var support = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        var (supportSkipped, r2) = await MfaSkipKit.Skip(f, support);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("staff_target", await StaffKit.Err(await supportSkipped.Post(path, new { reason = StaffKit.Why })));

        // and with the gate closed the skipped session cannot do it either
        var o = MfaSkipKit.Options(f);
        try { o.AllowStaffMfaSkip = false; Assert.Equal("two_factor_required", await StaffKit.Err(await skipped.Post(path, new { reason = StaffKit.Why }))); }
        finally { o.AllowStaffMfaSkip = true; }
    }

    [Fact]
    public async Task The_step_up_route_needs_a_staff_session()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/admin/auth/demo-step-up", new { }, false)).StatusCode);
    }
}

public class DemoStaffMfaLimitsFactory : DemoStaffMfaFactory { public DemoStaffMfaLimitsFactory() { Extra["Demo:PerHour"] = "2"; } }

public class DemoStaffMfaLimitTests(DemoStaffMfaLimitsFactory f) : IClassFixture<DemoStaffMfaLimitsFactory>
{
    [Fact]
    public async Task The_skip_is_rate_limited_like_the_other_demo_actions()
    {
        var email = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        Assert.Equal(HttpStatusCode.OK, (await MfaSkipKit.Skip(f, email)).res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaSkipKit.Skip(f, email)).res.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await MfaSkipKit.Skip(f, email)).res.StatusCode);
    }
}

public class DemoStaffMfaIpTests
{
    [Fact]
    public async Task An_ip_outside_the_allowlist_is_still_refused()
    {
        using var f = new DemoStaffMfaFactory();
        f.Extra["Admin:IpAllowlist:0"] = "203.0.113.0/24";
        var email = await StaffKit.Create(f, StaffRole.Support, twoFactor: false);
        var (_, res) = await MfaSkipKit.Skip(f, email);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("ip_not_allowed", await StaffKit.Err(res));
        Assert.Equal(0, MfaSkipKit.Sessions(f, email));
    }
}
