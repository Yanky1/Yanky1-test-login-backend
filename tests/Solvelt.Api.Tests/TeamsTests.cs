using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Tests;

public static class TeamHelpers
{
    public static async Task<(Api api, string email, Guid seller)> NewOwner(ApiFactory f, bool withBusiness = true)
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        if (withBusiness) Assert.True((await api.Put("/api/v1/seller/application/business", Api.ValidBusiness)).IsSuccessStatusCode);
        return (api, email, api.Session!.SellerId);
    }

    public static string TokenFor(ApiFactory f, string email) =>
        Regex.Matches(f.Mails(email, "team-invitation").Last().Text, @"/invite/([A-Za-z0-9_-]+)").Last().Groups[1].Value;

    public static async Task<Guid> NewBrand(Api owner, string name)
    {
        var r = await owner.Post("/api/v1/brands", new { name });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await owner.JsonBody(r)).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> BrandId(Api api, int index = 0) =>
        (await api.JsonBody(await api.Get("/api/v1/brands"))).EnumerateArray().ElementAt(index).GetProperty("id").GetGuid();

    /// <summary>Owner invites a brand-new email; the invitee joins through the link (no wizard). Returns the signed-in invitee.</summary>
    public static async Task<(Api api, string email)> JoinAs(ApiFactory f, Api owner, string role, Guid[]? brands = null)
    {
        var email = Api.NewEmail();
        var inv = await owner.Post("/api/v1/team/invitations", new { email, role, brandIds = brands });
        Assert.Equal(HttpStatusCode.Accepted, inv.StatusCode);
        var api = new Api(f);
        var res = await api.Post($"/api/v1/invitations/{TokenFor(f, email)}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        api.SetSession((await res.Content.ReadFromJsonAsync<Login>(Api.Json))!);
        return (api, email);
    }

    public static async Task Switch(this Api api, Guid sellerId)
    {
        var r = await api.Post("/api/v1/session/account", new { sellerId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        api.SetToken((await api.JsonBody(r)).GetProperty("accessToken").GetString()!);
    }

    public static async Task<HttpStatusCode> Code(this Task<HttpResponseMessage> t) => (await t).StatusCode;
    public static async Task<string?> ErrCode(this HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;
}

public class TeamsTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Backfill_gives_existing_sellers_an_owner_membership_and_a_default_brand()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var user = f.WithDb(db => db.Sellers.Single(s => s.Id == seller).UserId);
        // simulate a pre-teams database: no membership, no brand
        f.WithDb(db => { db.Memberships.RemoveRange(db.Memberships.Where(m => m.SellerId == seller)); db.Brands.RemoveRange(db.Brands.Where(b => b.SellerId == seller)); db.SaveChanges(); return 0; });
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Solvelt.Api.Data.AppDbContext>();
            await TeamService.BackfillAsync(db, f.Time);
            await TeamService.BackfillAsync(db, f.Time); // idempotent
        }
        var m = f.WithDb(db => db.Memberships.Where(x => x.SellerId == seller).ToList());
        Assert.Single(m);
        Assert.Equal(user, m[0].UserId); Assert.Equal(AccountRole.Owner, m[0].Role); Assert.True(m[0].AllBrands);
        var brands = f.WithDb(db => db.Brands.Where(b => b.SellerId == seller).ToList());
        Assert.Single(brands); Assert.Equal("Acme Co", brands[0].Name); Assert.True(brands[0].IsDefault);
        var again = new Api(f);
        // existing wizard/status keeps working for the (re-signed-in) owner
        var login = await owner.Get("/api/v1/seller/application");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _ = again;
    }

    [Fact]
    public async Task New_account_has_main_brand_that_follows_the_vendor_name()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f, withBusiness: false);
        var b = await owner.JsonBody(await owner.Get("/api/v1/brands"));
        Assert.Equal("Main brand", b[0].GetProperty("name").GetString());
        await owner.Put("/api/v1/seller/application/business", Api.ValidBusiness);
        b = await owner.JsonBody(await owner.Get("/api/v1/brands"));
        Assert.Equal("Acme Co", b[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Invite_new_user_join_then_sees_only_scoped_brand_and_never_the_wizard()
    {
        var (owner, ownerEmail, seller) = await TeamHelpers.NewOwner(f);
        var a = await TeamHelpers.BrandId(owner);
        var bB = await TeamHelpers.NewBrand(owner, "Second brand");
        var (mgr, email) = await TeamHelpers.JoinAs(f, owner, "Manager", [bB]);
        Assert.Equal(seller, mgr.Session!.ActiveAccountId);
        Assert.Equal(seller, mgr.Session.SellerId);
        Assert.Equal("Manager", Assert.Single(mgr.Session.Accounts!).Role);
        var brands = await mgr.JsonBody(await mgr.Get("/api/v1/brands"));
        Assert.Equal(bB, Assert.Single(brands.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, await mgr.Get($"/api/v1/brands/{a}").Code());
        Assert.Equal(HttpStatusCode.OK, await mgr.Get($"/api/v1/brands/{bB}").Code());
        // no wizard for members: the application is restricted and not editable
        var app = await mgr.JsonBody(await mgr.Get("/api/v1/seller/application"));
        Assert.True(app.GetProperty("restricted").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, await mgr.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code());
        // inviter is told, invitee never got a verification mail
        Assert.Single(f.Mails(ownerEmail, "team-invite-accepted"));
        Assert.Empty(f.Mails(email, "verify-code"));
        Assert.Contains(f.WithDb(db => db.AuditEvents.Select(x => x.Type).ToList()), t => t == "invite.accepted");
        // the invited user got no seller account of their own
        var uid = f.WithDb(db => db.Users.Single(u => u.Email == email).Id);
        Assert.Equal(0, f.WithDb(db => db.Sellers.Count(s => s.UserId == uid)));
        // can sign in normally afterwards
        var again = new Api(f);
        var l = await again.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(seller, l.ActiveAccountId);
    }

    [Fact]
    public async Task Existing_user_accepts_after_signing_in_and_wrong_email_is_refused()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var other = new Api(f);
        var otherEmail = await other.SignupVerified();
        var wrong = new Api(f);
        await wrong.SignupVerified();
        var inv = await owner.Post("/api/v1/team/invitations", new { email = otherEmail.ToUpperInvariant(), role = "Viewer" });
        Assert.Equal(HttpStatusCode.Accepted, inv.StatusCode);
        var token = TeamHelpers.TokenFor(f, otherEmail);
        // preview is anonymous
        var pv = await new Api(f).JsonBody(await new Api(f).Get($"/api/v1/invitations/{token}", false));
        Assert.Equal("pending", pv.GetProperty("state").GetString());
        Assert.Equal(otherEmail, pv.GetProperty("email").GetString());
        // wrong signed-in user
        var r = await wrong.Post($"/api/v1/invitations/{token}/accept");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("email_mismatch", await r.ErrCode());
        Assert.Equal(HttpStatusCode.Unauthorized, await new Api(f).Post($"/api/v1/invitations/{token}/accept", null, false).Code());
        // right user
        Assert.Equal(HttpStatusCode.OK, await other.Post($"/api/v1/invitations/{token}/accept").Code());
        var me = await other.LoginAs(otherEmail, ApiFactory.Strong);
        Assert.Equal(2, me.Accounts!.Count);
        Assert.Contains(me.Accounts, a => a.SellerId == seller && a.Role == "Viewer");
        // single use
        var again = await other.Post($"/api/v1/invitations/{token}/accept");
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Equal("invite_accepted", await again.ErrCode());
    }

    [Fact]
    public async Task Expired_revoked_and_unknown_tokens()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var e2 = Api.NewEmail();
        var inv = await owner.JsonBody(await owner.Post("/api/v1/team/invitations", new { email = e2, role = "Viewer" }));
        var t2 = TeamHelpers.TokenFor(f, e2);
        Assert.Equal(HttpStatusCode.NoContent, await owner.Delete($"/api/v1/team/invitations/{inv.GetProperty("id").GetGuid()}").Code());
        var r2 = await new Api(f).Post($"/api/v1/invitations/{t2}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal("invite_revoked", await r2.ErrCode());

        var e1 = Api.NewEmail();
        await owner.Post("/api/v1/team/invitations", new { email = e1, role = "Viewer" });
        var t1 = TeamHelpers.TokenFor(f, e1);
        f.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var r = await new Api(f).Post($"/api/v1/invitations/{t1}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
        Assert.Equal("invite_expired", await r.ErrCode());
        var pv = await new Api(f).JsonBody(await new Api(f).Get($"/api/v1/invitations/{t1}", false));
        Assert.Equal("expired", pv.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await new Api(f).Get("/api/v1/invitations/not-a-real-token", false).Code());
    }

    [Fact]
    public async Task Inviting_twice_refreshes_the_single_pending_invite_and_rotates_the_link()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var email = Api.NewEmail();
        await owner.Post("/api/v1/team/invitations", new { email, role = "Viewer" });
        var t1 = TeamHelpers.TokenFor(f, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, await owner.Post("/api/v1/team/invitations", new { email, role = "Viewer" }).Code());
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/team/invitations", new { email = email.ToUpperInvariant(), role = "Manager" }).Code());
        var list = await owner.JsonBody(await owner.Get("/api/v1/team/invitations"));
        var mine = list.EnumerateArray().Where(i => i.GetProperty("email").GetString() == email).ToList();
        Assert.Single(mine);
        Assert.Equal("Manager", mine[0].GetProperty("role").GetString());
        Assert.Equal(2, mine[0].GetProperty("sendCount").GetInt32());
        var t2 = TeamHelpers.TokenFor(f, email);
        Assert.NotEqual(t1, t2);
        Assert.Equal(HttpStatusCode.NotFound, await new Api(f).Get($"/api/v1/invitations/{t1}", false).Code()); // old link is dead
        // explicit resend is rate limited too
        var id = mine[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.TooManyRequests, await owner.Post($"/api/v1/team/invitations/{id}/resend").Code());
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Accepted, await owner.Post($"/api/v1/team/invitations/{id}/resend").Code());
        Assert.Equal(3, f.Mails(email, "team-invitation").Count);
    }

    [Fact]
    public async Task Invite_responses_do_not_reveal_whether_the_email_has_a_login()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var (_, known, _) = await TeamHelpers.NewOwner(f);
        var unknown = Api.NewEmail();
        var r1 = await owner.Post("/api/v1/team/invitations", new { email = known, role = "Viewer" });
        var r2 = await owner.Post("/api/v1/team/invitations", new { email = unknown, role = "Viewer" });
        Assert.Equal(r1.StatusCode, r2.StatusCode);
        var j1 = await owner.JsonBody(r1); var j2 = await owner.JsonBody(r2);
        Assert.Equal(j1.EnumerateObject().Select(p => p.Name).OrderBy(x => x), j2.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal(j1.GetProperty("message").GetString(), j2.GetProperty("message").GetString());
        Assert.Single(f.Mails(known, "team-invitation")); Assert.Single(f.Mails(unknown, "team-invitation"));
    }

    [Fact]
    public async Task One_email_in_two_accounts_with_different_roles_and_account_switching()
    {
        var (ownerA, _, sellerA) = await TeamHelpers.NewOwner(f);
        var (ownerB, _, sellerB) = await TeamHelpers.NewOwner(f);
        var user = new Api(f);
        var email = await user.SignupVerified();
        Assert.Equal(HttpStatusCode.Accepted, await ownerA.Post("/api/v1/team/invitations", new { email, role = "Admin" }).Code());
        Assert.Equal(HttpStatusCode.Accepted, await ownerB.Post("/api/v1/team/invitations", new { email, role = "Viewer" }).Code());
        // signed in user: pending invites listed, accepted by id
        var pending = await user.JsonBody(await user.Get("/api/v1/account/invitations"));
        Assert.Equal(2, pending.GetArrayLength());
        foreach (var p in pending.EnumerateArray())
            Assert.Equal(HttpStatusCode.OK, await user.Post($"/api/v1/account/invitations/{p.GetProperty("id").GetGuid()}/accept").Code());
        Assert.Equal(HttpStatusCode.OK, await user.Get("/api/v1/account/invitations").Code());
        // sign in: three accounts (own draft, A, B); several -> no last used -> must choose
        var fresh = new Api(f);
        var l = await fresh.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(3, l.Accounts!.Count);
        Assert.Null(l.ActiveAccountId); Assert.Equal(Guid.Empty, l.SellerId); Assert.Equal("", l.VendorName);
        Assert.Equal(HttpStatusCode.Forbidden, await fresh.Get("/api/v1/team/members").Code()); // no active account
        await fresh.Switch(sellerA);
        Assert.Equal(HttpStatusCode.OK, await fresh.Get("/api/v1/team/members").Code());  // Admin in A
        await fresh.Switch(sellerB);
        Assert.Equal(HttpStatusCode.Forbidden, await fresh.Get("/api/v1/team/members").Code()); // Viewer in B
        Assert.Equal(HttpStatusCode.OK, await fresh.Get("/api/v1/brands").Code());
        // last used is remembered on the next sign-in
        var next = new Api(f);
        var l2 = await next.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(sellerB, l2.ActiveAccountId); Assert.Equal("Acme Co", l2.VendorName);
        Assert.Equal(sellerB, l2.Accounts![0].SellerId); // last used first
        // refresh keeps the active account
        var rr = await next.Refresh(csrf: true);
        Assert.Equal(HttpStatusCode.OK, rr.StatusCode);
        Assert.Equal(sellerB, next.Session!.ActiveAccountId);
        // not a member of this one: indistinguishable from unknown
        var (_, _, sellerC) = await TeamHelpers.NewOwner(f);
        Assert.Equal(HttpStatusCode.NotFound, await next.Post("/api/v1/session/account", new { sellerId = sellerC }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await next.Post("/api/v1/session/account", new { sellerId = Guid.NewGuid() }).Code());
        var me = await next.JsonBody(await next.Get("/api/v1/account/me"));
        Assert.Equal(sellerB, me.GetProperty("activeAccountId").GetGuid());
        Assert.Equal(3, me.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task User_can_create_a_second_seller_account_while_member_elsewhere()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var (mgr, _) = await TeamHelpers.JoinAs(f, owner, "Manager");
        Assert.Equal(HttpStatusCode.BadRequest, await mgr.Post("/api/v1/account/seller-accounts", new { acceptTerms = false }).Code());
        var r = await mgr.Post("/api/v1/account/seller-accounts", new { acceptTerms = true });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await mgr.JsonBody(r)).GetProperty("sellerId").GetGuid();
        await mgr.Switch(id);
        var app = await mgr.JsonBody(await mgr.Get("/api/v1/seller/application"));
        Assert.Equal("Draft", app.GetProperty("status").GetString());
        Assert.False(app.TryGetProperty("restricted", out _));
        Assert.Equal(HttpStatusCode.OK, await mgr.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code());
        // and still just a Manager of the first one
        await mgr.Switch(seller);
        Assert.Equal(HttpStatusCode.Forbidden, await mgr.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code());
    }

    [Fact]
    public async Task Removal_and_demotion_take_effect_on_the_next_request_with_the_old_token()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, owner, "Admin");
        var (viewer, viewerEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        Assert.Equal(HttpStatusCode.OK, await admin.Get("/api/v1/team/members").Code());
        var members = await owner.JsonBody(await owner.Get("/api/v1/team/members"));
        Guid Member(string email) => members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
        // demote Admin -> Viewer: the SAME access token loses admin rights immediately
        var d = await owner.Patch($"/api/v1/team/members/{Member(adminEmail)}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Get("/api/v1/team/members").Code());
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Post("/api/v1/brands", new { name = "Nope" }).Code());
        Assert.Single(f.Mails(adminEmail, "team-role-changed"));
        // remove Viewer: old token is denied (no active account) on the very next request, sign-in still works
        Assert.Equal(HttpStatusCode.NoContent, await owner.Delete($"/api/v1/team/members/{Member(viewerEmail)}").Code());
        var denied = await viewer.Get("/api/v1/brands");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no_active_account", await denied.ErrCode());
        Assert.Equal(HttpStatusCode.Forbidden, await viewer.Get("/api/v1/seller/application").Code());
        Assert.Single(f.Mails(viewerEmail, "team-removed"));
        // zero accounts: still signs in, with an empty account list; can see/accept invites and create an account
        var back = new Api(f);
        var l = await back.LoginAs(viewerEmail, ApiFactory.Strong);
        Assert.Empty(l.Accounts!); Assert.Null(l.ActiveAccountId); Assert.Equal(Guid.Empty, l.SellerId);
        Assert.Equal(HttpStatusCode.OK, await back.Get("/api/v1/account/invitations").Code());
        Assert.Equal(HttpStatusCode.Created, await back.Post("/api/v1/account/seller-accounts", new { acceptTerms = true }).Code());
        // admin cannot sign someone else out; removal never revoked the removed user's session
        Assert.Equal(HttpStatusCode.OK, await viewer.Get("/api/v1/account/me").Code());
    }

    [Fact]
    public async Task Permission_matrix_each_role_against_each_endpoint()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var actors = new Dictionary<string, Api> { ["Owner"] = owner };
        foreach (var role in new[] { "Admin", "Manager", "Viewer" }) actors[role] = (await TeamHelpers.JoinAs(f, owner, role)).api;
        var brandId = await TeamHelpers.BrandId(owner);
        // endpoint -> roles allowed (anything else must be 403; allowed roles must NOT be 403)
        var matrix = new (string Name, Func<Api, Task<HttpResponseMessage>> Call, string[] Allowed)[]
        {
            ("GET members", a => a.Get("/api/v1/team/members"), ["Owner", "Admin"]),
            ("GET invitations", a => a.Get("/api/v1/team/invitations"), ["Owner", "Admin"]),
            ("POST invitation", a => a.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer" }), ["Owner", "Admin"]),
            ("PATCH member", a => a.Patch($"/api/v1/team/members/{Guid.NewGuid()}", new { role = "Viewer" }), ["Owner", "Admin"]),
            ("DELETE member", a => a.Delete($"/api/v1/team/members/{Guid.NewGuid()}"), ["Owner", "Admin"]),
            ("transfer ownership", a => a.Post("/api/v1/team/transfer-ownership", new { memberId = Guid.NewGuid() }), ["Owner"]),
            ("GET brands", a => a.Get("/api/v1/brands"), ["Owner", "Admin", "Manager", "Viewer"]),
            ("POST brand", a => a.Post("/api/v1/brands", new { name = "B " + Guid.NewGuid().ToString("N")[..6] }), ["Owner", "Admin"]),
            ("PUT brand", a => a.Put($"/api/v1/brands/{brandId}", new { name = "Acme Co" }), ["Owner", "Admin"]),
            ("archive brand", a => a.Post($"/api/v1/brands/{Guid.NewGuid()}/archive"), ["Owner", "Admin"]),
            ("DELETE logo", a => a.Delete($"/api/v1/brands/{Guid.NewGuid()}/logo"), ["Owner", "Admin"]),
            ("GET application", a => a.Get("/api/v1/seller/application"), ["Owner", "Admin", "Manager", "Viewer"]),
            ("PUT business", a => a.Put("/api/v1/seller/application/business", Api.ValidBusiness), ["Owner"]),
            ("PUT kosher", a => a.Put("/api/v1/seller/application/kosher", new { answers = new Dictionary<string, string> { ["sells_food"] = "no" } }), ["Owner"]),
            ("submit (agreement)", a => a.Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = false }), ["Owner"]),
            ("leave", a => a.Post("/api/v1/team/leave"), []), // checked separately (destructive)
        };
        foreach (var (name, call, allowed) in matrix.Where(m => m.Name != "leave"))
            foreach (var (role, api) in actors)
            {
                var res = await call(api);
                if (allowed.Contains(role)) Assert.True(res.StatusCode != HttpStatusCode.Forbidden, $"{role} should be allowed: {name} -> {(int)res.StatusCode}");
                else Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{role} should be forbidden: {name} -> {(int)res.StatusCode}");
            }
        // the matrix table itself
        Assert.True(Permissions.Has(AccountRole.Owner, Permission.AccountClose));
        Assert.False(Permissions.Has(AccountRole.Admin, Permission.AccountClose));
        Assert.False(Permissions.Has(AccountRole.Admin, Permission.OwnershipTransfer));
        Assert.True(Permissions.Has(AccountRole.Manager, Permission.BrandWorkWrite));
        Assert.False(Permissions.Has(AccountRole.Viewer, Permission.BrandWorkWrite));
        Assert.True(Permissions.Has(AccountRole.Viewer, Permission.BrandWorkRead));
        // anonymous
        Assert.Equal(HttpStatusCode.Unauthorized, await new Api(f).Get("/api/v1/team/members", false).Code());
        Assert.Equal(HttpStatusCode.Unauthorized, await new Api(f).Get("/api/v1/brands", false).Code());
    }

    [Fact]
    public async Task Role_hierarchy_last_owner_and_ownership_transfer_with_step_up()
    {
        var (owner, ownerEmail, seller) = await TeamHelpers.NewOwner(f);
        var (admin, adminEmail) = await TeamHelpers.JoinAs(f, owner, "Admin");
        var (admin2, admin2Email) = await TeamHelpers.JoinAs(f, owner, "Admin");
        var (mgr, mgrEmail) = await TeamHelpers.JoinAs(f, owner, "Manager");
        var members = await owner.JsonBody(await owner.Get("/api/v1/team/members"));
        Guid Member(string e) => members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == e).GetProperty("id").GetGuid();
        // Admin cannot invite an Owner, cannot touch the Owner, cannot grant Owner
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Owner" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Patch($"/api/v1/team/members/{Member(ownerEmail)}", new { role = "Viewer" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Delete($"/api/v1/team/members/{Member(ownerEmail)}").Code());
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Patch($"/api/v1/team/members/{Member(mgrEmail)}", new { role = "Owner" }).Code());
        Assert.Equal(HttpStatusCode.Forbidden, await owner.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Owner" }).Code()); // invite Owner: nobody
        // nobody edits themselves
        Assert.Equal(HttpStatusCode.Forbidden, await admin.Patch($"/api/v1/team/members/{Member(adminEmail)}", new { role = "Viewer" }).Code());
        // Admin may manage a Manager and a peer Admin
        Assert.Equal(HttpStatusCode.OK, await admin.Patch($"/api/v1/team/members/{Member(mgrEmail)}", new { role = "Viewer" }).Code());
        Assert.Equal(HttpStatusCode.NoContent, await admin.Delete($"/api/v1/team/members/{Member(admin2Email)}").Code());
        // last owner: cannot leave, cannot be demoted by anyone, cannot be removed
        var leave = await owner.Post("/api/v1/team/leave");
        Assert.Equal(HttpStatusCode.Conflict, leave.StatusCode); Assert.Equal("last_owner", await leave.ErrCode());
        // transfer: wrong password -> refused, then OK
        var bad = await owner.Post("/api/v1/team/transfer-ownership", new { memberId = Member(adminEmail), currentPassword = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/team/transfer-ownership", new { memberId = Member(adminEmail) }).Code());
        var ok = await owner.Post("/api/v1/team/transfer-ownership", new { memberId = Member(adminEmail), currentPassword = ApiFactory.Strong });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var adminMid = Member(adminEmail); var ownerMid = Member(ownerEmail);
        Assert.Equal("Owner", f.WithDb(db => db.Memberships.Single(m => m.SellerId == seller && m.Id == adminMid).Role.ToString()));
        Assert.Equal("Admin", f.WithDb(db => db.Memberships.Single(m => m.SellerId == seller && m.Id == ownerMid).Role.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, await owner.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code()); // old owner no longer Owner
        Assert.Equal(HttpStatusCode.OK, await admin.Put("/api/v1/seller/application/business", Api.ValidBusiness).Code());
        Assert.Single(f.Mails(adminEmail, "team-ownership")); Assert.Single(f.Mails(ownerEmail, "team-ownership"));
        // the new sole owner is protected from demotion by the old owner, who is now just an Admin
        Assert.Equal(HttpStatusCode.Forbidden, await owner.Patch($"/api/v1/team/members/{Member(adminEmail)}", new { role = "Viewer" }).Code());
        // an Owner demoting the only other Owner is allowed only while another Owner remains: promote old owner back, then demote self is blocked as self-edit
        Assert.Equal(HttpStatusCode.OK, await admin.Patch($"/api/v1/team/members/{Member(ownerEmail)}", new { role = "Owner" }).Code());
        Assert.Equal(HttpStatusCode.OK, await admin.Patch($"/api/v1/team/members/{Member(ownerEmail)}", new { role = "Admin" }).Code()); // two owners -> one may be demoted
        var sole = await owner.Patch($"/api/v1/team/members/{Member(adminEmail)}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, sole.StatusCode);
    }

    [Fact]
    public async Task Brands_crud_logo_validation_archive_and_serving()
    {
        var (owner, _, seller) = await TeamHelpers.NewOwner(f);
        var first = await TeamHelpers.BrandId(owner);
        var id = await TeamHelpers.NewBrand(owner, "Sweet Things");
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/brands", new { name = "sweet things" }).Code()); // unique per account, case-insensitive
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/brands", new { name = "" }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/brands", new { name = "X", website = "javascript:alert(1)" }).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/brands", new { name = "X", contactEmail = "nope" }).Code());
        var up = await owner.Put($"/api/v1/brands/{id}", new { name = "Sweet Things Ltd", description = "Cakes", contactEmail = "hi@example.com", website = "https://example.com" });
        var j = await owner.JsonBody(up);
        Assert.Equal("sweet-things-ltd", j.GetProperty("slug").GetString());
        // logo: spoofed extension / wrong content rejected
        static MultipartFormDataContent Form(byte[] bytes, string name, string type)
        {
            var c = new ByteArrayContent(bytes); c.Headers.ContentType = new MediaTypeHeaderValue(type);
            return new MultipartFormDataContent { { c, "file", name } };
        }
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 };
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        async Task<HttpResponseMessage> Upload(byte[] b, string name, string type)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/brands/{id}/logo") { Content = Form(b, name, type) };
            return await owner.Send(req);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(html, "logo.png", "image/png")).StatusCode);   // spoofed extension
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(png, "logo.jpg", "image/jpeg")).StatusCode);   // extension != content
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(png, "logo.svg", "image/svg+xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(new byte[1024 * 1024 + 1].Select((_, i) => i < 8 ? png[i] : (byte)0).ToArray(), "big.png", "image/png")).StatusCode);
        var okUp = await Upload(png, "logo.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, okUp.StatusCode);
        Assert.True((await owner.JsonBody(okUp)).GetProperty("hasLogo").GetBoolean());
        var got = await owner.Get($"/api/v1/brands/{id}/logo");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("image/png", got.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", got.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NoContent, await owner.Delete($"/api/v1/brands/{id}/logo").Code());
        Assert.Equal(HttpStatusCode.NotFound, await owner.Get($"/api/v1/brands/{id}/logo").Code());
        // archive: not the last active brand
        Assert.Equal(HttpStatusCode.OK, await owner.Post($"/api/v1/brands/{id}/archive").Code());
        var last = await owner.Post($"/api/v1/brands/{first}/archive");
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode); Assert.Equal("last_brand", await last.ErrCode());
        var active = await owner.JsonBody(await owner.Get("/api/v1/brands"));
        Assert.Equal(1, active.GetArrayLength());
        var all = await owner.JsonBody(await owner.Get("/api/v1/brands?includeArchived=true"));
        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, await owner.Post($"/api/v1/brands/{id}/restore").Code());
        Assert.True(f.WithDb(db => db.AuditEvents.Any(a => a.Type == "brand.archived")));
    }

    [Fact]
    public async Task Brand_scope_is_enforced_server_side_and_changes_apply_immediately()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var a = await TeamHelpers.BrandId(owner);
        var b = await TeamHelpers.NewBrand(owner, "Brand B");
        var c = await TeamHelpers.NewBrand(owner, "Brand C");
        // scope must be non-empty and belong to the account
        var bad = await owner.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer", allBrands = false, brandIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, await owner.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer", brandIds = new[] { Guid.NewGuid() } }).Code());
        var (viewer, vEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer", [a]);
        Assert.Equal(1, (await viewer.JsonBody(await viewer.Get("/api/v1/brands"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, await viewer.Get($"/api/v1/brands/{b}").Code());
        var summary = viewer.Session!.Accounts!.Single();
        Assert.Equal(1, summary.BrandCount);
        // widen scope: effective on the same token
        var members = await owner.JsonBody(await owner.Get("/api/v1/team/members"));
        var mid = members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == vEmail).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await owner.Patch($"/api/v1/team/members/{mid}", new { brandIds = new[] { a, b } }).Code());
        Assert.Equal(2, (await viewer.JsonBody(await viewer.Get("/api/v1/brands"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, await viewer.Get($"/api/v1/brands/{b}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await viewer.Get($"/api/v1/brands/{c}").Code());
        // all brands, includes later brands
        Assert.Equal(HttpStatusCode.OK, await owner.Patch($"/api/v1/team/members/{mid}", new { allBrands = true }).Code());
        Assert.Equal(3, (await viewer.JsonBody(await viewer.Get("/api/v1/brands"))).GetArrayLength());
        // helper-level checks for products
        var access = new AccountAccess(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AccountRole.Manager, false, new HashSet<Guid> { a }, SellerStatus.Approved);
        Assert.True(access.CanWorkOnBrand(a, write: true)); Assert.False(access.CanWorkOnBrand(b, write: false));
        Assert.False((access with { Role = AccountRole.Viewer }).CanWorkOnBrand(a, write: true));
        Assert.True((access with { Role = AccountRole.Admin, BrandIds = new HashSet<Guid>() }).CanWorkOnBrand(b, write: true));
    }

    [Fact]
    public async Task No_idor_across_accounts_on_any_endpoint()
    {
        var (ownerX, _, sellerX) = await TeamHelpers.NewOwner(f);
        var (ownerY, _, sellerY) = await TeamHelpers.NewOwner(f);
        var brandY = await TeamHelpers.BrandId(ownerY);
        var invEmail = Api.NewEmail();
        var invY = await ownerY.JsonBody(await ownerY.Post("/api/v1/team/invitations", new { email = invEmail, role = "Viewer" }));
        var membersY = await ownerY.JsonBody(await ownerY.Get("/api/v1/team/members"));
        var memberY = membersY[0].GetProperty("id").GetGuid();
        var tgt = (await TeamHelpers.JoinAs(f, ownerY, "Viewer"));
        membersY = await ownerY.JsonBody(await ownerY.Get("/api/v1/team/members"));
        var viewerYId = membersY.EnumerateArray().Single(m => m.GetProperty("email").GetString() == tgt.email).GetProperty("id").GetGuid();
        // X (an Owner) uses Y's ids
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Get($"/api/v1/brands/{brandY}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Put($"/api/v1/brands/{brandY}", new { name = "Hacked" }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Post($"/api/v1/brands/{brandY}/archive").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Get($"/api/v1/brands/{brandY}/logo").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Delete($"/api/v1/brands/{brandY}/logo").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Patch($"/api/v1/team/members/{viewerYId}", new { role = "Admin" }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Delete($"/api/v1/team/members/{viewerYId}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Post("/api/v1/team/transfer-ownership", new { memberId = viewerYId, currentPassword = ApiFactory.Strong }).Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Post($"/api/v1/team/invitations/{invY.GetProperty("id").GetGuid()}/resend").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Delete($"/api/v1/team/invitations/{invY.GetProperty("id").GetGuid()}").Code());
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Post("/api/v1/session/account", new { sellerId = sellerY }).Code());
        // X cannot scope a member to Y's brand
        Assert.Equal(HttpStatusCode.BadRequest, await ownerX.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer", brandIds = new[] { brandY } }).Code());
        // someone else's pending invite (by id) is invisible
        Assert.Equal(HttpStatusCode.NotFound, await ownerX.Post($"/api/v1/account/invitations/{invY.GetProperty("id").GetGuid()}/accept").Code());
        // the lists only contain own data
        Assert.DoesNotContain(await ownerX.JsonBody(await ownerX.Get("/api/v1/brands")) is var l ? l.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()) : [], x => x == brandY);
        Assert.Equal(1, (await ownerX.JsonBody(await ownerX.Get("/api/v1/team/members"))).GetArrayLength());
        _ = (sellerX, memberY);
    }

    [Fact]
    public async Task Invites_are_throttled_per_inviter()
    {
        var (owner, ownerEmail, _) = await TeamHelpers.NewOwner(f);
        for (var i = 0; i < TeamRules.InvitesPerInviterPerHour; i++)
            Assert.Equal(HttpStatusCode.Accepted, await owner.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer" }).Code());
        var r = await owner.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer" });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        f.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        var fresh = new Api(f); await fresh.LoginAs(ownerEmail, ApiFactory.Strong); // the old token/session expired meanwhile
        Assert.Equal(HttpStatusCode.Accepted, await fresh.Post("/api/v1/team/invitations", new { email = Api.NewEmail(), role = "Viewer" }).Code());
    }

    [Fact]
    public async Task Join_refuses_existing_verified_login_and_requires_terms_and_strong_password()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var existing = new Api(f);
        var email = await existing.SignupVerified();
        await owner.Post("/api/v1/team/invitations", new { email, role = "Viewer" });
        var token = TeamHelpers.TokenFor(f, email);
        var r = await new Api(f).Post($"/api/v1/invitations/{token}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("sign_in_required", await r.ErrCode());
        var e2 = Api.NewEmail();
        await owner.Post("/api/v1/team/invitations", new { email = e2, role = "Viewer" });
        var t2 = TeamHelpers.TokenFor(f, e2);
        Assert.Equal(HttpStatusCode.BadRequest, await new Api(f).Post($"/api/v1/invitations/{t2}/join", new { password = ApiFactory.Strong, acceptTerms = false }, false).Code());
        Assert.Equal(HttpStatusCode.BadRequest, await new Api(f).Post($"/api/v1/invitations/{t2}/join", new { password = "weak", acceptTerms = true }, false).Code());
        // declining is anonymous and final
        Assert.Equal(HttpStatusCode.NoContent, await new Api(f).Post($"/api/v1/invitations/{t2}/decline", null, false).Code());
        Assert.Equal(HttpStatusCode.Gone, await new Api(f).Post($"/api/v1/invitations/{t2}/join", new { password = ApiFactory.Strong, acceptTerms = true }, false).Code());
    }

    [Fact]
    public async Task Invitation_tokens_are_stored_hashed_only()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var email = Api.NewEmail();
        await owner.Post("/api/v1/team/invitations", new { email, role = "Viewer" });
        var token = TeamHelpers.TokenFor(f, email);
        var row = f.WithDb(db => db.Invitations.Single(i => i.Email == email));
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(Solvelt.Api.Services.Crypto.Sha256(token), row.TokenHash);
        Assert.DoesNotContain(f.WithDb(db => db.AuditEvents.Select(a => a.Detail).ToList()), d => d != null && d.Contains(token));
        Assert.True((row.ExpiresAt - f.Time.GetUtcNow().UtcDateTime).TotalDays is > 6.9 and <= 7.0);
    }

    [Fact]
    public async Task Suspended_account_blocks_access_but_members_keep_other_accounts()
    {
        var (ownerA, _, sellerA) = await TeamHelpers.NewOwner(f);
        var (ownerB, _, sellerB) = await TeamHelpers.NewOwner(f);
        var user = new Api(f);
        var email = await user.SignupVerified();
        await ownerA.Post("/api/v1/team/invitations", new { email, role = "Manager" });
        await user.Post($"/api/v1/invitations/{TeamHelpers.TokenFor(f, email)}/accept");
        var s = new Api(f); await s.LoginAs(email, ApiFactory.Strong);
        await s.Switch(sellerA);
        Assert.Equal(HttpStatusCode.OK, await s.Get("/api/v1/brands").Code());
        // admin approves, then suspends account A
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        await ownerA.CompleteApplicationAndSubmit();
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{sellerA}/status", new { status = "Approved" }).Code());
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{sellerA}/status", new { status = "Suspended" }).Code());
        // member keeps the session (has own draft account), but A is no longer reachable
        Assert.Equal(HttpStatusCode.Forbidden, await s.Get("/api/v1/brands").Code());
        Assert.Equal(HttpStatusCode.OK, await s.Get("/api/v1/account/me").Code());
        // the owner's only account is suspended -> session revoked as before
        Assert.Equal(HttpStatusCode.Unauthorized, await ownerA.Get("/api/v1/account/me").Code());
        _ = sellerB; _ = ownerB;
    }
}
