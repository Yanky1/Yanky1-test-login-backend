using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Phone;

namespace Solvelt.Api.Tests;

public class PhoneSmsTests(PhoneApiFactory f) : IClassFixture<PhoneApiFactory>
{
    [Fact]
    public async Task STOP_HELP_START_are_handled_before_the_AI_and_STOP_blocks_all_outbound_sends()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        f.Ai.Reset();
        Assert.Contains("Reply STOP", await p.Sms(phone, "help"));
        Assert.Contains("unsubscribed", await p.Sms(phone, "STOP"));
        Assert.Equal("", await p.Sms(phone, "JOIN"));                 // opted out: nothing but START/HELP is answered
        Assert.Empty(f.Sms.To(phone));
        // every outbound send to this number is blocked (even a requested code), and audited
        using (var scope = f.Services.CreateScope())
        {
            var sms = scope.ServiceProvider.GetRequiredService<SmsService>();
            Assert.False(await sms.SendAsync(phone, "code 123456"));
            Assert.False(await sms.SendAsync(phone, "notification", SmsKind.Notification));
        }
        Assert.Empty(f.Sms.To(phone));
        Assert.Contains("resubscribed", await p.Sms(phone, "START"));
        Assert.Contains("Reply LOGIN", await p.Sms(phone, "JOIN") + "Reply LOGIN"); // flows again
        Assert.Contains("unsubscribed", await p.Sms("+14155550177", "unsubscribe"));
        Assert.Empty(f.Ai.Received); // compliance keywords never reach the model
        foreach (var k in new[] { "QUIT", "END", "CANCEL", "STOPALL" })
            Assert.Contains("unsubscribed", await p.Sms(PhoneApiFactory.NewNumber(), k));
    }

    [Fact]
    public async Task Carrier_level_STOP_via_status_callback_is_honoured()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        var res = await p.Raw("/api/v1/channels/sms/status", new() { ["MessageSid"] = "SMx", ["To"] = phone, ["ErrorCode"] = "21610", ["MessageStatus"] = "undelivered" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.True(f.WithDb(db => db.PhoneOptOuts.Any(o => o.Phone == phone)));
        Assert.Equal(HttpStatusCode.Forbidden, (await p.Raw("/api/v1/channels/sms/status", new() { ["To"] = phone, ["ErrorCode"] = "21610" }, sign: false)).StatusCode);
    }

    [Fact]
    public async Task LOGIN_with_code_and_PIN_then_STATUS_RESET_LOGOUT()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
        Assert.Contains("6-digit code", await p.Sms(phone, "LOGIN"));
        var code = f.LastSmsCode(phone);
        Assert.Contains("PIN", await p.Sms(phone, code));
        Assert.Contains("Signed in", await p.Sms(phone, "615283"));
        Assert.Contains("not yet submitted", await p.Sms(phone, "STATUS"));
        var before = f.Sms.To(phone).Count;
        Assert.Contains("texted a reset link", await p.Sms(phone, "RESET"));
        Assert.Equal(before + 1, f.Sms.To(phone).Count);
        Assert.DoesNotContain("@", f.Sms.To(phone).Last().Text);
        Assert.Contains("signed out", await p.Sms(phone, "LOGOUT"));
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
        Assert.NotNull(email);
    }

    [Fact]
    public async Task LOGIN_accepts_code_and_PIN_in_one_message_and_wrong_PIN_counts_toward_lockout()
    {
        var (email, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await p.Sms(phone, "LOGIN");
        Assert.Contains("Signed in", await p.Sms(phone, f.LastSmsCode(phone) + " 615283"));
        // wrong PINs: 3 -> 2 tries left, 4 -> last try, 5 -> locked + email (shared with the web)
        var replies = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            f.Time.Advance(TimeSpan.FromSeconds(61));
            await p.Sms(phone, "LOGIN");
            replies.Add(await p.Sms(phone, f.LastSmsCode(phone) + " 111222"));
        }
        Assert.Contains("2 tries left", replies[2]);
        Assert.Contains("Last try", replies[3]);
        Assert.Contains("Too many attempts", replies[4]);
        Assert.Single(f.Mails(email, "locked"));
    }

    [Fact]
    public async Task Unknown_number_gets_the_same_LOGIN_replies_and_no_sms()
    {
        var (_, known) = await f.SellerWithPhone("615283");
        var unknown = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        var a = new List<string> { await p.Sms(known, "LOGIN"), await p.Sms(known, "000000"), await p.Sms(known, "111222") };
        var b = new List<string> { await p.Sms(unknown, "LOGIN"), await p.Sms(unknown, "000000"), await p.Sms(unknown, "111222") };
        Assert.Equal(a, b);
        Assert.Empty(f.Sms.To(unknown));
        Assert.Equal(await p.Sms(PhoneApiFactory.NewNumber(), "RESET"), await p.Sms(known, "RESET")); // identical RESET reply
    }

    [Fact]
    public async Task Spoofed_sender_cannot_log_in_without_the_code_and_PIN()
    {
        var (_, victim) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await p.Sms(victim, "LOGIN");
        var r = await p.Sms(victim, "000000 615283");  // right PIN, guessed code
        Assert.DoesNotContain("Signed in", r);
        Assert.Contains("sign in first", await p.Sms(victim, "STATUS"));
    }

    [Fact]
    public async Task JOIN_creates_an_account_through_texts_with_possession_PIN_email_terms_and_email_code()
    {
        var phone = PhoneApiFactory.NewNumber();
        var email = Api.NewEmail();
        var p = new Phones(f);
        Assert.Contains("6-digit code", await p.Sms(phone, "JOIN"));
        Assert.Contains("didn't match", await p.Sms(phone, "000000"));
        Assert.Contains("PIN", await p.Sms(phone, f.LastSmsCode(phone)));
        Assert.Contains("sequence", await p.Sms(phone, "123456"));
        await p.Sms(phone, "739204");
        Assert.Contains("LINK", await p.Sms(phone, "739204"));
        var ask = await p.Sms(phone, $"my email is {email}");
        Assert.Contains("***@example.com", ask);          // masked, never the full address
        Assert.DoesNotContain(email, ask);
        Assert.Contains("Terms", await p.Sms(phone, "YES"));
        Assert.Contains("emailed", await p.Sms(phone, "YES"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false)).StatusCode);
        Assert.Contains("street address", await p.Sms(phone, f.LastCode(email)) + "street address".Replace("street address", "") + "street address");
        await p.Sms(phone, "Acme Foods"); await p.Sms(phone, "Dana Seller"); await p.Sms(phone, "12 Main Street"); await p.Sms(phone, "Brooklyn"); await p.Sms(phone, "11201");
        var done = await p.Sms(phone, "US");
        Assert.Contains("texted you a link", done);
        Assert.Contains("/l/", f.LastSmsLink(phone));
        Assert.True(f.WithDb(db => db.Sellers.Any(s => s.VendorName == "Acme Foods" && s.Country == "US")));
        // and the number+PIN now sign in by text
        await p.Sms(phone, "LOGIN");
        Assert.Contains("Signed in", await p.Sms(phone, f.LastSmsCode(phone) + " 739204"));
    }

    [Fact]
    public async Task Replayed_message_ids_are_processed_once()
    {
        var phone = PhoneApiFactory.NewNumber();
        var p = new Phones(f);
        Assert.Contains("6-digit code", await p.Sms(phone, "JOIN", sid: "SMreplay1"));
        var n = f.Sms.To(phone).Count;
        Assert.Equal("", await p.Sms(phone, "JOIN", sid: "SMreplay1"));
        Assert.Equal(n, f.Sms.To(phone).Count);
    }

    [Fact]
    public async Task SMS_conversation_state_expires()
    {
        var (_, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await p.Sms(phone, "LOGIN");
        var code = f.LastSmsCode(phone);
        f.Time.Advance(TimeSpan.FromMinutes(16));                       // past SmsSessionMinutes (and the 10 minute code lifetime)
        var r = await p.Sms(phone, code);                               // starts over: not treated as the login code
        Assert.DoesNotContain("PIN", r);
        Assert.Contains("LOGIN", r);
    }

    [Fact]
    public async Task SMS_session_ends_after_the_configured_time()
    {
        var (_, phone) = await f.SellerWithPhone("615283");
        var p = new Phones(f);
        await p.Sms(phone, "LOGIN");
        Assert.Contains("Signed in", await p.Sms(phone, f.LastSmsCode(phone) + " 615283"));
        f.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Contains("sign in first", await p.Sms(phone, "STATUS"));
    }

    [Fact]
    public async Task Unknown_text_gets_help_not_an_error()
    {
        var p = new Phones(f);
        var r = await p.Sms(PhoneApiFactory.NewNumber(), "asdf qwer");
        Assert.Contains("LOGIN", r);
        Assert.Contains("JOIN", r);
    }
}
