using System.Net;
using System.Text.RegularExpressions;
using Solvelt.Api.Phone;

namespace Solvelt.Api.Tests;

public class PhoneReview2Tests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    private static string Sid() => "CA" + Guid.NewGuid().ToString("N");

    private async Task JoinUntilTerms(Phones p, string phone, string email)
    {
        await p.Sms(phone, "JOIN"); await p.Sms(phone, f.LastSmsCode(phone));
        await p.Sms(phone, "739204"); await p.Sms(phone, "739204");
        await p.Sms(phone, email); await p.Sms(phone, "YES"); await p.Sms(phone, "YES");
    }

    // ---- 1: the real owner verifying on the web must not inherit somebody else's held number + PIN ----------------------------------------
    [Fact]
    public async Task Victim_verifying_the_email_on_the_web_does_not_get_the_attackers_number_and_PIN()
    {
        var attacker = PhoneApiFactory.NewNumber(); var victim = Api.NewEmail();
        var p = new Phones(f);
        await JoinUntilTerms(p, attacker, victim);                       // attacker stops before the email code
        var web = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await web.Post("/api/v1/auth/resend-code", new { email = victim }, false)).StatusCode);
        var code = f.LastCode(victim);
        Assert.Equal(HttpStatusCode.OK, (await web.Post("/api/v1/auth/verify-email", new { email = victim, code }, false)).StatusCode);
        Assert.False(f.WithDb(db => db.Users.Any(u => u.Email == victim && (u.PhoneNumber != null || u.PhoneNumberConfirmed))));
        Assert.False(f.WithDb(db => db.PhoneCredentials.Any(c => db.Users.Any(u => u.Id == c.UserId && u.Email == victim))));
        Assert.False(f.WithDb(db => db.PhoneLinks.Any(l => l.Purpose == PendingPhoneService.Purpose && db.Users.Any(u => u.Id == l.UserId && u.Email == victim))));   // the held data is dropped
        var n = f.Sms.To(attacker).Count;
        Assert.Contains("6-digit code", await p.Sms(attacker, "LOGIN"));
        Assert.Equal(n, f.Sms.To(attacker).Count);                          // no code is sent to the attacker's number
        Assert.DoesNotContain("Signed in", await p.Sms(attacker, "000000 739204"));
        // and an attacker's entering the code in the phone conversation after the web verify finds nothing to attach either
        Assert.False(f.WithDb(db => db.Users.Any(u => u.PhoneNumber == attacker)));
    }

    [Fact]
    public async Task Link_signup_attaches_only_in_the_browser_that_completed_the_link()
    {
        // attacker completes a link with the VICTIM's email in their browser; the victim verifies in another browser
        var phone = PhoneApiFactory.NewNumber(); var victim = Api.NewEmail();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "2"); await p.Press(sid, phone, f.LastSmsCode(phone));
        await p.Press(sid, phone, "739204"); await p.Press(sid, phone, "739204"); await p.Press(sid, phone, "1");
        var token = Regex.Match(f.LastSmsLink(phone), @"/l/(\S+)").Groups[1].Value;
        var attackerBrowser = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await attackerBrowser.Post("/api/v1/channels/links/complete-signup", new { token, email = victim, password = ApiFactory.Strong, acceptTerms = true }, false)).StatusCode);
        var victimBrowser = new Api(f);
        Assert.Equal(HttpStatusCode.OK, (await victimBrowser.Post("/api/v1/auth/verify-email", new { email = victim, code = f.LastCode(victim) }, false)).StatusCode);
        Assert.False(f.WithDb(db => db.Users.Any(u => u.PhoneNumber == phone)));

        // the legitimate owner of the number verifies in the SAME browser: attached
        var phone2 = PhoneApiFactory.NewNumber(); var own = Api.NewEmail(); var sid2 = Sid();
        await p.Start(sid2, phone2); await p.Press(sid2, phone2, "2"); await p.Press(sid2, phone2, f.LastSmsCode(phone2));
        await p.Press(sid2, phone2, "739204"); await p.Press(sid2, phone2, "739204"); await p.Press(sid2, phone2, "1");
        var token2 = Regex.Match(f.LastSmsLink(phone2), @"/l/(\S+)").Groups[1].Value;
        var browser = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await browser.Post("/api/v1/channels/links/complete-signup", new { token = token2, email = own, password = ApiFactory.Strong, acceptTerms = true }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Post("/api/v1/auth/verify-email", new { email = own, code = f.LastCode(own) }, false)).StatusCode);
        Assert.True(f.WithDb(db => db.Users.Any(u => u.Email == own && u.PhoneNumber == phone2 && u.PhoneNumberConfirmed)));
    }

    // ---- 2: an SMS/phone session does not survive a number change ------------------------------------------------------------------------
    [Fact]
    public async Task Sms_session_on_the_old_number_ends_when_the_number_changes()
    {
        var (email, a) = await f.SellerWithPhone("615283");
        var b = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await p.Sms(a, "LOGIN");
        Assert.Contains("Signed in", await p.Sms(a, f.LastSmsCode(a) + " 615283"));
        Assert.Contains("not yet submitted", await p.Sms(a, "STATUS"));
        var web = new Api(f); await web.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.Accepted, (await web.Post("/api/v1/account/phone/start", new { phoneNumber = b, currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Post("/api/v1/account/phone/verify", new { code = f.LastSmsCode(b) })).StatusCode);
        var r = await p.Sms(a, "STATUS");
        Assert.Contains("sign in first", r);
        Assert.DoesNotContain("not yet submitted", r);
    }

    [Fact]
    public async Task A_phone_session_state_that_survived_a_number_change_is_still_refused()
    {
        // belt and braces: even if the state row were left behind, the engine compares the session's number with the account's current number
        var (email, a) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        f.Time.Advance(TimeSpan.FromSeconds(61));
        await p.Sms(a, "LOGIN");
        await p.Sms(a, f.LastSmsCode(a) + " 615283");
        f.WithDb(db => { var u = db.Users.Single(x => x.Email == email); u.PhoneNumber = PhoneApiFactory.NewNumber(); db.SaveChanges(); return 0; }); // changed behind the engine's back
        Assert.Contains("sign in again", await p.Sms(a, "STATUS"));
    }
}
