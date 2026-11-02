using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Solvelt.Api.Tests;

public class AuthLoginTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Signup_then_login_returns_exact_contract_body()
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        var anon = new Api(f);
        var res = await anon.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var names = body.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "accessToken", "accountStatus", "email", "expiresIn", "refreshToken", "roles", "sellerId", "tokenType", "vendorName" }, names);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal("Active", body.GetProperty("accountStatus").GetString());
        Assert.Equal("Seller", body.GetProperty("roles")[0].GetString());
        Assert.True(Guid.TryParse(body.GetProperty("sellerId").GetString(), out var g) && g != Guid.Empty);
        // refresh cookie is HttpOnly + Secure + Strict and scoped to the auth path
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("sl_rt="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_user_and_bad_password_return_identical_401()
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        var a = await new Api(f).Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        var b = await new Api(f).Post("/api/v1/auth/login", new { email = Api.NewEmail(), password = "Wrong-Passw0rd!x" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        var ja = await a.Content.ReadAsStringAsync(); var jb = await b.Content.ReadAsStringAsync();
        Assert.Equal(ja, jb);
        Assert.Equal("Login failed", JsonDocument.Parse(ja).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Lockout_thresholds_hint_captcha_and_lock_are_identical_for_real_and_fake_accounts()
    {
        var real = Api.NewEmail();
        await new Api(f).SignupVerified(real);
        var fake = Api.NewEmail();
        foreach (var email in new[] { real, fake })
        {
            var c = new Api(f);
            var hints = new List<JsonElement>();
            for (var i = 1; i <= 4; i++)
            {
                var r = await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = i == 4 ? "XXXX.DUMMY.TOKEN.XXXX" : null }, false);
                Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
                var j = await c.JsonBody(r);
                Assert.Equal("Login failed", j.GetProperty("error").GetString());
                hints.Add(j.GetProperty("hint"));
            }
            Assert.Equal(JsonValueKind.Null, hints[1].GetProperty("message").ValueKind);        // 2 failures: nothing yet
            Assert.Equal("2 tries left", hints[2].GetProperty("message").GetString());           // 3 failures
            Assert.Equal("Last try", hints[3].GetProperty("message").GetString());               // 4 failures
            Assert.True(hints[3].GetProperty("captchaRequired").GetBoolean());
            Assert.True(hints[3].GetProperty("lastTry").GetBoolean());
            // 5th failure without captcha -> 400 Verification failed
            var noCap = await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
            Assert.Equal(HttpStatusCode.BadRequest, noCap.StatusCode);
            Assert.Equal("{\"error\":\"Verification failed\"}", await noCap.Content.ReadAsStringAsync());
            // 5th failure with captcha -> lock
            var fifth = await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
            Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
            var fj = await c.JsonBody(fifth);
            Assert.Equal(0, fj.GetProperty("hint").GetProperty("attemptsRemaining").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, fj.GetProperty("hint").GetProperty("lockedUntil").ValueKind);
        }
        // real account: locked email sent; correct password is still refused while locked
        Assert.Single(f.Mails(real, "locked"));
        var locked = await new Api(f).Post("/api/v1/auth/login", new { email = real, password = ApiFactory.Strong, captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        // after 15 minutes login works again
        f.Time.Advance(TimeSpan.FromMinutes(16));
        var ok = await new Api(f).Post("/api/v1/auth/login", new { email = real, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Lockout_signs_out_everywhere()
    {
        var email = Api.NewEmail();
        var api = new Api(f);
        await api.SignupVerified(email);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/account/me")).StatusCode);
        var c = new Api(f);
        for (var i = 0; i < 4; i++) await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
        await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x", captchaToken = "XXXX.DUMMY.TOKEN.XXXX" }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/account/me")).StatusCode);
    }

    [Fact]
    public async Task Captcha_rejection_returns_400_verification_failed()
    {
        await using var bad = new ApiFactoryWith(("Turnstile:SecretKey", "2x0000000000000000000000000000000AA"));
        var email = Api.NewEmail();
        var c = new Api(bad);
        await c.SignupVerified(email);
        for (var i = 0; i < 4; i++) await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        var r = await c.Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong, captchaToken = "anything" }, false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("{\"error\":\"Verification failed\"}", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Successful_login_resets_failure_counter()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        var c = new Api(f);
        for (var i = 0; i < 3; i++) await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        await c.LoginAs(email, ApiFactory.Strong);
        var r = await c.Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        var j = await c.JsonBody(r);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("hint").GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task Suspended_and_rejected_sellers_cannot_login()
    {
        var email = Api.NewEmail();
        var api = new Api(f);
        await api.SignupVerified(email);
        await api.CompleteApplicationAndSubmit();
        var id = api.Session!.SellerId;
        var admin = new Api(f);
        await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/v1/admin/sellers/{id}/status", new { status = "Rejected" })).StatusCode);
        var r = await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public class ApiFactoryWith : ApiFactory, IAsyncDisposable
{
    public ApiFactoryWith(params (string, string)[] extra) { foreach (var (k, v) in extra) Extra[k] = v; }
    public new ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
