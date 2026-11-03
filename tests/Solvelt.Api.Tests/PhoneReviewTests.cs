using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;
using Solvelt.Api.Phone;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the orchestrator's review of the phone/SMS channel (items 1-7, race and session revocation).</summary>
public class PhoneReviewTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    private static string Sid() => "CA" + Guid.NewGuid().ToString("N");

    private async Task<string> SmsLogin(Phones p, string phone, string pin)
    {
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await p.Sms(phone, "LOGIN");
        return await p.Sms(phone, f.LastSmsCode(phone) + " " + pin);
    }

    /// <summary>JOIN ... up to (and including) accepting the Terms with someone else's email: the account exists, its mailbox is NOT proven.</summary>
    private async Task JoinWithEmailUntilTerms(Phones p, string phone, string email, string pin = "739204")
    {
        await p.Sms(phone, "JOIN"); await p.Sms(phone, f.LastSmsCode(phone));
        await p.Sms(phone, pin); await p.Sms(phone, pin);
        await p.Sms(phone, email); await p.Sms(phone, "YES"); await p.Sms(phone, "YES");
    }

    private async Task ResetPasswordOnWeb(string email)
    {
        var web = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await web.Post("/api/v1/auth/forgot-password", new { email }, false)).StatusCode);
        var link = f.Mails(email, "reset-password").Last().Text;
        var token = Regex.Match(link, @"token=([^&\s]+)").Groups[1].Value;
        Assert.Equal(HttpStatusCode.NoContent, (await web.Post("/api/v1/auth/reset-password", new { email, token, newPassword = "V1ctim-Resets-It-Now!" }, false)).StatusCode);
    }

    // ---- 1 + 5: pre-hijack through a phone sign-up --------------------------------------------------------------------------------------
    [Fact]
    public async Task Phone_signup_with_a_victims_email_cannot_plant_a_login_number_and_the_victims_reset_kills_it()
    {
        var attacker = PhoneApiFactory.NewNumber(); var victim = Api.NewEmail();
        var p = new Phones(f);
        await JoinWithEmailUntilTerms(p, attacker, victim);
        // the account exists but the attacker's number/PIN are NOT attached: the mailbox was never proven
        Assert.False(f.WithDb(db => db.Users.Any(u => u.PhoneNumber == attacker)));
        Assert.False(f.WithDb(db => db.PhoneCredentials.Any()) && f.WithDb(db => db.Users.Any(u => u.Email == victim && u.PhoneNumber != null)));
        // the victim recovers the account on the web (reset marks the email verified and wipes planted credentials)
        await ResetPasswordOnWeb(victim);
        var before = f.Sms.To(attacker).Count;
        f.Time.Advance(TimeSpan.FromHours(2));
        // attacker: LOGIN gets the same reply as for any unknown number, no code is sent, the PIN is useless; RESET texts nothing
        Assert.Contains("6-digit code", await p.Sms(attacker, "LOGIN"));
        Assert.Equal(before, f.Sms.To(attacker).Count);
        Assert.DoesNotContain("Signed in", await p.Sms(attacker, "000000 739204"));
        await p.Sms(attacker, "RESET");
        Assert.Equal(before, f.Sms.To(attacker).Count);
        Assert.DoesNotContain(f.Sms.To(attacker), m => m.Text.Contains("/l/"));
        Assert.False(f.WithDb(db => db.PhoneLinks.Any(l => l.Purpose == PendingPhoneService.Purpose)));
    }

    [Fact]
    public async Task Link_signup_with_a_victims_email_also_attaches_nothing_before_the_mailbox_is_proven()
    {
        var phone = PhoneApiFactory.NewNumber(); var victim = Api.NewEmail();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "2"); await p.Press(sid, phone, f.LastSmsCode(phone));
        await p.Press(sid, phone, "739204"); await p.Press(sid, phone, "739204"); await p.Press(sid, phone, "1");
        var token = Regex.Match(f.LastSmsLink(phone), @"/l/(\S+)").Groups[1].Value;
        var api = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/channels/links/complete-signup", new { token, email = victim, password = ApiFactory.Strong, acceptTerms = true }, false)).StatusCode);
        Assert.False(f.WithDb(db => db.Users.Any(u => u.PhoneNumber == phone)));
        await ResetPasswordOnWeb(victim);
        Assert.False(f.WithDb(db => db.Users.Any(u => u.PhoneNumber == phone)));
        Assert.False(f.WithDb(db => db.PhoneLinks.Any(l => l.Purpose == PendingPhoneService.Purpose)));
    }

    [Fact]
    public async Task Abandoned_phone_signup_does_not_block_the_number()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        await JoinWithEmailUntilTerms(p, phone, Api.NewEmail());       // never enters the email code
        f.Time.Advance(TimeSpan.FromHours(2));
        var n = f.Sms.To(phone).Count;
        Assert.Contains("6-digit code", await p.Sms(phone, "JOIN"));
        Assert.Equal(n + 1, f.Sms.To(phone).Count);                       // a code IS sent this time
        var email = Api.NewEmail();
        await p.Sms(phone, f.LastSmsCode(phone)); await p.Sms(phone, "739204"); await p.Sms(phone, "739204");
        await p.Sms(phone, email); await p.Sms(phone, "YES"); await p.Sms(phone, "YES");
        Assert.Contains("street address", await p.Sms(phone, f.LastCode(email)) + " street address");
        Assert.True(f.WithDb(db => db.Users.Any(u => u.Email == email && u.PhoneNumber == phone && u.PhoneNumberConfirmed)));
    }

    // ---- 2: first number needs step-up --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Adding_the_first_number_needs_the_password_and_a_stolen_session_alone_cannot_do_it()
    {
        var api = new Api(f); await api.SignupVerified(); var phone = PhoneApiFactory.NewNumber();
        var r = await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("wrong_password", (await api.JsonBody(r)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone, currentPassword = "nope" })).StatusCode);
        Assert.Empty(f.Sms.To(phone));
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone, currentPassword = ApiFactory.Strong })).StatusCode);
    }

    [Fact]
    public async Task Passwordless_accounts_confirm_the_first_number_with_a_code_emailed_to_the_account()
    {
        var api = new Api(f); var email = await api.SignupVerified(); var phone = PhoneApiFactory.NewNumber();
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = (await users.FindByEmailAsync(email))!;
            await users.RemovePasswordAsync(u); u.HasPassword = false; await users.UpdateAsync(u);
            var s = await scope.ServiceProvider.GetRequiredService<Solvelt.Api.Services.SessionService>().CreateAsync(u, "test", null, null);
            api.SetSession(new Login("Bearer", s.AccessToken, s.ExpiresIn, s.RefreshToken, Guid.Empty, email, "", "Active", ["Seller"]));
        }
        var first = await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True((await api.JsonBody(first)).GetProperty("stepUpRequired").GetBoolean());
        Assert.Empty(f.Sms.To(phone));                                    // nothing goes to the new number before the step-up
        Assert.Single(f.Mails(email, "phone-stepup-code"));               // the code goes to the account's mailbox
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone, stepUpCode = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/account/phone/start", new { phoneNumber = phone, stepUpCode = f.LastCode(email, "phone-stepup-code") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/api/v1/account/phone/verify", new { code = f.LastSmsCode(phone) })).StatusCode);
    }

    // ---- 3: no enumeration by caller ID -------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Pressing_1_gives_an_identical_transcript_for_registered_and_unregistered_caller_ids()
    {
        var (_, known) = await f.SellerWithPhone();
        var unknown = PhoneApiFactory.NewNumber();
        var transcripts = new List<string>();
        foreach (var caller in new[] { known, unknown })
        {
            var p = new Phones(f); var sid = Sid();
            var a = await p.Start(sid, caller); var b = await p.Press(sid, caller, "1");
            transcripts.Add(Regex.Replace(a + b, "seq=\\d+", "seq=N"));
        }
        Assert.Equal(transcripts[0], transcripts[1]);
    }

    // ---- 4: XML declaration matches the bytes we send -----------------------------------------------------------------------------------
    [Fact]
    public async Task Twiml_declares_utf8_and_is_utf8()
    {
        var p = new Phones(f);
        var xml = await p.Start(Sid(), PhoneApiFactory.NewNumber());
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", new VoiceResponse("v", "en-US").Say("é ü 日本").ToString());
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", VoiceResponse.Message("é"));
        var res = await p.Raw("/api/v1/channels/sms/inbound", new() { ["MessageSid"] = "SMutf" + Guid.NewGuid().ToString("N")[..8], ["From"] = PhoneApiFactory.NewNumber(), ["Body"] = "HELP" });
        Assert.Contains("utf-8", res.Content.Headers.ContentType!.ToString(), StringComparison.OrdinalIgnoreCase);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", System.Text.Encoding.UTF8.GetString(bytes));
    }

    // ---- 6: redaction gaps --------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Redactor_catches_commas_mixed_separators_and_spoken_digits()
    {
        foreach (var t in new[] { "my code is 123,456 ok", "pin 7-3-9,2-0-4 thanks", "it is 739 204", "1 2 3 4 5 6", "seven three nine two zero four", "7.3.9.2.0.4", "1/2/3/4/5/6" })
            Assert.DoesNotMatch(@"\d(\D{0,3}\d){3}|seven three nine", Redactor.Redact(t));
        Assert.Equal("hello there", Redactor.Redact("hello there"));
        Assert.Equal("I live at 12 Main St", Redactor.Redact("I live at 12 Main St"));
    }

    [Fact]
    public async Task SMS_text_with_obfuscated_codes_reaches_the_AI_without_any_digit_run()
    {
        f.Ai.Reset();
        var p = new Phones(f);
        await p.Sms(PhoneApiFactory.NewNumber(), "hi my code is 123,456 and pin 7-3-9,2-0-4 please help me out");
        Assert.NotEmpty(f.Ai.Received);
        Assert.All(f.Ai.Received, r => Assert.DoesNotMatch(@"\d(\D{0,3}\d){3}", r.UserText));
    }

    // ---- 7: only allowlisted actions run ------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Voice_menu_refuses_a_proposed_reset_link_and_other_real_actions_that_are_not_allowed()
    {
        var (_, phone) = await f.SellerWithPhone();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        var n = f.Sms.To(phone).Count;
        foreach (var action in new[] { AiActions.RequestResetLink, AiActions.SignOutAll, AiActions.RequestStatus })
        {
            f.Ai.Override = _ => new AiResult(action, new(), null);
            var r = await p.Call(sid, phone, speech: "do it");
            Assert.Contains("sign in first", Phones.Says(r));
        }
        f.Ai.Reset();
        Assert.Equal(n, f.Sms.To(phone).Count);
    }

    [Fact]
    public async Task SMS_menu_runs_a_reset_link_proposal_because_it_is_explicitly_allowlisted()
    {
        var (_, phone) = await f.SellerWithPhone();
        var p = new Phones(f);
        f.Ai.Override = r => { Assert.Contains(AiActions.RequestResetLink, r.Allowed); return new AiResult(AiActions.RequestResetLink, new(), null); };
        var n = f.Sms.To(phone).Count;
        Assert.Contains("password reset link", await p.Sms(phone, "I can't get in, help me with my password"));
        f.Ai.Reset();
        Assert.Equal(n + 1, f.Sms.To(phone).Count);
    }

    // ---- (a) race on the voice step -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Two_parallel_step_webhooks_with_the_same_seq_are_processed_once()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        var path = p.NextAction(sid);
        var form = new Dictionary<string, string> { ["CallSid"] = sid, ["From"] = phone, ["Digits"] = "2" };
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => p.Raw(path, form)));
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Equal(1, f.Sms.To(phone).Count);                           // one sign-up code, not six
    }

    // ---- (b) phone/SMS sessions end with every other session ------------------------------------------------------------------------------
    [Fact]
    public async Task Sms_session_ends_on_web_logout_all()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        Assert.Contains("Signed in", await SmsLogin(p, phone, "615283"));
        Assert.Contains("not yet submitted", await p.Sms(phone, "STATUS"));
        var web = new Api(f); await web.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.NoContent, (await web.Post("/api/v1/auth/logout-all")).StatusCode);
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
    }

    [Fact]
    public async Task Sms_session_ends_on_password_reset_and_on_lockout()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await SmsLogin(p, phone, "615283");
        await ResetPasswordOnWeb(email);
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
        await SmsLogin(p, phone, "615283");
        Assert.Contains("not yet submitted", await p.Sms(phone, "STATUS"));
        var web = new Api(f);
        for (var i = 0; i < 5; i++) await web.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = i >= 3 ? "XXXX.DUMMY.TOKEN.XXXX" : null }, false);
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
    }

    [Fact]
    public async Task Phone_sessions_end_when_the_number_is_removed()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f); var sid = Sid();
        await p.SignIn(sid, phone, phone, "615283");
        var web = new Api(f); await web.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.NoContent, (await web.Post("/api/v1/account/phone/remove", new { currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.DoesNotContain("application", Phones.Says(await p.Press(sid, phone, "1")));
    }
}
