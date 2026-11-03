using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using System.Text;

namespace Solvelt.Api.Tests;

/// <summary>Regression tests for the security review (items 1-6).</summary>
[Collection("stub-oidc")]
public class HardeningTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static readonly byte[] CredId = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Simulates the attacker's planted passkey (the attestation step itself needs a real authenticator; see the E2E test).</summary>
    private async Task PlantPasskey(string email)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var key = new UserPasskeyInfo(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, 0, [], false, false, true, [], []) { Name = "attacker" };
        await users.AddOrUpdatePasskeyAsync(user, key);
        await users.SetTwoFactorEnabledAsync(user, true);
        await users.ResetAuthenticatorKeyAsync(user);
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 5);
        await users.AddLoginAsync(user, new UserLoginInfo("Google", "attacker-sub-" + Guid.NewGuid().ToString("N"), "Google"));
    }

    private async Task<(int Passkeys, bool TwoFactor, int Logins, bool HasKey, int Sessions)> Inspect(string email)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = (await users.FindByEmailAsync(email))!;
        return ((await users.GetPasskeysAsync(u)).Count, u.TwoFactorEnabled, (await users.GetLoginsAsync(u)).Count,
            await users.GetAuthenticatorKeyAsync(u) is not null, db.Sessions.Count(s => s.UserId == u.Id && s.RevokedAt == null));
    }

    // ---------------------------------------------------------------- 1. pre-hijacking
    [Fact]
    public async Task Item1_unverified_session_is_forbidden_from_passkeys_2fa_and_email_change()
    {
        var api = new Api(f);
        await api.UnverifiedSession();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/account/passkeys/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get("/api/v1/account/passkeys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/account/2fa/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/account/2fa/enable", new { code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/account/email/change", new { newEmail = Api.NewEmail(), currentPassword = ApiFactory.Strong })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put("/api/v1/seller/application/business", Api.ValidBusiness)).StatusCode);
    }

    [Fact]
    public async Task Item1_reset_password_claim_wipes_attackers_planted_credentials()
    {
        var victim = Api.NewEmail();
        var attacker = new Api(f);
        Assert.Equal(HttpStatusCode.Accepted, (await attacker.Post("/api/v1/auth/signup", new { email = victim, password = "Attacker-Pass-1234!", acceptTerms = true }, false)).StatusCode);
        await PlantPasskey(victim);
        Assert.Equal(1, (await Inspect(victim)).Passkeys);
        // the real owner resets the password through their mailbox
        await new Api(f).Post("/api/v1/auth/forgot-password", new { email = victim }, false);
        var tokenB64 = Regex.Match(f.Mails(victim, "reset-password").Last().Text, @"token=([^&\s]+)").Groups[1].Value;
        var ok = await new Api(f).Post("/api/v1/auth/reset-password", new { email = victim, token = tokenB64, newPassword = "Victim-Pass-5678!x" }, false);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var after = await Inspect(victim);
        Assert.Equal(0, after.Passkeys); Assert.False(after.TwoFactor); Assert.Equal(0, after.Logins); Assert.False(after.HasKey); Assert.Equal(0, after.Sessions);
        // attacker's old password is dead, the victim's works
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email = victim, password = "Attacker-Pass-1234!" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email = victim, password = "Victim-Pass-5678!x" }, false)).StatusCode);
    }

    [Fact]
    public async Task Item1_google_claim_wipes_attackers_planted_credentials()
    {
        var victim = Api.NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await new Api(f).Post("/api/v1/auth/signup", new { email = victim, password = "Attacker-Pass-1234!", acceptTerms = true }, false)).StatusCode);
        await PlantPasskey(victim);
        StubOidcHandler.Sub = "g-" + Guid.NewGuid().ToString("N"); StubOidcHandler.Email = victim; StubOidcHandler.Verified = "true";
        var c = new Api(f);
        var start = await c.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var cb = await c.Http.GetAsync(start.Headers.Location!);
        Assert.Contains("code=", cb.Headers.Location!.ToString());
        var after = await Inspect(victim);
        Assert.Equal(0, after.Passkeys); Assert.False(after.TwoFactor); Assert.False(after.HasKey);
        Assert.Equal(1, after.Logins); // only the real owner's Google login remains
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email = victim, password = "Attacker-Pass-1234!" }, false)).StatusCode);
    }

    [Fact]
    public async Task Item1_unverified_accounts_cannot_sign_in_with_a_planted_passkey()
    {
        // passkey sign-in requires a verified email even when a credential is attached (guard in the sign-in endpoint)
        var email = Api.NewEmail();
        await new Api(f).Post("/api/v1/auth/signup", new { email, password = ApiFactory.Strong, acceptTerms = true }, false);
        await PlantPasskey(email);
        var bad = await new Api(f).Post("/api/v1/auth/passkey/signin", new { credential = new { id = "AQIDBA", rawId = "AQIDBA", type = "public-key", response = new { } } }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    // ---------------------------------------------------------------- 2. MFA parity
    private async Task<(Api api, string email)> GoogleUser(bool twoFactor)
    {
        var email = Api.NewEmail();
        StubOidcHandler.Sub = "g-" + Guid.NewGuid().ToString("N"); StubOidcHandler.Email = email; StubOidcHandler.Verified = "true";
        var c = new Api(f);
        var start = await c.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var cb = await c.Http.GetAsync(start.Headers.Location!);
        var code = Uri.UnescapeDataString(Regex.Match(cb.Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value);
        if (twoFactor)
        {
            var first = await c.Post("/api/v1/auth/external/exchange", new { code }, false);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            typeof(Api).GetProperty("Session")!.SetValue(c, await first.Content.ReadFromJsonAsync<Login>(Api.Json));
            var setup = await c.JsonBody(await c.Post("/api/v1/account/2fa/setup"));
            var key = setup.GetProperty("sharedKey").GetString()!;
            var en = await c.Post("/api/v1/account/2fa/enable", new { code = Totp(key, f.Time.GetUtcNow()) });
            Assert.Equal(HttpStatusCode.OK, en.StatusCode);
            c.GetType().GetProperty("Session")!.SetValue(c, null);
            TotpKeys[email] = key;
        }
        return (c, email);
    }
    private static readonly Dictionary<string, string> TotpKeys = new();

    private async Task<string> NewExchangeCode(string email, string sub)
    {
        StubOidcHandler.Sub = sub; StubOidcHandler.Email = email; StubOidcHandler.Verified = "true";
        var c = new Api(f);
        var start = await c.Http.GetAsync("/api/v1/auth/external/google/start");
        var cb = await c.Http.GetAsync(start.Headers.Location!);
        return Uri.UnescapeDataString(Regex.Match(cb.Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value);
    }

    [Fact]
    public async Task Item2_external_login_enforces_totp_when_enabled()
    {
        var sub = "g-" + Guid.NewGuid().ToString("N");
        var email = Api.NewEmail();
        var (_, _) = (0, 0);
        StubOidcHandler.Sub = sub; StubOidcHandler.Email = email; StubOidcHandler.Verified = "true";
        var seed = new Api(f);
        var s1 = await seed.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var cb1 = await seed.Http.GetAsync(s1.Headers.Location!);
        var code1 = Uri.UnescapeDataString(Regex.Match(cb1.Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value);
        var first = await seed.Post("/api/v1/auth/external/exchange", new { code = code1 }, false);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // no 2FA yet
        typeof(Api).GetProperty("Session")!.SetValue(seed, await first.Content.ReadFromJsonAsync<Login>(Api.Json));
        var key = (await seed.JsonBody(await seed.Post("/api/v1/account/2fa/setup"))).GetProperty("sharedKey").GetString()!;
        var codes = await seed.JsonBody(await seed.Post("/api/v1/account/2fa/enable", new { code = Totp(key, f.Time.GetUtcNow()) }));
        var recovery = codes.GetProperty("recoveryCodes")[0].GetString()!;

        var code2 = await NewExchangeCode(email, sub);
        var anon = new Api(f);
        var noCode = await anon.Post("/api/v1/auth/external/exchange", new { code = code2 }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.True((await anon.JsonBody(noCode)).GetProperty("twoFactorRequired").GetBoolean());
        Assert.False(noCode.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/external/exchange", new { code = code2, totpCode = "000000" }, false)).StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(31)); // next TOTP step
        var ok = await anon.Post("/api/v1/auth/external/exchange", new { code = code2, totpCode = Totp(key, f.Time.GetUtcNow()) }, false);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // the exchange code is single use
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/external/exchange", new { code = code2, totpCode = Totp(key, f.Time.GetUtcNow()) }, false)).StatusCode);
        // recovery codes work too
        var code3 = await NewExchangeCode(email, sub);
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/external/exchange", new { code = code3, recoveryCode = recovery }, false)).StatusCode);
    }

    [Fact]
    public async Task Item2_external_2fa_guessing_is_throttled()
    {
        var sub = "g-" + Guid.NewGuid().ToString("N"); var email = Api.NewEmail();
        StubOidcHandler.Sub = sub; StubOidcHandler.Email = email; StubOidcHandler.Verified = "true";
        var seed = new Api(f);
        var s1 = await seed.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var code1 = Uri.UnescapeDataString(Regex.Match((await seed.Http.GetAsync(s1.Headers.Location!)).Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value);
        typeof(Api).GetProperty("Session")!.SetValue(seed, await (await seed.Post("/api/v1/auth/external/exchange", new { code = code1 }, false)).Content.ReadFromJsonAsync<Login>(Api.Json));
        var key = (await seed.JsonBody(await seed.Post("/api/v1/account/2fa/setup"))).GetProperty("sharedKey").GetString()!;
        await seed.Post("/api/v1/account/2fa/enable", new { code = Totp(key, f.Time.GetUtcNow()) });
        var code2 = await NewExchangeCode(email, sub);
        for (var i = 0; i < 6; i++) await new Api(f).Post("/api/v1/auth/external/exchange", new { code = code2, totpCode = "000000" }, false);
        f.Time.Advance(TimeSpan.FromSeconds(31));
        // locked: even the right code is refused now
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/external/exchange", new { code = code2, totpCode = Totp(key, f.Time.GetUtcNow()) }, false)).StatusCode);
    }

    [Fact]
    public async Task Item2_passkey_options_require_user_verification()
    {
        var api = new Api(f); await api.SignupVerified();
        var request = await api.JsonBody(await new Api(f).Post("/api/v1/auth/passkey/options", new { }, false));
        Assert.Equal("required", request.GetProperty("userVerification").GetString());
        var create = await api.JsonBody(await api.Post("/api/v1/account/passkeys/options"));
        Assert.Equal("required", create.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        var signup = await api.JsonBody(await new Api(f).Post("/api/v1/auth/signup/passkey/options", new { email = Api.NewEmail(), acceptTerms = true }, false));
        Assert.Equal("required", signup.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
    }

    // ---------------------------------------------------------------- 3. refresh grace window (backend half)
    [Fact]
    public async Task Item3_previous_refresh_token_is_accepted_inside_the_grace_window_only()
    {
        var api = new Api(f); var email = await api.SignupVerified();
        var a = (await new Api(f).LoginAs(email, ApiFactory.Strong)).RefreshToken;
        var r1 = await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = a }, false);
        var b = (await r1.Content.ReadFromJsonAsync<Login>(Api.Json))!.RefreshToken;
        f.Time.Advance(TimeSpan.FromSeconds(10)); // a second tab still holds token `a`
        var grace = await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = a }, false);
        Assert.Equal(HttpStatusCode.OK, grace.StatusCode);
        var c = (await grace.Content.ReadFromJsonAsync<Login>(Api.Json))!.RefreshToken;
        Assert.NotEqual(b, c);
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = b }, false)).StatusCode); // session still alive
        f.Time.Advance(TimeSpan.FromSeconds(40)); // beyond 30 s: `a` is now a replay
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = a }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = c }, false)).StatusCode); // family revoked
        Assert.True(f.WithDb(db => db.AuditEvents.Any(x => x.Type == "refresh.reuse_detected")));
    }

    // ---------------------------------------------------------------- 4. email-change step-up for passwordless accounts
    [Fact]
    public async Task Item4_passwordless_email_change_requires_a_code_from_the_current_address()
    {
        var sub = "g-" + Guid.NewGuid().ToString("N"); var email = Api.NewEmail();
        StubOidcHandler.Sub = sub; StubOidcHandler.Email = email; StubOidcHandler.Verified = "true";
        var c = new Api(f);
        var start = await c.Http.GetAsync("/api/v1/auth/external/google/start?terms=1");
        var code = Uri.UnescapeDataString(Regex.Match((await c.Http.GetAsync(start.Headers.Location!)).Headers.Location!.ToString(), @"code=([^&]+)").Groups[1].Value);
        typeof(Api).GetProperty("Session")!.SetValue(c, await (await c.Post("/api/v1/auth/external/exchange", new { code }, false)).Content.ReadFromJsonAsync<Login>(Api.Json));
        var newEmail = Api.NewEmail();
        // a bare access token is not enough: it only triggers a code to the CURRENT mailbox
        var step1 = await c.Post("/api/v1/account/email/change", new { newEmail });
        Assert.Equal(HttpStatusCode.Accepted, step1.StatusCode);
        Assert.True((await c.JsonBody(step1)).GetProperty("stepUpRequired").GetBoolean());
        Assert.Single(f.Mails(email, "stepup-code"));
        Assert.Empty(f.Mails(newEmail, "change-email-code"));
        // wrong code -> nothing is sent to the new address
        var bad = await c.Post("/api/v1/account/email/change", new { newEmail, stepUpCode = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(f.Mails(newEmail, "change-email-code"));
        // right code -> the new address gets its verification code
        var good = await c.Post("/api/v1/account/email/change", new { newEmail, stepUpCode = f.LastCode(email, "stepup-code") });
        Assert.Equal(HttpStatusCode.Accepted, good.StatusCode);
        Assert.Single(f.Mails(newEmail, "change-email-code"));
        Assert.Equal(HttpStatusCode.OK, (await c.Post("/api/v1/account/email/confirm", new { code = f.LastCode(newEmail, "change-email-code") })).StatusCode);
    }

    // ---------------------------------------------------------------- 5. lockout race
    [Fact]
    public async Task Item5_parallel_wrong_passwords_never_exceed_the_failure_limit()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
            (await new Api(f).Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false)).StatusCode).ToArray();
        var codes = await Task.WhenAll(tasks);
        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        var uid = f.WithDb(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).First());
        var checkedPasswords = f.WithDb(db => db.AuditEvents.Count(a => a.Type == "login.failed" && a.UserId == uid));
        Assert.True(checkedPasswords <= 5, $"{checkedPasswords} password checks got past the lock");
        Assert.True(f.Mails(email, "locked").Count == 1);
        // and the correct password is refused while locked
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false)).StatusCode);
    }

    [Fact]
    public async Task Item5_parallel_burst_with_one_correct_password_is_still_bounded()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        var tasks = Enumerable.Range(0, 12).Select(async i =>
            (await new Api(f).Post("/api/v1/auth/login", new { email, password = i == 9 ? ApiFactory.Strong : "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false)).StatusCode).ToArray();
        var codes = await Task.WhenAll(tasks);
        var uid = f.WithDb(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).First());
        // A successful login legitimately resets the counter, so wrong guesses are bounded per counter window: <= 5 * (successes + 1).
        var failed = f.WithDb(db => db.AuditEvents.Count(a => a.Type == "login.failed" && a.UserId == uid));
        var succeeded = f.WithDb(db => db.AuditEvents.Count(a => a.Type == "login.succeeded" && a.UserId == uid));
        Assert.True(succeeded <= 1, $"{succeeded} successful logins");
        Assert.True(failed <= 5 * (succeeded + 1), $"{failed} wrong-password checks happened with {succeeded} reset(s)");
        Assert.True(codes.Count(c => c == HttpStatusCode.OK) <= 1);
    }

    // ---------------------------------------------------------------- 6. signup enumeration
    [Fact]
    public async Task Item6_signup_response_is_identical_for_new_and_existing_verified_emails()
    {
        var existing = Api.NewEmail();
        await new Api(f).SignupVerified(existing);
        var fresh = Api.NewEmail();
        var a = await new Api(f).Post("/api/v1/auth/signup", new { email = existing, password = ApiFactory.Strong, acceptTerms = true }, false);
        var b = await new Api(f).Post("/api/v1/auth/signup", new { email = fresh, password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(b.StatusCode, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(await b.Content.ReadAsStringAsync(), await a.Content.ReadAsStringAsync());
        Assert.Equal(b.Headers.Contains("Set-Cookie"), a.Headers.Contains("Set-Cookie"));
        // the existing owner is told, the new address gets a code; the existing account is untouched
        Assert.Single(f.Mails(existing, "signup-existing"));
        Assert.Empty(f.Mails(existing, "verify-code").Skip(1));
        Assert.Single(f.Mails(fresh, "verify-code"));
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email = existing, password = ApiFactory.Strong }, false)).StatusCode);
    }

    [Fact]
    public async Task Item6_passkey_signup_options_look_identical_for_existing_and_new_emails()
    {
        var existing = Api.NewEmail();
        await new Api(f).SignupVerified(existing);
        var a = await new Api(f).Post("/api/v1/auth/signup/passkey/options", new { email = existing, acceptTerms = true }, false);
        var b = await new Api(f).Post("/api/v1/auth/signup/passkey/options", new { email = Api.NewEmail(), acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(b.StatusCode, a.StatusCode);
        var ja = await a.Content.ReadFromJsonAsync<JsonElement>(); var jb = await b.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(jb.GetProperty("rp").ToString(), ja.GetProperty("rp").ToString());
        Assert.Equal(jb.GetProperty("authenticatorSelection").ToString(), ja.GetProperty("authenticatorSelection").ToString());
        Assert.Equal(existing, ja.GetProperty("user").GetProperty("name").GetString()); // email is bound into the user entity
    }

    [Fact]
    public async Task Item6_latest_signup_replaces_an_unverified_account_so_a_squatters_password_dies()
    {
        var email = Api.NewEmail();
        await new Api(f).Post("/api/v1/auth/signup", new { email, password = "Attacker-Pass-1234!", acceptTerms = true }, false);
        await new Api(f).Post("/api/v1/auth/signup", new { email, password = "Owner-Pass-5678!xx", acceptTerms = true }, false);
        var owner = new Api(f);
        var v = await owner.Post("/api/v1/auth/verify-email", new { email, code = f.LastCode(email) }, false);
        Assert.Equal(HttpStatusCode.OK, v.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = "Attacker-Pass-1234!" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new Api(f).Post("/api/v1/auth/login", new { email, password = "Owner-Pass-5678!xx" }, false)).StatusCode);
    }

    [Fact]
    public async Task Item6_verify_and_resend_do_not_reveal_whether_an_account_exists()
    {
        var real = Api.NewEmail();
        await new Api(f).Post("/api/v1/auth/signup", new { email = real, password = ApiFactory.Strong, acceptTerms = true }, false);
        var unknown = Api.NewEmail();
        string Strip(string j) => Regex.Replace(j, "\"traceId\":\"[^\"]+\"", "");
        var v1 = await new Api(f).Post("/api/v1/auth/verify-email", new { email = real, code = "000000" }, false);
        var v2 = await new Api(f).Post("/api/v1/auth/verify-email", new { email = unknown, code = "000000" }, false);
        Assert.Equal(v2.StatusCode, v1.StatusCode);
        Assert.Equal(Strip(await v2.Content.ReadAsStringAsync()), Strip(await v1.Content.ReadAsStringAsync()));
        var r1 = await new Api(f).Post("/api/v1/auth/resend-code", new { email = real }, false);
        var r2 = await new Api(f).Post("/api/v1/auth/resend-code", new { email = unknown }, false);
        Assert.Equal(await r2.Content.ReadAsStringAsync(), await r1.Content.ReadAsStringAsync());
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
}
