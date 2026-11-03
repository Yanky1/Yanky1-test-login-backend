using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Tests;

public class AdminStaffTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string TokenFrom(ApiFactory f, string email) =>
        Uri.UnescapeDataString(Regex.Match(f.Mails(email, "staff-invite").Last().Text, @"token=([^\s&]+)").Groups[1].Value);

    [Fact]
    public async Task Invite_flow_hashed_single_use_expiring_token_and_two_factor_enrolment()
    {
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        var email = StaffKit.NewEmail();
        var inv = await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "Support", reason = "New support hire" });
        Assert.Equal(HttpStatusCode.OK, inv.StatusCode);
        var token = TokenFrom(f, email);
        // only the hash is stored
        var stored = f.WithDb(db => db.StaffInvitations.Single(i => i.Email == email));
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(Solvelt.Api.Services.Crypto.Sha256(token), stored.TokenHash);
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(stored));

        var anon = new Api(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email = StaffKit.NewEmail(), token, password = ApiFactory.Strong }, false)).StatusCode); // wrong email
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token = token + "x", password = ApiFactory.Strong }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token, password = "weak" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token, password = ApiFactory.Strong }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token, password = ApiFactory.Strong }, false)).StatusCode); // single use
        Assert.Equal(StaffRole.Support, f.WithDb(db => db.StaffMembers.Single(s => s.UserId == StaffKit.UserId(f, email)).Role));

        // no admin access until 2FA is enrolled
        Assert.Equal("two_factor_setup_required", await StaffKit.Err(await anon.Post("/api/v1/admin/auth/login", new { email, password = ApiFactory.Strong }, false)));
        await anon.Post("/api/v1/admin/auth/2fa/setup", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.OK, (await anon.Post("/api/v1/admin/auth/2fa/enable", new { email, password = ApiFactory.Strong, code = await StaffKit.Totp(f, email) }, false)).StatusCode);
        var api = await StaffKit.Login(f, email);
        Assert.Equal("Support", (await api.JsonBody(await api.Get("/api/v1/admin/auth/me"))).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Invitations_need_super_admin_step_up_and_expire_and_are_replaced_by_newer_ones()
    {
        var email = StaffKit.NewEmail();
        var support = await StaffKit.LoginNew(f, StaffRole.Support);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.Post("/api/v1/admin/staff/invitations", new { email, role = "ReadOnly", reason = StaffKit.Why })).StatusCode);
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail);
        Assert.Equal("step_up_required", await StaffKit.Err(await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "ReadOnly", reason = StaffKit.Why })));
        await StaffKit.StepUp(f, sa, saEmail);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "ReadOnly" })).StatusCode); // reason
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "Owner", reason = StaffKit.Why })).StatusCode);
        await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "ReadOnly", reason = StaffKit.Why });
        var first = TokenFrom(f, email);
        f.Time.Advance(TimeSpan.FromSeconds(5));
        await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "Moderator", reason = StaffKit.Why });
        var second = TokenFrom(f, email);
        var anon = new Api(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token = first, password = ApiFactory.Strong }, false)).StatusCode);
        f.Time.Advance(TimeSpan.FromDays(4));
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token = second, password = ApiFactory.Strong }, false)).StatusCode);
        // an existing staff member cannot be invited again
        var sa2 = await StaffKit.Login(f, saEmail); await StaffKit.StepUp(f, sa2, saEmail);
        Assert.Equal("already_staff", await StaffKit.Err(await sa2.Post("/api/v1/admin/staff/invitations", new { email = saEmail, role = "Support", reason = StaffKit.Why })));
    }

    [Fact]
    public async Task Existing_account_joins_staff_only_with_its_own_password()
    {
        var seller = new Api(f); var email = await seller.SignupVerified();
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        await sa.Post("/api/v1/admin/staff/invitations", new { email, role = "ReadOnly", reason = StaffKit.Why });
        var token = TokenFrom(f, email);
        var anon = new Api(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token, password = "Another-Str0ng-Pass!" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.Post("/api/v1/admin/auth/accept-invite", new { email, token, password = ApiFactory.Strong }, false)).StatusCode);
        Assert.True(f.WithDb(db => db.StaffMembers.Any(s => s.UserId == StaffKit.UserId(f, email))));
    }

    [Fact]
    public async Task Role_changes_need_reason_and_step_up_and_nobody_changes_their_own_role()
    {
        var saEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var sa = await StaffKit.Login(f, saEmail); await StaffKit.StepUp(f, sa, saEmail);
        var target = await StaffKit.Create(f, StaffRole.Support);
        var tid = StaffKit.UserId(f, target);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sa.Put($"/api/v1/admin/staff/{tid}/role", new { role = "Moderator" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sa.Put($"/api/v1/admin/staff/{tid}/role", new { role = "Root", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sa.Put($"/api/v1/admin/staff/{tid}/role", new { role = "Moderator", reason = "Moving to trust & safety" })).StatusCode);
        Assert.Equal(StaffRole.Moderator, f.WithDb(db => db.StaffMembers.Single(s => s.UserId == tid).Role));
        var row = f.WithDb(db => db.AuditEvents.OrderByDescending(e => e.Id).First(e => e.Type == "admin.staff.role_changed"));
        Assert.Contains("Support", row.Detail); Assert.Contains("Moderator", row.Detail); Assert.Equal("Moving to trust & safety", row.Reason);
        var res = await sa.Put($"/api/v1/admin/staff/{StaffKit.UserId(f, saEmail)}/role", new { role = "ReadOnly", reason = StaffKit.Why });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("self_change", await StaffKit.Err(res));
        Assert.Equal(HttpStatusCode.Forbidden, (await sa.Post($"/api/v1/admin/staff/{StaffKit.UserId(f, saEmail)}/deactivate", new { reason = StaffKit.Why })).StatusCode);
        // a promoted/demoted Super admin gains/loses the legacy Admin role that guards the older seller-status API
        // a promoted Super admin does NOT get the legacy seller-kind Admin role; a demoted holder of it loses it
        Assert.Equal(HttpStatusCode.OK, (await sa.Put($"/api/v1/admin/staff/{tid}/role", new { role = "SuperAdmin", reason = StaffKit.Why })).StatusCode);
        Assert.False(await HasLegacyAdmin(tid));
        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await um.AddToRoleAsync((await um.FindByIdAsync(tid.ToString()))!, "Admin");
        }
        Assert.True(await HasLegacyAdmin(tid));
        Assert.Equal(HttpStatusCode.OK, (await sa.Put($"/api/v1/admin/staff/{tid}/role", new { role = "ReadOnly", reason = StaffKit.Why })).StatusCode);
        Assert.False(await HasLegacyAdmin(tid));
    }

    private async Task<bool> HasLegacyAdmin(Guid id)
    {
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await um.IsInRoleAsync((await um.FindByIdAsync(id.ToString()))!, "Admin");
    }

    [Fact]
    public async Task Deactivated_staff_lose_their_session_and_cannot_sign_in_until_reactivated()
    {
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        var email = await StaffKit.Create(f, StaffRole.Moderator);
        var staff = await StaffKit.Login(f, email);
        Assert.Equal(HttpStatusCode.OK, (await staff.Get("/api/v1/admin/dashboard")).StatusCode);
        var id = StaffKit.UserId(f, email);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"/api/v1/admin/staff/{id}/deactivate", new { reason = "Left the company" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Get("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffKit.TryLogin(f, new Api(f), email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sa.Post($"/api/v1/admin/staff/{id}/reactivate", new { reason = "Returned" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StaffKit.TryLogin(f, new Api(f), email)).StatusCode);
    }

    [Fact]
    public async Task Staff_list_shows_members_with_two_factor_state_and_pending_invitations()
    {
        var (sa, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        var noMfa = await StaffKit.Create(f, StaffRole.ReadOnly, twoFactor: false);
        await sa.Post("/api/v1/admin/staff/invitations", new { email = "listed-invitee@example.com", role = "Support", reason = StaffKit.Why });
        var j = await sa.JsonBody(await sa.Get("/api/v1/admin/staff"));
        var m = j.GetProperty("members").EnumerateArray().First(x => x.GetProperty("email").GetString() == noMfa);
        Assert.False(m.GetProperty("twoFactorEnabled").GetBoolean());
        Assert.Contains(j.GetProperty("invitations").EnumerateArray(), x => x.GetProperty("email").GetString() == "listed-invitee@example.com");
        Assert.DoesNotContain("tokenHash", j.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>At least one active Super admin must always remain, also under concurrency.</summary>
public class AdminLastSuperAdminTests
{
    private static StaffContext Ctx(Guid id, string email) => new(id, email, StaffRole.SuperAdmin, Guid.NewGuid(), DateTime.UtcNow);
    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;

    [Fact]
    public async Task A_caller_who_is_no_longer_an_active_super_admin_cannot_change_staff()
    {
        using var f = new ApiFactory();
        var seed = f.WithDb(db => db.StaffMembers.Single(s => s.Role == StaffRole.SuperAdmin).UserId);
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<StaffService>();
        Assert.Equal(403, Status(await svc.ChangeRoleAsync(Ctx(Guid.NewGuid(), "ghost@example.com"), seed, "Support", StaffKit.Why)));
        Assert.Equal(403, Status(await svc.SetActiveAsync(Ctx(Guid.NewGuid(), "ghost@example.com"), seed, false, StaffKit.Why)));
        Assert.Equal(StaffRole.SuperAdmin, f.WithDb(db => db.StaffMembers.Single(s => s.UserId == seed).Role));
    }

    [Fact]
    public async Task With_a_second_super_admin_the_first_can_be_demoted_through_the_api()
    {
        using var f = new ApiFactory();
        var seed = f.WithDb(db => db.StaffMembers.Single(s => s.Role == StaffRole.SuperAdmin).UserId);
        var (sa2, _) = await StaffKit.LoginSteppedUp(f, StaffRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await sa2.Put($"/api/v1/admin/staff/{seed}/role", new { role = "ReadOnly", reason = StaffKit.Why })).StatusCode);
        Assert.Equal(1, f.WithDb(db => db.StaffMembers.Count(s => s.Role == StaffRole.SuperAdmin && s.Active)));
    }

    [Theory]
    [InlineData(true)]   // demote each other
    [InlineData(false)]  // deactivate each other
    public async Task Two_super_admins_removing_each_other_in_parallel_never_leave_zero(bool demote)
    {
        using var f = new ApiFactory();
        var seed = f.WithDb(db => db.StaffMembers.Single(s => s.Role == StaffRole.SuperAdmin).UserId);
        var aEmail = await StaffKit.Create(f, StaffRole.SuperAdmin); var bEmail = await StaffKit.Create(f, StaffRole.SuperAdmin);
        var a = StaffKit.UserId(f, aEmail); var b = StaffKit.UserId(f, bEmail);
        f.WithDb(db => { db.StaffMembers.Single(s => s.UserId == seed).Role = StaffRole.ReadOnly; db.SaveChanges(); return 0; });
        for (var round = 0; round < 12; round++)
        {
            f.WithDb(db => { foreach (var m in db.StaffMembers.Where(s => s.UserId == a || s.UserId == b)) { m.Role = StaffRole.SuperAdmin; m.Active = true; } db.SaveChanges(); return 0; });
            async Task Go(Guid actor, string email, Guid target)
            {
                using var scope = f.Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<StaffService>();
                if (demote) await svc.ChangeRoleAsync(Ctx(actor, email), target, "ReadOnly", StaffKit.Why);
                else await svc.SetActiveAsync(Ctx(actor, email), target, false, StaffKit.Why);
            }
            await Task.WhenAll(Task.Run(() => Go(a, aEmail, b)), Task.Run(() => Go(b, bEmail, a)));
            Assert.True(f.WithDb(db => db.StaffMembers.Count(s => s.Role == StaffRole.SuperAdmin && s.Active)) >= 1, $"round {round}: no active Super admin left");
        }
    }
}
