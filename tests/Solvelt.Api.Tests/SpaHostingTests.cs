using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Solvelt.Api.Security;

namespace Solvelt.Api.Tests;

/// <summary>#92: the API serves the built SPA when wwwroot/index.html exists, and changes nothing when it does not.</summary>
public class SpaFactory : ApiFactory
{
    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "solvelt-spa-" + Guid.NewGuid().ToString("N"));
    public bool WithIndex { get; init; } = true;
    public const string Index = "<!doctype html><html><body><div id=\"root\">solvelt-spa</div></body></html>";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        if (WithIndex) File.WriteAllText(Path.Combine(WebRoot, "index.html"), Index);
        File.WriteAllText(Path.Combine(WebRoot, "assets", "app-3f9c1a.js"), "console.log('app');");
        File.WriteAllText(Path.Combine(WebRoot, "config.json"), "{}");
        base.ConfigureWebHost(builder);
        builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(WebRoot, true); } catch { /* ignore */ }
    }
}

public class SpaHostingTests
{
    private static string Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) || r.Content.Headers.TryGetValues(name, out v) ? string.Join(",", v) : "";
    private const string ApiCsp = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/products/123/edit")]     // a client route
    [InlineData("/login")]
    public async Task A_page_or_client_route_returns_index_html_with_the_page_csp_and_no_store(string path)
    {
        using var f = new SpaFactory();
        var r = await f.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(SpaFactory.Index, await r.Content.ReadAsStringAsync());
        Assert.StartsWith("text/html", r.Content.Headers.ContentType?.ToString());
        Assert.Equal(SpaHosting.PageCsp, Header(r, "Content-Security-Policy"));
        Assert.Contains("script-src 'self' https://challenges.cloudflare.com", SpaHosting.PageCsp);
        Assert.Equal("no-store", Header(r, "Cache-Control"));
        Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Fingerprinted_assets_are_cacheable_and_runtime_config_is_not()
    {
        using var f = new SpaFactory();
        var a = await f.NewClient().GetAsync("/assets/app-3f9c1a.js");
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal("console.log('app');", await a.Content.ReadAsStringAsync());
        var cc = Header(a, "Cache-Control");
        Assert.DoesNotContain("no-store", cc);
        Assert.Contains("max-age=31536000", cc);
        Assert.Equal(SpaHosting.PageCsp, Header(a, "Content-Security-Policy"));
        var cfg = await f.NewClient().GetAsync("/config.json");
        Assert.Equal(HttpStatusCode.OK, cfg.StatusCode);
        Assert.Equal("no-store", Header(cfg, "Cache-Control"));
    }

    [Theory]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/api/nothing/here")]
    [InlineData("/api")]
    public async Task An_unknown_api_path_is_404_json_with_the_strict_api_csp(string path)
    {
        using var f = new SpaFactory();
        var c = f.NewClient();
        var r = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(ApiCsp, Header(r, "Content-Security-Policy"));
        Assert.Equal("no-store", Header(r, "Cache-Control"));
        Assert.DoesNotContain("solvelt-spa", await r.Content.ReadAsStringAsync());
        Assert.Contains("json", r.Content.Headers.ContentType?.ToString() ?? "");
        Assert.Equal(404, (int?)(await r.Content.ReadFromJsonAsync<JsonNode>())?["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/does-not-exist", new { })).StatusCode);
    }

    [Fact]
    public async Task Real_api_endpoints_and_health_keep_the_strict_csp()
    {
        using var f = new SpaFactory();
        var h = await f.NewClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, h.StatusCode);
        Assert.Equal(ApiCsp, Header(h, "Content-Security-Policy"));
        var r = await f.NewClient().GetAsync("/api/v1/public-config");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(ApiCsp, Header(r, "Content-Security-Policy"));
        Assert.Equal("no-store", Header(r, "Cache-Control"));
    }

    [Fact]
    public async Task A_missing_file_and_a_non_get_request_to_a_page_path_are_404_not_index_html()
    {
        using var f = new SpaFactory();
        var c = f.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/assets/missing-abc.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/missing.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/some/page", new { })).StatusCode);
    }

    [Fact]
    public async Task Without_an_index_html_nothing_changes()
    {
        using var f = new ApiFactory();        // no wwwroot at all (dev, tests)
        var c = f.NewClient();
        foreach (var path in new[] { "/", "/index.html", "/products/1", "/login", "/assets/app-3f9c1a.js", "/api/v1/does-not-exist" })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal(ApiCsp, Header(r, "Content-Security-Policy"));
            Assert.DoesNotContain("solvelt-spa", await r.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task A_webroot_folder_without_an_index_html_does_not_turn_hosting_on()
    {
        using var f = new SpaFactory { WithIndex = false };
        var r = await f.NewClient().GetAsync("/products/1");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(ApiCsp, Header(r, "Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.NewClient().GetAsync("/assets/app-3f9c1a.js")).StatusCode);
    }
}
