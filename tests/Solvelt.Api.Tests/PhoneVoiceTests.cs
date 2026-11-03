using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Phone;

namespace Solvelt.Api.Tests;

public class PhoneVoiceTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    private static string Sid() => "CA" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Unsigned_and_wrongly_signed_webhooks_are_rejected()
    {
        var p = new Phones(f);
        var form = new Dictionary<string, string> { ["CallSid"] = Sid(), ["From"] = PhoneApiFactory.NewNumber() };
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Raw("/api/v1/channels/voice/incoming", form, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Raw("/api/v1/channels/voice/incoming", form, signature: "AAAA")).StatusCode);
        // a valid signature for a different body does not carry over
        var other = new Dictionary<string, string>(form) { ["From"] = "+14155550199" };
        var good = LamlSignatureValidator.Sign(PhoneApiFactory.Key, "https://localhost/api/v1/channels/voice/incoming", other);
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Raw("/api/v1/channels/voice/incoming", form, signature: good)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Raw("/api/v1/channels/sms/inbound", new() { ["MessageSid"] = "SM1", ["From"] = "+14155550101", ["Body"] = "HELP" }, sign: false)).StatusCode);
    }

    [Fact]
    public async Task Greeting_discloses_AI_and_gather_uses_speech_and_dtmf_for_choices_but_dtmf_only_for_secrets()
    {
        var p = new Phones(f); var sid = Sid(); var phone = PhoneApiFactory.NewNumber();
        var xml = await p.Start(sid, phone);
        Assert.Contains("AI", Phones.Says(xml));
        var g = Phones.Gather(xml)!;
        Assert.Equal("speech dtmf", g.Attribute("input")!.Value);
        Assert.Equal("#", g.Attribute("finishOnKey")!.Value);
        Assert.Equal("true", g.Attribute("actionOnEmptyResult")!.Value);
        var known = await f.SellerWithPhone();
        var sid2 = Sid();
        await p.Start(sid2, known.Phone);
        var pinPrompt = await p.Press(sid2, known.Phone, "1");
        Assert.Equal("dtmf", Phones.Gather(pinPrompt)!.Attribute("input")!.Value); // PIN: keypad only, never transcribed
    }

    [Fact]
    public async Task Known_caller_signs_in_with_PIN_and_hears_status()
    {
        var (email, phone) = await f.SellerWithPhone("739204");
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        await p.Press(sid, phone, "1");
        var menu = await p.Press(sid, phone, "739204");
        Assert.Contains("signed in", Phones.Says(menu));
        var status = await p.Press(sid, phone, "1");
        Assert.Contains("not yet submitted", Phones.Says(status));
        var bye = await p.Press(sid, phone, "0");
        Assert.True(Phones.HungUp(bye));
        Assert.NotNull(email);
    }

    [Fact]
    public async Task Spoofed_caller_id_cannot_sign_in_without_the_PIN()
    {
        var (_, phone) = await f.SellerWithPhone("739204");
        var attacker = new Phones(f); var sid = Sid();
        await attacker.Start(sid, phone); // caller ID == victim's number
        await attacker.Press(sid, phone, "1");
        var r = await attacker.Press(sid, phone, "111222");
        Assert.DoesNotContain("signed in", Phones.Says(r));
        Assert.Contains("didn't match", Phones.Says(r));
        // and no status/reset menu is reachable: pressing 1 now is the sign-in choice again, not "status"
        var r2 = await attacker.Press(sid, phone, "1");
        Assert.DoesNotContain("application", Phones.Says(r2));
        Assert.DoesNotContain(f.Sms.To(phone), m => m.Text.Contains("reset"));
    }

    [Fact]
    public async Task Unknown_caller_signs_in_with_account_number_SMS_code_and_PIN()
    {
        var (_, phone) = await f.SellerWithPhone("615283");
        var caller = PhoneApiFactory.NewNumber();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, caller);
        var ask = await p.Press(sid, caller, "1");
        Assert.Contains("mobile number", Phones.Says(ask));
        var before = f.Sms.To(phone).Count;
        var codePrompt = await p.Call(sid, caller, digits: phone.TrimStart('+'));
        Assert.Contains("6 digit code", Phones.Says(codePrompt));
        Assert.Equal(before + 1, f.Sms.To(phone).Count); // the code goes to the ACCOUNT's number, not to the caller
        Assert.Empty(f.Sms.To(caller));
        var pinPrompt = await p.Press(sid, caller, f.LastSmsCode(phone));
        Assert.Contains("PIN", Phones.Says(pinPrompt));
        var ok = await p.Press(sid, caller, "615283");
        Assert.Contains("signed in", Phones.Says(ok));
    }

    [Fact]
    public async Task Unknown_numbers_get_identical_prompts_and_failure_and_no_sms()
    {
        var (_, known) = await f.SellerWithPhone("615283");
        var unknown = PhoneApiFactory.NewNumber();
        var transcripts = new List<string>();
        foreach (var target in new[] { known, unknown })
        {
            var caller = PhoneApiFactory.NewNumber();
            var p = new Phones(f); var sid = Sid();
            var lines = new List<string>();
            lines.Add(Phones.Says(await p.Start(sid, caller)));
            lines.Add(Phones.Says(await p.Press(sid, caller, "1")));
            lines.Add(Phones.Says(await p.Call(sid, caller, digits: target.TrimStart('+'))));
            lines.Add(Phones.Says(await p.Press(sid, caller, "000000")));  // wrong code
            lines.Add(Phones.Says(await p.Press(sid, caller, "739204")));  // wrong PIN
            transcripts.Add(string.Join("\n", lines));
        }
        Assert.Equal(transcripts[0], transcripts[1]);
        Assert.Empty(f.Sms.To(unknown));
    }

    [Fact]
    public async Task Lockout_is_shared_with_the_web_3_4_5_and_sends_the_lockout_email()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var web = new Api(f);
        // two web failures + the phone failures continue the same counter
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await web.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false)).StatusCode);
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        await p.Press(sid, phone, "1");
        var third = await p.Press(sid, phone, "111222"); // 3rd failure overall
        Assert.Contains("2 tries left", Phones.Says(third));
        await p.Press(sid, phone, "1");
        var fourth = await p.Press(sid, phone, "111222");
        Assert.Contains("Last try", Phones.Says(fourth));
        await p.Press(sid, phone, "1");
        var fifth = await p.Press(sid, phone, "111222");
        Assert.Contains("Too many attempts", Phones.Says(fifth));
        Assert.True(Phones.HungUp(fifth));
        Assert.Single(f.Mails(email, "locked"));
        // the correct password on the web is refused while locked, and the correct PIN by phone too
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false)).StatusCode);
        var sid2 = Sid();
        await p.Start(sid2, phone); await p.Press(sid2, phone, "1");
        var locked = await p.Press(sid2, phone, "615283");
        Assert.DoesNotContain("signed in", Phones.Says(locked));
        Assert.Contains("Too many attempts", Phones.Says(locked));
    }

    [Fact]
    public async Task Web_failures_make_the_phone_hint_continue_the_counter()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var web = new Api(f);
        for (var i = 0; i < 3; i++) await web.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "1");
        Assert.Contains("Last try", Phones.Says(await p.Press(sid, phone, "111222")));
    }

    [Fact]
    public async Task Silence_and_invalid_input_repeat_twice_then_say_goodbye()
    {
        var p = new Phones(f); var sid = Sid(); var phone = PhoneApiFactory.NewNumber();
        await p.Start(sid, phone);
        var a = await p.Call(sid, phone, digits: "");  // no input
        Assert.Contains("didn't hear", Phones.Says(a)); Assert.False(Phones.HungUp(a));
        var b = await p.Call(sid, phone, digits: "7");
        Assert.False(Phones.HungUp(b));
        var c = await p.Call(sid, phone, digits: "8");
        Assert.True(Phones.HungUp(c));
        Assert.Contains("Goodbye", Phones.Says(c));
    }

    [Fact]
    public async Task Stale_or_replayed_steps_are_not_processed_and_state_expires()
    {
        var (_, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        var stale = p.NextAction(sid);
        await p.Press(sid, phone, "1");                       // seq advances
        var replay = await p.Call(sid, phone, digits: "615283", path: stale); // replayed old request with the PIN digits
        Assert.DoesNotContain("signed in", Phones.Says(replay));
        // expiry: the call state dies after CallStateMinutes
        f.Time.Advance(TimeSpan.FromMinutes(11));
        var after = await p.Call(sid, phone, digits: "615283");
        Assert.DoesNotContain("signed in", Phones.Says(after));
        Assert.Contains("Press 1 to sign in", Phones.Says(after));
    }

    [Fact]
    public async Task Phone_session_expires_after_the_configured_minutes()
    {
        var (_, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "1");
        Assert.Contains("signed in", Phones.Says(await p.Press(sid, phone, "615283")));
        f.Time.Advance(TimeSpan.FromMinutes(9)); // < call state 10 min: still the same call
        Assert.Contains("application", Phones.Says(await p.Press(sid, phone, "1")));
        f.Time.Advance(TimeSpan.FromMinutes(11));
        var expired = await p.Press(sid, phone, "1");
        Assert.DoesNotContain("application is", Phones.Says(expired));
    }

    [Fact]
    public async Task Signed_in_caller_can_text_a_reset_link_and_it_works_on_the_web()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "1"); await p.Press(sid, phone, "615283");
        var before = f.Sms.To(phone).Count;
        Assert.Contains("texted a reset link", Phones.Says(await p.Press(sid, phone, "2")));
        Assert.Equal(before + 1, f.Sms.To(phone).Count);
        var sms = f.Sms.To(phone).Last().Text;
        Assert.DoesNotContain("@", sms); // never a full email by SMS
        Assert.Single(f.Mails(email, "phone-reset-link"));
        var token = System.Text.RegularExpressions.Regex.Match(f.LastSmsLink(phone), @"/l/(\S+)").Groups[1].Value;
        var api = new Api(f);
        var red = await api.Post("/api/v1/channels/links/redeem", new { token }, false);
        Assert.Equal(HttpStatusCode.OK, red.StatusCode);
        var body = await api.JsonBody(red);
        var reset = await api.Post("/api/v1/auth/reset-password", new { email = body.GetProperty("email").GetString(), token = body.GetProperty("token").GetString(), newPassword = "N3w-Pass-Phrase-Here!" }, false);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        // single use
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/channels/links/redeem", new { token }, false)).StatusCode);
    }

    [Fact]
    public async Task Change_PIN_and_sign_out_everywhere_and_transfer()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var web = new Api(f); await web.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.OK, (await web.Get("/api/v1/account/me")).StatusCode);
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone); await p.Press(sid, phone, "1"); await p.Press(sid, phone, "615283");
        // change PIN: weak PIN rejected, then accepted
        await p.Press(sid, phone, "3");
        Assert.Contains("sequence", Phones.Says(await p.Press(sid, phone, "123456")));
        await p.Press(sid, phone, "802957");
        Assert.Contains("PIN was changed", Phones.Says(await p.Press(sid, phone, "802957")));
        Assert.Single(f.Mails(email, "phone-pin-changed"));
        // sign out of all devices
        Assert.Contains("signed out of all devices", Phones.Says(await p.Press(sid, phone, "4")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Get("/api/v1/account/me")).StatusCode);
        // transfer to support
        var dial = await p.Press(sid, phone, "5");
        Assert.Equal(PhoneApiFactory.Support, XDocument.Parse(dial).Descendants("Dial").Single().Value);
        // the old PIN no longer works; the new one does
        var sid2 = Sid(); await p.Start(sid2, phone); await p.Press(sid2, phone, "1");
        Assert.Contains("didn't match", Phones.Says(await p.Press(sid2, phone, "615283")));
        await p.Press(sid2, phone, "1");
        Assert.Contains("signed in", Phones.Says(await p.Press(sid2, phone, "802957")));
    }

    [Fact]
    public async Task Signup_by_phone_possession_code_then_web_link()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f); var sid = Sid();
        await p.Start(sid, phone);
        var code = await p.Press(sid, phone, "2");
        Assert.Contains("6 digit code", Phones.Says(code));
        Assert.Equal(phone, f.Sms.To(phone).Last().To);
        var wrong = await p.Press(sid, phone, "000000");
        Assert.Contains("didn't match", Phones.Says(wrong));
        await p.Press(sid, phone, f.LastSmsCode(phone));
        Assert.Contains("sequence", Phones.Says(await p.Press(sid, phone, "123456")));
        await p.Press(sid, phone, "739204");
        var choice = await p.Press(sid, phone, "739204");
        Assert.Contains("Press 1 to get a text", Phones.Says(choice));
        Assert.Contains("texted you a link", Phones.Says(await p.Press(sid, phone, "1")));
        var link = f.LastSmsLink(phone);
        var token = System.Text.RegularExpressions.Regex.Match(link, @"/l/(\S+)").Groups[1].Value;
        var api = new Api(f);
        var peek = await api.Post("/api/v1/channels/links/redeem", new { token }, false);
        Assert.Equal("signup", (await api.JsonBody(peek)).GetProperty("purpose").GetString());
        var email = Api.NewEmail();
        var done = await api.Post("/api/v1/channels/links/complete-signup", new { token, email, password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Accepted, done.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/channels/links/complete-signup", new { token, email = Api.NewEmail(), password = ApiFactory.Strong, acceptTerms = true }, false)).StatusCode); // single use
        var verify = await api.Post("/api/v1/auth/verify-email", new { email, code = f.LastCode(email) }, false);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        // the account now has the pre-verified number and the PIN chosen on the phone
        var sid2 = Sid(); await p.Start(sid2, phone); await p.Press(sid2, phone, "1");
        Assert.Contains("signed in", Phones.Says(await p.Press(sid2, phone, "739204")));
    }

    [Fact]
    public async Task Signup_by_phone_with_spoken_email_then_conversational_business_details()
    {
        var phone = PhoneApiFactory.NewNumber();
        var local = "voice" + Guid.NewGuid().ToString("N")[..8];
        var spoken = $"{local} at example dot com"; var email = $"{local}@example.com";
        var p = new Phones(f); var sid = Sid();
        f.Ai.Reset();
        await p.Start(sid, phone); await p.Press(sid, phone, "2");
        await p.Press(sid, phone, f.LastSmsCode(phone));
        await p.Press(sid, phone, "739204"); await p.Press(sid, phone, "739204");
        var ask = await p.Press(sid, phone, "2");
        Assert.Equal("speech dtmf", Phones.Gather(ask)!.Attribute("input")!.Value);
        var readback = await p.Call(sid, phone, speech: spoken);
        Assert.Contains("I heard", Phones.Says(readback));
        Assert.Contains($"{local[0]}***@example.com", f.Sms.To(phone).Last().Text);  // masked: no full email by SMS
        Assert.DoesNotContain(email, f.Sms.To(phone).Last().Text);
        await p.Press(sid, phone, "1");                                   // yes, that's right
        Assert.Contains("Terms of Service", Phones.Says(p.LastXml));
        await p.Press(sid, phone, "1");                                   // accepts the Terms
        // the account exists but is not usable until the email is verified
        var api = new Api(f);
        var early = await api.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);
        await p.Press(sid, phone, f.LastCode(email));
        Assert.Contains("email is verified", Phones.Says(p.LastXml));
        // business details through the AI (fake) and the server validators
        var r = await p.Call(sid, phone, speech: "my company is Acme Foods and my name is Dana Seller");
        Assert.Contains("street address", Phones.Says(r));
        await p.Call(sid, phone, speech: "12 Main Street");
        await p.Call(sid, phone, speech: "Brooklyn");
        await p.Call(sid, phone, digits: "11201");
        var fin = await p.Call(sid, phone, speech: "United States");
        Assert.Contains("texted you a link", Phones.Says(fin));
        Assert.True(Phones.HungUp(fin));
        f.WithDb(db =>
        {
            var s = db.Sellers.Single(x => db.Users.Any(u => u.Id == x.UserId && u.Email == email));
            Assert.Equal("Acme Foods", s.VendorName); Assert.Equal("Dana Seller", s.ContactName); Assert.Equal("12 Main Street", s.AddressLine1);
            Assert.Equal("Brooklyn", s.City); Assert.Equal("11201", s.PostalCode); Assert.Equal("US", s.Country);
            Assert.Equal("2026-01", s.TermsVersion); Assert.NotNull(s.TermsAcceptedAt);   // explicit "yes" + wording version stored
            Assert.Null(s.SellerAgreementAcceptedAt);                                     // the Seller Agreement is accepted on the web
            return 0;
        });
        Assert.All(f.Ai.Received, r2 => { Assert.DoesNotContain("@", r2.UserText); Assert.DoesNotContain("11201", r2.UserText); });
        Assert.Contains("/l/", f.LastSmsLink(phone)); // a link to set the password and finish on the web
    }

    [Fact]
    public async Task Signup_for_a_number_that_already_has_an_account_looks_identical_and_never_succeeds()
    {
        var (_, existing) = await f.SellerWithPhone();
        var fresh = PhoneApiFactory.NewNumber();
        var outputs = new List<string>();
        foreach (var number in new[] { existing, fresh })
        {
            var p = new Phones(f); var sid = Sid();
            var before = f.Sms.To(number).Count;
            await p.Start(sid, number);
            var s1 = Phones.Says(await p.Press(sid, number, "2"));
            var s2 = Phones.Says(await p.Press(sid, number, "000000"));
            outputs.Add(s1 + "|" + s2);
            if (number == existing) Assert.Equal(before, f.Sms.To(number).Count); // nothing sent to the existing account's number
        }
        Assert.Equal(outputs[0], outputs[1]);
    }

    [Fact]
    public void Voice_xml_is_escaped_and_well_formed()
    {
        var vr = new VoiceResponse("Polly.Joanna", "en-US").Say("A & B <script>\"x\"</script>").Gather("/a?x=1&seq=2", ["Say <this> & that"], true, 1);
        var doc = XDocument.Parse(vr.ToString());
        Assert.Equal("A & B <script>\"x\"</script>", doc.Descendants("Say").First().Value);
        Assert.Equal("/a?x=1&seq=2", doc.Descendants("Gather").Single().Attribute("action")!.Value);
        Assert.Contains("&amp;", vr.ToString());
        Assert.Equal("1, 2, 3", VoiceResponse.Digits("123"));
    }

    [Fact]
    public async Task Rate_limit_per_number_returns_429()
    {
        await using var f2 = new PhoneApiFactory(); f2.Extra["Phone:NumberPerMinute"] = "3";
        var p = new Phones(f2); var phone = PhoneApiFactory.NewNumber();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await p.Raw("/api/v1/channels/voice/incoming", new() { ["CallSid"] = Sid(), ["From"] = phone })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, codes[0]);
    }
}
