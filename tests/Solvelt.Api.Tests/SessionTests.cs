using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Solvelt.Api.Tests;

public class SessionTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private async Task<(Api api, string email)> Signed()
    {
        var api = new Api(f);
        var email = await api.SignupVerified();
        return (api, email);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_old_one_is_rejected_and_reuse_revokes_family()
    {
        var (api, email) = await Signed();
        var login = await api.LoginAs(email, ApiFactory.Strong);
        var first = login.RefreshToken;
        // body-based (non-browser) refresh rotates
        var anon = new Api(f);
        var r1 = await anon.Post("/api/v1/auth/refresh", new { refreshToken = first }, false);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var second = (await r1.Content.ReadFromJsonAsync<Login>(Api.Json))!.RefreshToken;
        Assert.NotEqual(first, second);
        // reuse of the first token -> 401 and the family (session) is revoked, so `second` is dead too
        var reuse = await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = first }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var after = await new Api(f).Post("/api/v1/auth/refresh", new { refreshToken = second }, false);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.True(f.WithDb(db => db.AuditEvents.Any(a => a.Type == "refresh.reuse_detected")));
    }

    [Fact]
    public async Task Refresh_via_cookie_requires_csrf_header()
    {
        var (_, email) = await Signed();
        var c = new Api(f);
        await c.LoginAs(email, ApiFactory.Strong);
        var noCsrf = await c.Refresh(csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        var ok = await c.Refresh();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Refresh_with_foreign_origin_is_rejected()
    {
        var (_, email) = await Signed();
        var c = new Api(f);
        await c.LoginAs(email, ApiFactory.Strong);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("X-CSRF-Token", c.Csrf!);
        req.Headers.Add("Origin", "https://evil.example");
        var res = await c.Send(req, false);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Idle_session_is_revoked_by_server_and_refresh_fails()
    {
        var (_, email) = await Signed();
        var c = new Api(f);
        await c.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/v1/account/me")).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(21)); // > Session:IdleTimeoutMinutes (20); access token (60m) still valid
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Get("/api/v1/account/me")).StatusCode);
        var r = await c.Refresh();
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Activity_keeps_session_alive_across_the_idle_window()
    {
        var (_, email) = await Signed();
        var c = new Api(f);
        await c.LoginAs(email, ApiFactory.Strong);
        for (var i = 0; i < 4; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(15));
            var r = await c.Refresh(); // background refresh only happens while the user is active
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Fact]
    public async Task Expired_access_token_is_rejected()
    {
        var (_, email) = await Signed();
        await using var short_ = new ApiFactoryWith(("Session:IdleTimeoutMinutes", "600"));
        var c = new Api(short_);
        await c.SignupVerified();
        short_.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Get("/api/v1/account/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_session_and_logout_all_revokes_every_session()
    {
        var (_, email) = await Signed();
        var a = new Api(f); var b = new Api(f);
        await a.LoginAs(email, ApiFactory.Strong);
        await b.LoginAs(email, ApiFactory.Strong);
        var lo = await a.Post("/api/v1/auth/logout", null, false);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        req.Headers.Add("X-CSRF-Token", a.Csrf!);
        Assert.Equal(HttpStatusCode.Forbidden == lo.StatusCode ? HttpStatusCode.NoContent : lo.StatusCode, (await a.Send(req, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Get("/api/v1/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Get("/api/v1/account/me")).StatusCode);
        var c = new Api(f);
        await c.LoginAs(email, ApiFactory.Strong);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Post("/api/v1/auth/logout-all")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Get("/api/v1/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Get("/api/v1/account/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_list_and_revoke_single_session()
    {
        var (_, email) = await Signed();
        var a = new Api(f); var b = new Api(f);
        await a.LoginAs(email, ApiFactory.Strong);
        await b.LoginAs(email, ApiFactory.Strong);
        var list = await a.JsonBody(await a.Get("/api/v1/account/sessions"));
        Assert.True(list.GetArrayLength() >= 2);
        var other = list.EnumerateArray().First(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/account/sessions/{other}")).StatusCode);
    }

    [Fact]
    public async Task Tampered_or_foreign_token_is_rejected()
    {
        var (api, _) = await Signed();
        var parts = api.Session!.AccessToken.Split('.');
        var bad = new Api(f);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", parts[0] + "." + parts[1] + ".AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.Http.SendAsync(req)).StatusCode);
        // alg=none
        var none = Convert.ToBase64String("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray()).TrimEnd('=') + "." + parts[1] + ".";
        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", none);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.Http.SendAsync(req2)).StatusCode);
    }
}
