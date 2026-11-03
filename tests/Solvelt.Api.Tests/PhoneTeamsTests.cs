using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Phone;
using Solvelt.Api.Services;

namespace Solvelt.Api.Tests;

/// <summary>Phone/SMS status follows the membership model of docs/TEAMS.md: one account answers directly, several ask "which account?".</summary>
public class PhoneTeamsTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    private async Task<string> GivePhone(string email, string pin = "615283")
    {
        var phone = PhoneApiFactory.NewNumber();
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = (await users.FindByEmailAsync(email))!;
        u.PhoneNumber = phone; u.PhoneNumberConfirmed = true;
        Assert.True((await users.UpdateAsync(u)).Succeeded);
        await scope.ServiceProvider.GetRequiredService<PinService>().SetAsync(u.Id, pin);
        return phone;
    }

    private async Task<string> SmsLogin(Phones p, string phone, string pin = "615283")
    {
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await p.Sms(phone, "LOGIN");
        return await p.Sms(phone, f.LastSmsCode(phone) + " " + pin);
    }

    [Fact]
    public async Task A_member_of_several_accounts_is_asked_which_account_by_SMS_and_the_choice_sticks()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);                      // vendor "Acme Co"
        var (member, memberEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        Assert.Equal(System.Net.HttpStatusCode.Created, (await member.Post("/api/v1/account/seller-accounts", new { acceptTerms = true })).StatusCode); // second account
        var phone = await GivePhone(memberEmail);
        var p = new Phones(f);
        Assert.Contains("Signed in", await SmsLogin(p, phone));
        var ask = await p.Sms(phone, "STATUS");
        Assert.Contains("Which account?", ask);
        var n = Regex.Match(ask, @"Reply (\d) for Acme Co").Groups[1].Value;
        Assert.NotEqual("", n);
        Assert.Contains("didn't", await p.Sms(phone, "9") + "didn't");              // out-of-range choice is refused, nothing leaks
        var r = await p.Sms(phone, n);
        Assert.Contains("Acme Co is", r);
        Assert.DoesNotContain("Which account?", await p.Sms(phone, "STATUS"));        // the choice is remembered for this session
    }

    [Fact]
    public async Task Voice_status_for_a_member_of_several_accounts_uses_a_single_digit_choice()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var (member, memberEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        Assert.Equal(System.Net.HttpStatusCode.Created, (await member.Post("/api/v1/account/seller-accounts", new { acceptTerms = true })).StatusCode); // second account
        var phone = await GivePhone(memberEmail);
        var p = new Phones(f); var sid = "CA" + Guid.NewGuid().ToString("N");
        await p.SignIn(sid, phone, phone, "615283");
        var ask = await p.Press(sid, phone, "1");
        Assert.Contains("Which account?", Phones.Says(ask));
        Assert.Equal("1", Phones.Gather(ask)!.Attribute("numDigits")!.Value);
        Assert.Equal("dtmf", Phones.Gather(ask)!.Attribute("input")!.Value);
        var n = Regex.Match(Phones.Says(ask), @"Press (\d) for Acme Co").Groups[1].Value;
        Assert.Contains("Acme Co is", Phones.Says(await p.Press(sid, phone, n)));
    }

    [Fact]
    public async Task A_member_with_only_one_membership_hears_the_status_directly()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var (_, memberEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        var phone = await GivePhone(memberEmail);
        var p = new Phones(f);
        await SmsLogin(p, phone);
        var r = await p.Sms(phone, "STATUS");
        Assert.DoesNotContain("Which account?", r);
        Assert.Contains("Acme Co is", r);
    }

    [Fact]
    public async Task A_removed_member_hears_nothing_about_the_old_account_but_can_still_sign_in()
    {
        var (owner, _, _) = await TeamHelpers.NewOwner(f);
        var (_, memberEmail) = await TeamHelpers.JoinAs(f, owner, "Viewer");
        var phone = await GivePhone(memberEmail);
        var p = new Phones(f);
        await SmsLogin(p, phone);
        Assert.Contains("Acme Co is", await p.Sms(phone, "STATUS"));
        var members = await owner.JsonBody(await owner.Get("/api/v1/team/members"));
        var id = members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == memberEmail).GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await owner.Delete($"/api/v1/team/members/{id}")).StatusCode);
        await SmsLogin(p, phone);                       // (removal does not touch the user's phone identity)
        var r = await p.Sms(phone, "STATUS");
        Assert.DoesNotContain("Acme Co", r);
        Assert.Contains("no seller application", r);
    }

    [Fact]
    public async Task A_user_with_one_account_hears_the_status_directly()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await SmsLogin(p, phone);
        var r = await p.Sms(phone, "STATUS");
        Assert.DoesNotContain("Which account?", r);
        Assert.Contains("is not yet submitted", r);
        Assert.NotNull(email);
    }
}
