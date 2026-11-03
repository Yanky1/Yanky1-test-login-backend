using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Solvelt.Api.Tests;

[Collection("stub-oidc")]
public class ExternalTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static void Stub(string sub, string email, string verified = "true")
    { StubOidcHandler.Sub = sub; StubOidcHandler.Email = email; StubOidcHandler.Verified = verified; }

    private async Task<(HttpResponseMessage cb, Api api)> Flow(string query = "terms=1")
    {
        var api = new Api(f);
        var start = await api.Http.GetAsync("/api/v1/auth/external/google/start?" + query);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var cb = await api.Http.GetAsync(start.Headers.Location!);
        return (cb, api);
    }

    private static string? Code(HttpResponseMessage cb) =>
        cb.Headers.Location is { } l && Regex.Match(l.ToString(), @"[?&]code=([^&]+)") is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : null;

    [Fact]
    public async Task Google_signup_creates_verified_account_and_skips_email_verification()
    {
        var email = Api.NewEmail(); Stub("g-" + Guid.NewGuid().ToString("N"), email);
        var (cb, api) = await Flow();
        Assert.Contains("/auth/callback?code=", cb.Headers.Location!.ToString());
        var ex = await api.Post("/api/v1/auth/external/exchange", new { code = Code(cb) }, false);
        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
        var login = await ex.Content.ReadFromJsonAsync<Login>(Api.Json);
        Assert.Equal("Active", login!.AccountStatus);
        Assert.Equal(email, login.Email);
        Assert.Empty(f.Mails(email, "verify-code"));
        var u = f.WithDb(db => db.Users.First(x => x.Email == email));
        Assert.True(u.EmailConfirmed); Assert.False(u.HasPassword);
        Assert.Equal(3, f.WithDb(db => db.Sellers.First(s => s.UserId == u.Id).DraftStep)); // jumps past Verify
        // exchange code is single use
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/external/exchange", new { code = Code(cb) }, false)).StatusCode);
    }

    [Fact]
    public async Task Google_signup_without_terms_is_refused()
    {
        Stub("g-" + Guid.NewGuid().ToString("N"), Api.NewEmail());
        var (cb, _) = await Flow("terms=0");
        Assert.Contains("error=terms_required", cb.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Google_links_to_existing_verified_account_only_when_provider_email_is_verified()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        Stub("g-" + Guid.NewGuid().ToString("N"), email, "false");
        var (bad, _) = await Flow();
        Assert.Contains("error=email_unverified", bad.Headers.Location!.ToString());
        Assert.DoesNotContain(f.WithDb(db => db.UserLogins.ToList()), l => l.ProviderKey == StubOidcHandler.Sub);
        Stub(StubOidcHandler.Sub, email, "true");
        var (ok, api) = await Flow();
        Assert.NotNull(Code(ok));
        Assert.Single(f.WithDb(db => db.UserLogins.ToList()), l => l.ProviderKey == StubOidcHandler.Sub);
        var ex = await api.Post("/api/v1/auth/external/exchange", new { code = Code(ok) }, false);
        Assert.Equal(email, (await ex.Content.ReadFromJsonAsync<Login>(Api.Json))!.Email);
    }

    [Fact]
    public async Task Linking_to_unverified_local_account_removes_squatter_password_and_sessions()
    {
        var email = Api.NewEmail();
        var squatter = new Api(f);
        var signup = await squatter.Post("/api/v1/auth/signup", new { email, password = ApiFactory.Strong, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Accepted, signup.StatusCode);
        Stub("g-" + Guid.NewGuid().ToString("N"), email);
        var (cb, _) = await Flow();
        Assert.NotNull(Code(cb));
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/login", new { email, password = ApiFactory.Strong }, false)).StatusCode);
    }

    [Fact]
    public async Task Repeat_google_login_uses_existing_link_and_unknown_provider_is_404()
    {
        var email = Api.NewEmail(); var sub = "g-" + Guid.NewGuid().ToString("N"); Stub(sub, email);
        var (first, _) = await Flow();
        Assert.NotNull(Code(first));
        Stub(sub, "changed-" + email); // provider email changed; the (provider, sub) link still wins
        var (second, api) = await Flow("terms=0");
        Assert.NotNull(Code(second));
        Assert.Equal(email, (await (await api.Post("/api/v1/auth/external/exchange", new { code = Code(second) }, false)).Content.ReadFromJsonAsync<Login>(Api.Json))!.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await new Api(f).Http.GetAsync("/api/v1/auth/external/apple/start")).StatusCode); // flag off
        Assert.Equal(HttpStatusCode.NotFound, (await new Api(f).Http.GetAsync("/api/v1/auth/external/evil/start")).StatusCode);
    }

    [Fact]
    public async Task Return_url_is_sanitised_against_open_redirects()
    {
        Stub("g-" + Guid.NewGuid().ToString("N"), Api.NewEmail());
        var (evil, _) = await Flow("terms=1&returnUrl=" + Uri.EscapeDataString("https://evil.example/x"));
        Assert.DoesNotContain("next=", evil.Headers.Location!.ToString());
        Stub("g-" + Guid.NewGuid().ToString("N"), Api.NewEmail());
        var (good, _) = await Flow("terms=1&returnUrl=" + Uri.EscapeDataString("/settings/security"));
        Assert.Contains("next=%2Fsettings%2Fsecurity", good.Headers.Location!.ToString());
    }
}

[CollectionDefinition("stub-oidc", DisableParallelization = true)]
public class StubOidcCollection;
