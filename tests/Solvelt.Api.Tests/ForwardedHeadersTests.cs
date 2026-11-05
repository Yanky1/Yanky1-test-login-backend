using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;
using Solvelt.Api.Security;

namespace Solvelt.Api.Tests;

/// <summary>#90: forwarded headers on App Service. The test host has no socket, so the "peer" is set from X-Test-Peer before the app's own pipeline runs.</summary>
public class ProxyFactory : ApiFactory
{
    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddTransient<IStartupFilter, PeerFilter>();

    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var p) && IPAddress.TryParse(p, out var ip)) ctx.Connection.RemoteIpAddress = ip;
                return nxt();
            });
            next(app);
        };
    }

    public async Task<JsonNode> WhoAmI(string peer, string? xff = null, string? proto = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/test/client");
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        if (proto is not null) req.Headers.Add("X-Forwarded-Proto", proto);
        var res = await NewClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonNode>())!;
    }
}

public class ForwardedHeadersTests
{
    private static ProxyFactory With(params (string Key, string Value)[] s)
    {
        var f = new ProxyFactory();
        foreach (var (k, v) in s) f.Extra[k] = v;
        return f;
    }

    [Fact]
    public async Task A_spoofed_forwarded_for_from_an_untrusted_peer_is_ignored()
    {
        using var f = With(("App:TrustedProxies:0", "10.1.0.0/16"));
        var who = await f.WhoAmI("203.0.113.9", "198.51.100.1", "http");   // the test client talks https
        Assert.Equal("203.0.113.9", (string?)who["ip"]);
        Assert.Equal("https", (string?)who["scheme"]);
    }

    [Fact]
    public async Task Nothing_is_trusted_by_default()
    {
        using var f = With();
        Assert.Equal("10.1.2.3", (string?)(await f.WhoAmI("10.1.2.3", "198.51.100.1"))["ip"]);
        var o = f.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Empty(o.KnownProxies); Assert.Empty(o.KnownIPNetworks); Assert.Equal(1, o.ForwardLimit);
    }

    [Fact]
    public async Task A_peer_inside_a_trusted_cidr_range_is_honoured()
    {
        using var f = With(("App:TrustedProxies:0", "10.1.0.0/16"));
        var who = await f.WhoAmI("10.1.77.5", "198.51.100.1", "http");
        Assert.Equal("198.51.100.1", (string?)who["ip"]);
        Assert.Equal("http", (string?)who["scheme"]);
    }

    [Fact]
    public async Task A_single_trusted_ip_and_an_address_with_a_port_work()
    {
        using var f = With(("App:TrustedProxies:0", "192.0.2.10"));
        Assert.Equal("198.51.100.1", (string?)(await f.WhoAmI("192.0.2.10", "198.51.100.1:49152"))["ip"]);   // App Service sends ip:port
        Assert.Equal("192.0.2.11", (string?)(await f.WhoAmI("192.0.2.11", "198.51.100.1"))["ip"]);            // a neighbour address is not trusted
    }

    [Fact]
    public async Task Forward_limit_1_uses_only_the_right_most_client()
    {
        using var f = With(("App:TrustedProxies:0", "10.1.0.0/16"));
        // the client injected 1.2.3.4 (and the proxy appended the real peer 198.51.100.1): only the right-most entry counts
        var who = await f.WhoAmI("10.1.0.1", "1.2.3.4, 198.51.100.1");
        Assert.Equal("198.51.100.1", (string?)who["ip"]);
        Assert.Equal("1.2.3.4", (string?)who["forwardedFor"]);   // the injected hop is left unconsumed
    }

    [Fact]
    public async Task Forward_limit_1_holds_even_when_the_injected_hop_is_itself_inside_a_trusted_range()
    {
        using var f = With(("App:TrustedProxies:0", "10.0.0.0/8"));
        // 10.9.9.9 is a "proxy" address the client made up; with the limit at 1 it is not walked past
        Assert.Equal("10.9.9.9", (string?)(await f.WhoAmI("10.0.0.1", "10.9.9.9"))["ip"]);
        Assert.Equal("10.9.9.9", (string?)(await f.WhoAmI("10.0.0.1", "198.51.100.1, 10.9.9.9"))["ip"]);
    }

    [Fact]
    public async Task The_platform_opt_in_trusts_private_ingress_ranges_only()
    {
        using var f = With(("App:TrustPlatformProxy", "true"));
        Assert.Equal("198.51.100.1", (string?)(await f.WhoAmI("169.254.129.1", "198.51.100.1:5000", "https"))["ip"]);
        Assert.Equal("198.51.100.1", (string?)(await f.WhoAmI("10.0.1.4", "198.51.100.1"))["ip"]);
        Assert.Equal("203.0.113.9", (string?)(await f.WhoAmI("203.0.113.9", "198.51.100.1"))["ip"]);   // a public peer is never a platform proxy
    }

    [Fact]
    public async Task The_platform_opt_in_is_off_by_default()
    {
        using var f = With();
        Assert.Equal("169.254.129.1", (string?)(await f.WhoAmI("169.254.129.1", "198.51.100.1"))["ip"]);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("0.0.0.0/1")]
    [InlineData("4.0.0.0/7")]
    [InlineData("2000::/8")]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("")]
    public void App_refuses_to_start_with_a_catch_all_or_malformed_trusted_proxy(string entry)
    {
        foreach (var platform in new[] { "false", "true" })
        {
            using var f = With(("App:TrustedProxies:0", entry), ("App:TrustPlatformProxy", platform));
            Assert.ThrowsAny<Exception>(() => f.CreateClient());
        }
    }

    [Fact]
    public void Resolve_rejects_catch_all_and_accepts_the_narrowest_allowed_ranges()
    {
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.Resolve(new AppOptions { TrustedProxies = ["0.0.0.0/0"], TrustPlatformProxy = true }));
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.Resolve(new AppOptions { TrustedProxies = ["::/0"] }));
        Assert.False(ForwardedHeadersSetup.Resolve(new AppOptions()).Enabled);
        var t = ForwardedHeadersSetup.Resolve(new AppOptions { TrustedProxies = ["10.0.0.0/8", "fd00::/16", "192.0.2.1"] });
        Assert.True(t.Enabled); Assert.Equal(2, t.Networks.Count); Assert.Single(t.Proxies);
        Assert.True(ForwardedHeadersSetup.Resolve(new AppOptions { TrustPlatformProxy = true }).Enabled);
    }

    // ---- what the forwarded address is used for -----------------------------------------------------------------------------------------

    private async Task<HttpStatusCode> Login(ProxyFactory f, string peer, string? xff)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = "nobody@example.com", password = "x" }) };
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        return (await f.NewClient().SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task The_rate_limit_partition_is_the_forwarded_client_only_when_the_peer_is_trusted()
    {
        using var f = With(("App:TrustedProxies:0", "10.1.0.0/16"), ("RateLimit:AuthPerMinute", "2"));
        // trusted proxy: two different real clients get a bucket each, a third request from the first one is limited
        for (var i = 0; i < 2; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Login(f, "10.1.0.1", "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Login(f, "10.1.0.1", "198.51.100.1"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Login(f, "10.1.0.1", "198.51.100.2"));
        // untrusted peer: the header cannot pick a fresh bucket; the peer's own address is the key
        for (var i = 0; i < 2; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Login(f, "203.0.113.9", "198.51.100." + (10 + i)));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Login(f, "203.0.113.9", "198.51.100.99"));
    }

    [Fact]
    public async Task Without_trusted_proxies_every_client_shares_the_peer_bucket()
    {
        using var f = With(("RateLimit:AuthPerMinute", "2"));
        for (var i = 0; i < 2; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Login(f, "10.1.0.1", "198.51.100." + i));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Login(f, "10.1.0.1", "198.51.100.77"));
    }

    private static async Task<(HttpStatusCode Status, string? Code)> Dashboard(ProxyFactory f, string peer, string? xff)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/dashboard");
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        var res = await f.NewClient().SendAsync(req);
        string? code = null;
        try { code = (string?)(await res.Content.ReadFromJsonAsync<JsonNode>())?["code"]; } catch { /* no body */ }
        return (res.StatusCode, code);
    }

    [Fact]
    public async Task The_admin_allowlist_sees_the_forwarded_client_only_when_the_peer_is_trusted()
    {
        using var f = With(("App:TrustedProxies:0", "10.1.0.0/16"), ("Admin:IpAllowlist:0", "198.51.100.0/24"));
        // trusted proxy, allowed real client: passes the IP gate (then 401, there is no staff session)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Dashboard(f, "10.1.0.1", "198.51.100.5")).Status);
        // trusted proxy, real client outside the allowlist
        Assert.Equal("ip_not_allowed", (await Dashboard(f, "10.1.0.1", "203.0.113.5")).Code);
        // untrusted peer that claims an allowed address: the claim is ignored
        Assert.Equal("ip_not_allowed", (await Dashboard(f, "203.0.113.5", "198.51.100.5")).Code);
    }
}
