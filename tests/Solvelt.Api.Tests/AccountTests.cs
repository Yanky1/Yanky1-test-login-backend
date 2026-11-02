using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

namespace Solvelt.Api.Tests;

public class AccountTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static string Token(string link) =>
        Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Regex.Match(link, @"token=([^&\s]+)").Groups[1].Value));

    [Fact]
    public async Task Forgot_password_is_generic_for_known_and_unknown_emails()
    {
        var known = Api.NewEmail();
        await new Api(f).SignupVerified(known);
        var a = await new Api(f).Post("/api/v1/auth/forgot-password", new { email = known }, false);
        var b = await new Api(f).Post("/api/v1/auth/forgot-password", new { email = Api.NewEmail() }, false);
        var c = await new Api(f).Post("/api/v1/auth/forgot-password", new { email = "garbage" }, false);
        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, c.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        Assert.Single(f.Mails(known, "reset-password"));
    }

    [Fact]
    public async Task Reset_password_is_single_use_unlocks_signs_out_everywhere_and_notifies()
    {
        var email = Api.NewEmail();
        var session = new Api(f);
        await session.SignupVerified(email);
        // lock the account first
        var c = new Api(f);
        for (var i = 0; i < 5; i++) await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, captchaToken = "x" }, false)).StatusCode);
        await new Api(f).Post("/api/v1/auth/forgot-password", new { email }, false);
        var link = f.Mails(email, "reset-password").Last().Text;
        var tokenB64 = Regex.Match(link, @"token=([^&\s]+)").Groups[1].Value;
        const string newPw = "N3w-Marble-Fox-Pass!";
        // weak password rejected, token still usable
        var weak = await new Api(f).Post("/api/v1/auth/reset-password", new { email, token = tokenB64, newPassword = "weak" }, false);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var ok = await new Api(f).Post("/api/v1/auth/reset-password", new { email, token = tokenB64, newPassword = newPw }, false);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Single(f.Mails(email, "password-changed"));
        // single use
        var again = await new Api(f).Post("/api/v1/auth/reset-password", new { email, token = tokenB64, newPassword = "An0ther-Marble-Fox!" }, false);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        // unlocked: new password works immediately without waiting for lock expiry
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = newPw }, false)).StatusCode);
        // old session signed out everywhere
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Get("/api/v1/account/me")).StatusCode);
    }

    [Fact]
    public async Task Reset_token_for_other_email_or_garbage_fails_generically()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        var r = await new Api(f).Post("/api/v1/auth/reset-password", new { email, token = "bm90LWEtdG9rZW4", newPassword = ApiFactory.Strong }, false);
        var r2 = await new Api(f).Post("/api/v1/auth/reset-password", new { email = Api.NewEmail(), token = "bm90LWEtdG9rZW4", newPassword = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        static string Strip(string j) => Regex.Replace(j, "\"traceId\":\"[^\"]+\"", "");
        Assert.Equal(Strip(await r.Content.ReadAsStringAsync()), Strip(await r2.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Change_password_requires_current_enforces_rules_signs_out_others_and_notifies()
    {
        var email = Api.NewEmail();
        var a = new Api(f); await a.SignupVerified(email);
        var other = new Api(f); await other.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/change-password", new { currentPassword = "wrong", newPassword = "N3w-Marble-Fox-Pass!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/change-password", new { currentPassword = ApiFactory.Strong, newPassword = "weak" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Post("/api/v1/account/change-password", new { currentPassword = ApiFactory.Strong, newPassword = "N3w-Marble-Fox-Pass!" })).StatusCode);
        Assert.Single(f.Mails(email, "password-changed"));
        Assert.Equal(HttpStatusCode.OK, (await a.Get("/api/v1/account/me")).StatusCode);          // current session survives
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Get("/api/v1/account/me")).StatusCode); // others do not
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = "N3w-Marble-Fox-Pass!" }, false)).StatusCode);
    }

    [Fact]
    public async Task Change_email_verifies_new_address_and_notifies_old_one()
    {
        var oldEmail = Api.NewEmail(); var newEmail = Api.NewEmail();
        var a = new Api(f); await a.SignupVerified(oldEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/email/change", new { newEmail, currentPassword = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await a.Post("/api/v1/account/email/change", new { newEmail, currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Single(f.Mails(newEmail, "change-email-code"));
        Assert.Empty(f.Mails(oldEmail, "email-changed"));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/email/confirm", new { code = "123456" })).StatusCode);
        var ok = await a.Post("/api/v1/account/email/confirm", new { code = f.LastCode(newEmail, "change-email-code") });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Single(f.Mails(oldEmail, "email-changed"));
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email = newEmail, password = ApiFactory.Strong }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email = oldEmail, password = ApiFactory.Strong }, false)).StatusCode);
    }

    [Fact]
    public async Task Change_email_to_taken_address_looks_identical_but_sends_nothing()
    {
        var taken = Api.NewEmail();
        await new Api(f).SignupVerified(taken);
        var a = new Api(f); await a.SignupVerified();
        var res = await a.Post("/api/v1/account/email/change", new { newEmail = taken, currentPassword = ApiFactory.Strong });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Empty(f.Mails(taken, "change-email-code"));
    }

    private static string Totp(string base32Key, DateTimeOffset now)
    {
        const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.ToUpperInvariant().TrimEnd('=').Select(c => Convert.ToString(alpha.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(now.ToUnixTimeSeconds() / 30); if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key, counter);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task Totp_two_factor_setup_login_recovery_codes_and_disable()
    {
        var email = Api.NewEmail();
        var a = new Api(f); await a.SignupVerified(email);
        var setup = await a.JsonBody(await a.Post("/api/v1/account/2fa/setup"));
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/2fa/enable", new { code = "000000" })).StatusCode);
        var enable = await a.JsonBody(await a.Post("/api/v1/account/2fa/enable", new { code = Totp(key, f.Time.GetUtcNow()) }));
        var recovery = enable.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(10, recovery.Length);
        // password alone is no longer enough, and says 2FA is needed
        var step1 = await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, step1.StatusCode);
        Assert.True((await a.JsonBody(step1)).GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, totpCode = "000000" }, false)).StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, totpCode = Totp(key, f.Time.GetUtcNow()) }, false)).StatusCode);
        // recovery code works once
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, recoveryCode = recovery[0] }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, recoveryCode = recovery[0] }, false)).StatusCode);
        // disable requires password + code
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/account/2fa/disable", new { code = "000000", currentPassword = ApiFactory.Strong })).StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.NoContent, (await a.Post("/api/v1/account/2fa/disable", new { code = Totp(key, f.Time.GetUtcNow()), currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false)).StatusCode);
    }

    [Fact]
    public async Task Passkey_option_endpoints_return_webauthn_json_for_rp_localhost()
    {
        var a = new Api(f); await a.SignupVerified();
        var req = await a.JsonBody(await new Api(f).Post("/api/v1/auth/passkey/options", new { }, false));
        Assert.Equal("localhost", req.GetProperty("rpId").GetString());
        Assert.True(req.TryGetProperty("challenge", out _));
        var create = await a.JsonBody(await a.Post("/api/v1/account/passkeys/options"));
        Assert.Equal("localhost", create.GetProperty("rp").GetProperty("id").GetString());
        Assert.True(create.GetProperty("authenticatorSelection").TryGetProperty("residentKey", out _));
        var list = await a.JsonBody(await a.Get("/api/v1/account/passkeys"));
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/passkey/signin", new { credential = new { id = "x" } }, false)).StatusCode);
    }

    [Fact]
    public async Task Cannot_remove_last_sign_in_method()
    {
        // Google-only account: unlinking the only login must be refused
        StubOidcHandler.Sub = "g-last-" + Guid.NewGuid().ToString("N"); StubOidcHandler.Email = Api.NewEmail(); StubOidcHandler.Verified = "true";
        var c = new Api(f);
        var res = await c.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var cb = await c.Http.GetAsync(res.Headers.Location!);
        var code = Regex.Match(cb.Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value;
        var ex = await c.Post("/api/v1/auth/external/exchange", new { code = Uri.UnescapeDataString(code) }, false);
        typeof(Api).GetProperty("Session")!.SetValue(c, await ex.Content.ReadFromJsonAsync<Login>(Api.Json));
        var del = await c.Delete("/api/v1/account/logins/Google");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }
}
