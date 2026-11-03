using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the security review of the Teams backend (fix/teams-review).</summary>
public class TeamsReviewTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private async Task<(Api a, string aEmail, Api b, string bEmail, Guid seller)> TwoOwners()
    {
        var (a, aEmail, seller) = await TeamHelpers.NewOwner(f);
        var (b, bEmail) = await TeamHelpers.JoinAs(f, a, "Admin");
        var mid = (await a.JsonBody(await a.Get("/api/v1/team/members"))).EnumerateArray().Single(m => m.GetProperty("email").GetString() == bEmail).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await a.Patch($"/api/v1/team/members/{mid}", new { role = "Owner" }).Code());
        return (a, aEmail, b, bEmail, seller);
    }
    private async Task<Guid> MemberId(Api api, string email) =>
        (await api.JsonBody(await api.Get("/api/v1/team/members"))).EnumerateArray().Single(m => m.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();

    // ---- 2: a removed Owner is not restored by the startup backfill; the primary owner is repointed; status mails go to current Owners
    [Fact]
    public async Task Removed_primary_owner_is_not_restored_by_backfill_and_status_mails_go_to_current_owners()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        var aId = f.WithDb(db => db.Users.Single(u => u.Email == aEmail).Id);
        var bId = f.WithDb(db => db.Users.Single(u => u.Email == bEmail).Id);
        Assert.Equal(HttpStatusCode.NoContent, await b.Delete($"/api/v1/team/members/{await MemberId(b, aEmail)}").Code());
        Assert.Equal(bId, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).UserId)); // repointed to the remaining Owner
        using (var scope = f.Services.CreateScope())
            await TeamService.BackfillAsync(scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>(), f.Time);
        Assert.False(f.WithDb(db => db.Memberships.Any(m => m.SellerId == seller && m.UserId == aId)));
        Assert.Equal(HttpStatusCode.Forbidden, await a.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code());
        // status mail goes to the current Owner only
        await b.CompleteApplicationAndSubmit();
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Approved" }).Code());
        Assert.Single(f.Mails(bEmail, "approved"));
        Assert.Empty(f.Mails(aEmail, "approved"));
    }

    [Fact]
    public async Task Leaving_primary_owner_is_not_restored_either()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        var aId = f.WithDb(db => db.Users.Single(u => u.Email == aEmail).Id);
        Assert.Equal(HttpStatusCode.NoContent, await a.Post("/api/v1/team/leave").Code());
        using (var scope = f.Services.CreateScope())
            await TeamService.BackfillAsync(scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>(), f.Time);
        Assert.False(f.WithDb(db => db.Memberships.Any(m => m.SellerId == seller && m.UserId == aId)));
        Assert.NotEqual(aId, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).UserId));
        Assert.Equal(HttpStatusCode.OK, await b.Get("/api/v1/team/members").Code());
    }

    // ---- follow-ups ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Backfill_never_readds_a_legacy_UserId_without_membership_when_other_members_exist()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        var aId = f.WithDb(db => db.Users.Single(u => u.Email == aEmail).Id);
        Assert.Equal(HttpStatusCode.NoContent, await b.Delete($"/api/v1/team/members/{await MemberId(b, aEmail)}").Code());
        // simulate a stale pointer to the removed user (legacy rows / older code paths)
        f.WithDb(db => { db.Sellers.Single(s => s.Id == seller).UserId = aId; db.SaveChanges(); return 0; });
        using (var scope = f.Services.CreateScope())
            await TeamService.BackfillAsync(scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>(), f.Time);
        Assert.False(f.WithDb(db => db.Memberships.Any(m => m.SellerId == seller && m.UserId == aId)));
        Assert.Equal(HttpStatusCode.Forbidden, await a.Get("/api/v1/brands").Code());
    }

    [Fact]
    public async Task Status_mails_go_to_active_owners_even_if_UserId_points_at_a_removed_user()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        var aId = f.WithDb(db => db.Users.Single(u => u.Email == aEmail).Id);
        Assert.Equal(HttpStatusCode.NoContent, await b.Delete($"/api/v1/team/members/{await MemberId(b, aEmail)}").Code());
        f.WithDb(db => { db.Sellers.Single(s => s.Id == seller).UserId = aId; db.SaveChanges(); return 0; });
        await b.CompleteApplicationAndSubmit();
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Approved" }).Code());
        Assert.Single(f.Mails(bEmail, "approved"));
        Assert.Empty(f.Mails(aEmail, "approved"));
        Assert.Empty(f.Mails(aEmail, "application-received"));
    }

    [Fact]
    public async Task An_actor_demoted_a_moment_ago_cannot_finish_an_action_at_the_old_role()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, owner, "Admin");
        var (viewer, viewerEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        var adminMid = await MemberId(owner, adminEmail); var viewerMid = await MemberId(owner, viewerEmail);
        var adminUser = f.WithDb(db => db.Users.Single(u => u.Email == adminEmail).Id);
        // the request started as Admin (stale AccessAccess) ...
        var stale = new AccountAccess(adminUser, seller, adminMid, AccountRole.Admin, true, new HashSet<Guid>(), SellerStatus.Draft);
        // ... but the Owner demoted them before the action got the account lock
        f.WithDb(db => { db.Memberships.Single(m => m.Id == adminMid).Role = AccountRole.Viewer; db.SaveChanges(); return 0; });
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TeamService>();
        var r = await svc.RemoveMemberAsync(stale, viewerMid);
        Assert.False(r.Ok); Assert.Equal(403, r.Status);
        Assert.True(f.WithDb(db => db.Memberships.Any(m => m.Id == viewerMid)));
        // and a removed actor is refused too
        f.WithDb(db => { db.Memberships.Remove(db.Memberships.Single(m => m.Id == adminMid)); db.SaveChanges(); return 0; });
        var r2 = await svc.ChangeMemberAsync(stale, viewerMid, AccountRole.Manager, null, null, null);
        Assert.False(r2.Ok); Assert.Equal(403, r2.Status);
        Assert.Equal(AccountRole.Viewer, f.WithDb(db => db.Memberships.Single(m => m.Id == viewerMid).Role));
    }

    [Fact]
    public async Task Closure_email_skips_suspended_owners()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        Assert.Equal(HttpStatusCode.OK, await a.Patch($"/api/v1/team/members/{await MemberId(a, bEmail)}", new { status = "Suspended" }).Code());
        Assert.Equal(HttpStatusCode.OK, await a.Post("/api/v1/team/close-account", new { currentPassword = ApiFactory.Strong }).Code());
        Assert.Single(f.Mails(aEmail, "team-account-closed"));
        Assert.Empty(f.Mails(bEmail, "team-account-closed"));
    }

    [Fact]
    public async Task Demoting_the_primary_owner_repoints_UserId_to_an_active_owner()
    {
        var (a, aEmail, b, bEmail, seller) = await TwoOwners();
        var bId = f.WithDb(db => db.Users.Single(u => u.Email == bEmail).Id);
        Assert.NotEqual(bId, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).UserId)); // A is the primary owner
        Assert.Equal(HttpStatusCode.OK, await b.Patch($"/api/v1/team/members/{await MemberId(b, aEmail)}", new { role = "Admin" }).Code());
        Assert.Equal(bId, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).UserId));
    }

    // ---- 3: concurrent mutual demotion / removal never leaves 0 Owners
    [Fact]
    public async Task Parallel_mutual_demotions_and_removals_never_leave_an_account_without_an_owner()
    {
        for (var i = 0; i < 6; i++)
        {
            var (a, aEmail, b, bEmail, seller) = await TwoOwners();
            var aMid = await MemberId(a, aEmail); var bMid = await MemberId(a, bEmail);
            var demote = i % 2 == 0;
            var t1 = demote ? a.Patch($"/api/v1/team/members/{bMid}", new { role = "Viewer" }) : a.Delete($"/api/v1/team/members/{bMid}");
            var t2 = demote ? b.Patch($"/api/v1/team/members/{aMid}", new { role = "Viewer" }) : b.Delete($"/api/v1/team/members/{aMid}");
            var res = await Task.WhenAll(t1, t2);
            Assert.DoesNotContain(res, r => (int)r.StatusCode >= 500);
            Assert.True(f.WithDb(db => db.Memberships.Count(m => m.SellerId == seller && m.Role == AccountRole.Owner && m.Status == MembershipStatus.Active)) >= 1, $"iteration {i}");
        }
    }

    // ---- 4: accepting an invite never overwrites an existing membership
    [Fact]
    public async Task Accepting_an_invite_with_an_existing_membership_is_refused_and_changes_nothing()
    {
        var (owner, ownerEmail, seller) = await TeamHelpers.NewOwner(f);
        var alt = Api.NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/team/invitations", new { email = alt, role = "Viewer" }).Code());
        var token = TeamHelpers.TokenFor(f, alt);
        // the sole Owner changes their own email to the invited address, then accepts
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/account/email/change", new { newEmail = alt, currentPassword = ApiFactory.Strong }).Code());
        var confirm = await owner.Post("/api/v1/account/email/confirm", new { code = f.LastCode(alt, "change-email-code") });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        owner.SetToken((await owner.JsonBody(confirm)).GetProperty("accessToken").GetString()!);
        var r = await owner.Post($"/api/v1/invitations/{token}/accept");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("already_member", await r.ErrCode());
        Assert.Equal(AccountRole.Owner, f.WithDb(db => db.Memberships.Single(m => m.SellerId == seller).Role));
        Assert.NotEqual(InviteStatus.Pending, f.WithDb(db => db.Invitations.Single(i => i.Email == alt).Status));
    }

    [Fact]
    public async Task A_suspended_member_cannot_reactivate_themselves_through_an_invite()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var user = new Api(f); var email = await user.SignupVerified();
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/team/invitations", new { email, role = "Admin" }).Code());
        var token = TeamHelpers.TokenFor(f, email);
        var uid = f.WithDb(db => db.Users.Single(u => u.Email == email).Id);
        f.WithDb(db => { db.Memberships.Add(new AccountMembership { SellerId = seller, UserId = uid, Role = AccountRole.Viewer, Status = MembershipStatus.Suspended, CreatedAt = f.Time.GetUtcNow().UtcDateTime, UpdatedAt = f.Time.GetUtcNow().UtcDateTime }); db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.Conflict, await user.Post($"/api/v1/invitations/{token}/accept").Code());
        var m = f.WithDb(db => db.Memberships.Single(x => x.SellerId == seller && x.UserId == uid));
        Assert.Equal(MembershipStatus.Suspended, m.Status); Assert.Equal(AccountRole.Viewer, m.Role);
    }

    // ---- 5a: simultaneous brand creates with the same name
    [Fact]
    public async Task Simultaneous_brand_creates_with_the_same_name_never_500()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        for (var i = 0; i < 5; i++)
        {
            var name = "Race " + i;
            var res = await Task.WhenAll(owner.Post("/api/v1/brands", new { name }), owner.Post("/api/v1/brands", new { name }));
            Assert.DoesNotContain(res, r => (int)r.StatusCode >= 500);
            Assert.Equal(1, res.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, f.WithDb(db => db.Brands.Count(b => b.SellerId == seller && b.Name == name)));
        }
    }

    // ---- 5b: Owner-only close account
    [Fact]
    public async Task Closing_an_account_is_owner_only_needs_step_up_and_removes_access_for_everyone()
    {
        var (owner, ownerEmail, seller) = await TeamHelpers.NewOwner(f);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, owner, "Admin");
        var (viewer, _) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Post("/api/v1/team/close-account", new { currentPassword = ApiFactory.Strong }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Post("/api/v1/team/close-account", new { currentPassword = ApiFactory.Strong }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/team/close-account", new { currentPassword = "wrong" }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/team/close-account", new { }).Code());
        Assert.Equal(SellerStatus.Draft, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).Status));
        var ok = await owner.Post("/api/v1/team/close-account", new { currentPassword = ApiFactory.Strong });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(SellerStatus.Closed, f.WithDb(db => db.Sellers.Single(s => s.Id == seller).Status));
        // everybody's very next request is refused; the data is retained
        foreach (var api in new[] { owner, admin, viewer })
        {
            var r = await api.Get("/api/v1/brands");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("no_active_account", await r.ErrCode());
        }
        Assert.True(f.WithDb(db => db.Brands.Any(b => b.SellerId == seller)));
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(a => a.Type).ToList()), t => t == "account.closed");
        Assert.Single(f.Mails(ownerEmail, "team-account-closed"));
        // they can still sign in (their login is not affected) but the account is gone from their list and can't be opened
        var back = new Api(f);
        var l = await back.LoginAs(adminEmail, ApiFactory.Strong);
        Assert.Empty(l.Accounts!); Assert.Null(l.ActiveAccountId);
        Assert.Equal(HttpStatusCode.NotFound, await back.Post("/api/v1/session/account", new { sellerId = seller }).Code());
        // the platform admin API cannot resurrect it
        var plat = new Api(f); await plat.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Conflict, await plat.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Approved" }).Code());
        Assert.Equal(HttpStatusCode.Conflict, await plat.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Closed" }).Code());
    }
}

/// <summary>Item 1: invite-join must not adopt an unverified squatter that planted a phone number and PIN through the phone sign-up.</summary>
public class TeamsReviewPhoneTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    [Fact]
    public async Task Invite_join_replaces_a_phone_squatter_instead_of_adopting_it()
    {
        var attacker = PhoneApiFactory.NewNumber(); var victim = Api.NewEmail();
        var p = new Phones(f);
        // attacker: phone sign-up with the victim's email, stopping before the mailbox is proven (unverified user, Draft seller)
        await p.Sms(attacker, "JOIN"); await p.Sms(attacker, f.LastSmsCode(attacker));
        await p.Sms(attacker, "739204"); await p.Sms(attacker, "739204");
        await p.Sms(attacker, victim); await p.Sms(attacker, "YES"); await p.Sms(attacker, "YES");
        var squatterId = f.WithDb(db => db.Users.Single(u => u.Email == victim).Id);
        Assert.False(f.WithDb(db => db.Users.Single(u => u.Id == squatterId).EmailConfirmed));
        // an Owner invites the victim as Admin; the victim joins with a password
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/team/invitations", new { email = victim, role = "Admin" }).Code());
        var joined = await new Api(f).Post($"/api/v1/invitations/{TeamHelpers.TokenFor(f, victim)}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        // the squatter is gone; the victim is a brand-new user with nothing planted
        var uid = f.WithDb(db => db.Users.Single(u => u.Email == victim).Id);
        Assert.NotEqual(squatterId, uid);
        var u = f.WithDb(db => db.Users.Single(x => x.Id == uid));
        Assert.True(u.EmailConfirmed); Assert.Null(u.PhoneNumber); Assert.False(u.PhoneNumberConfirmed);
        Assert.False(f.WithDb(db => db.PhoneCredentials.Any(c => c.UserId == uid)));
        Assert.False(f.WithDb(db => db.PhoneCredentials.Any(c => c.UserId == squatterId)));
        Assert.False(f.WithDb(db => db.Sellers.Any(s => s.UserId == squatterId || s.UserId == uid)));
        var memberships = f.WithDb(db => db.Memberships.Where(m => m.UserId == uid).ToList());
        Assert.Equal(AccountRole.Admin, Assert.Single(memberships).Role);
        Assert.False(f.WithDb(db => db.Memberships.Any(m => m.UserId == squatterId)));
        // the attacker's phone + PIN no longer sign in, and RESET sends nothing
        f.Time.Advance(TimeSpan.FromSeconds(61));
        var before = f.Sms.To(attacker).Count;
        await p.Sms(attacker, "LOGIN");
        Assert.DoesNotContain("Signed in", await p.Sms(attacker, "000000 739204"));
        await p.Sms(attacker, "RESET");
        Assert.Equal(before, f.Sms.To(attacker).Count);
    }
}
