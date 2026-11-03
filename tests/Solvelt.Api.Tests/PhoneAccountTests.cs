using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Phone;

namespace Solvelt.Api.Tests;

public class PhoneAccountTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    private async Task AddNumber(Api api, string phone, string? stepUp = ApiFactory.Strong)
    {
        var s = await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone, currentPassword = stepUp });
        Assert.Equal(HttpStatusCode.Accepted, s.StatusCode);
        var v = await api.Post("/api/v1/account/phone/verify", new { code = f.LastSmsCode(phone) });
        Assert.Equal(HttpStatusCode.OK, v.StatusCode);
    }

    [Fact]
    public async Task Add_number_with_sms_code_then_set_PIN_with_strength_rules()
    {
        var api = new Api(f); var email = await api.SignupVerified(); var phone = PhoneApiFactory.NewNumber();
        Assert.False((await api.JsonBody(await api.Get("/api/v1/account/phone"))).GetProperty("hasNumber").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put("/api/v1/account/phone/pin", new { pin = "739204", currentPassword = ApiFactory.Strong })).StatusCode); // no number yet
        Assert.Equal(HttpStatusCode.UnprocessableEntity.GetHashCode() == 422 ? HttpStatusCode.BadRequest : HttpStatusCode.BadRequest,
            (await api.Post("/api/v1/account/phone/start", new { phoneNumber = "123" })).StatusCode);
        await AddNumber(api, phone);
        Assert.Single(f.Mails(email, "phone-added"));
        var st = await api.JsonBody(await api.Get("/api/v1/account/phone"));
        Assert.Equal(phone[^4..], st.GetProperty("numberEnding").GetString());
        Assert.False(st.GetProperty("hasPin").GetBoolean());
        foreach (var weak in new[] { "123456", "111111", "12345", "abcdef" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Put("/api/v1/account/phone/pin", new { pin = weak, currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put("/api/v1/account/phone/pin", new { pin = "739204", currentPassword = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Put("/api/v1/account/phone/pin", new { pin = "739204", currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.True((await api.JsonBody(await api.Get("/api/v1/account/phone"))).GetProperty("hasPin").GetBoolean());
        Assert.Single(f.Mails(email, "phone-pin-changed"));
        // the PIN is stored hashed
        Assert.DoesNotContain("739204", f.WithDb(db => db.PhoneCredentials.Select(c => c.PinHash).ToList()));
        // the new credentials work on the phone channel
        var p = new Phones(f); var sid = "CA" + Guid.NewGuid().ToString("N");
        Assert.Contains("signed in", Phones.Says(await p.SignIn(sid, phone, phone, "739204")));
    }

    [Fact]
    public async Task A_number_belongs_to_one_account_and_taking_it_is_not_revealed()
    {
        var (_, phone) = await f.SellerWithPhone();
        var other = new Api(f); await other.SignupVerified();
        var before = f.Sms.To(phone).Count;
        var s = await other.Post("/api/v1/account/phone/start", new { phoneNumber = phone, currentPassword = ApiFactory.Strong });
        Assert.Equal(HttpStatusCode.Accepted, s.StatusCode);                      // same answer as for a free number
        Assert.Equal(before, f.Sms.To(phone).Count);                              // but nothing is sent
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Post("/api/v1/account/phone/verify", new { code = "123456" })).StatusCode);
        // the DB itself enforces uniqueness
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = (await users.FindByEmailAsync(other.Session!.Email))!;
        u.PhoneNumber = phone; u.PhoneNumberConfirmed = true;
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => users.UpdateAsync(u));
    }

    [Fact]
    public async Task Changing_the_number_needs_step_up_and_notifies_the_email()
    {
        var api = new Api(f); var email = await api.SignupVerified(); var a = PhoneApiFactory.NewNumber(); var b = PhoneApiFactory.NewNumber();
        await AddNumber(api, a);
        var noStepUp = await api.Post("/api/v1/account/phone/start", new { phoneNumber = b });
        Assert.Equal(HttpStatusCode.BadRequest, noStepUp.StatusCode);
        Assert.Empty(f.Sms.To(b));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = b, currentPassword = "nope" })).StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(61)); // resend cool-down of the code mechanism
        await AddNumber(api, b, ApiFactory.Strong);
        Assert.Single(f.Mails(email, "phone-changed"));
        Assert.Equal(b[^4..], (await api.JsonBody(await api.Get("/api/v1/account/phone"))).GetProperty("numberEnding").GetString());
    }

    [Fact]
    public async Task Removing_the_number_needs_step_up_and_removes_the_PIN()
    {
        var (email, phone) = await f.SellerWithPhone();
        var api = new Api(f); await api.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/account/phone/remove", new { currentPassword = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Post("/api/v1/account/phone/remove", new { currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Single(f.Mails(email, "phone-removed"));
        Assert.False((await api.JsonBody(await api.Get("/api/v1/account/phone"))).GetProperty("hasNumber").GetBoolean());
        var p = new Phones(f); var sid = "CA" + Guid.NewGuid().ToString("N");
        Assert.DoesNotContain("signed in", Phones.Says(await p.SignIn(sid, phone, phone, "482915", code: "000000")));
    }

    [Fact]
    public async Task Passwordless_accounts_step_up_with_a_code_texted_to_the_current_number()
    {
        // sign up by phone link: the account has no password but a verified number
        var (email, phone) = await f.SellerWithPhone();
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = (await users.FindByEmailAsync(email))!;
            await users.RemovePasswordAsync(u); u.HasPassword = false; await users.UpdateAsync(u);
        }
        var api = new Api(f);
        // sign in via the phone-session-less route: create a session directly
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var s = await scope.ServiceProvider.GetRequiredService<Solvelt.Api.Services.SessionService>().CreateAsync((await users.FindByEmailAsync(email))!, "test", null, null);
            api.SetSession(new Login("Bearer", s.AccessToken, s.ExpiresIn, s.RefreshToken, Guid.Empty, email, "", "Active", ["Seller"]));
        }
        var first = await api.Post("/api/v1/account/phone/remove", new { });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(phone, f.Sms.To(phone).Last().To);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/account/phone/remove", new { stepUpCode = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Post("/api/v1/account/phone/remove", new { stepUpCode = f.LastSmsCode(phone) })).StatusCode);
    }

    [Fact]
    public async Task Unverified_email_accounts_cannot_use_the_phone_endpoints()
    {
        var api = new Api(f); await api.UnverifiedSession();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = PhoneApiFactory.NewNumber() })).StatusCode);
    }

    [Fact]
    public async Task Rejected_or_suspended_sellers_cannot_sign_in_by_phone()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var seller = new Api(f); await seller.LoginAs(email, ApiFactory.Strong);
        await seller.CompleteApplicationAndSubmit();
        var id = seller.Session!.SellerId;
        Assert.True((await admin.Post($"/api/v1/admin/sellers/{id}/status", new { status = "Rejected" })).IsSuccessStatusCode);
        var p = new Phones(f); var sid = "CA" + Guid.NewGuid().ToString("N");
        Assert.DoesNotContain("signed in", Phones.Says(await p.SignIn(sid, phone, phone, "615283")));
    }
}
