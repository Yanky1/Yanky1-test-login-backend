using System.Net;
using System.Net.Http.Json;
using Solvelt.Api.Security;
using Solvelt.Api.Services;
using Solvelt.Api.Email;

namespace Solvelt.Api.Tests;

public class SecurityTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Security_headers_are_present()
    {
        var res = await new Api(f).Get("/health", false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        var api = await new Api(f).Get("/api/v1/public-config", false);
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Cors_allows_only_listed_origins()
    {
        var c = new Api(f);
        var ok = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        ok.Headers.Add("Origin", "http://localhost:5173"); ok.Headers.Add("Access-Control-Request-Method", "POST");
        var r1 = await c.Http.SendAsync(ok);
        Assert.True(r1.Headers.Contains("Access-Control-Allow-Origin"));
        var bad = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        bad.Headers.Add("Origin", "https://evil.example"); bad.Headers.Add("Access-Control-Request-Method", "POST");
        var r2 = await c.Http.SendAsync(bad);
        Assert.False(r2.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/a/b?x=1#h", "/a/b?x=1#h")]
    [InlineData("https://evil.example", null)]
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("\\\\evil.example", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/ok://x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("/a\r\nSet-Cookie: x=1", null)]
    public void SafeRedirect_only_allows_same_origin_relative_paths(string? input, string? expected) =>
        Assert.Equal(expected, SafeRedirect.Sanitize(input));

    [Fact]
    public async Task Rate_limiter_returns_429_with_problem_details_per_ip()
    {
        await using var limited = new ApiFactoryWith(("RateLimit:AuthPerMinute", "3"));
        var c = new Api(limited);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.Post("/api/v1/auth/login", new { email = "a@b.co", password = "x" }, false)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[4]);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
    }

    [Fact]
    public async Task Non_contract_errors_use_problem_details()
    {
        var res = await new Api(f).Post("/api/v1/auth/signup", new { email = "bad", password = "x", acceptTerms = true }, false);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Email_sink_endpoint_exists_in_Testing_only()
    {
        var res = await new Api(f).Get("/api/test/emails", false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Audit_log_records_security_events_without_secrets()
    {
        var email = Api.NewEmail();
        await new Api(f).SignupVerified(email);
        await new Api(f).Post("/api/v1/auth/login", new { email, password = "Wrong-Passw0rd!x" }, false);
        var events = f.WithDb(db => db.AuditEvents.ToList());
        Assert.Contains(events, e => e.Type == "signup.created");
        Assert.Contains(events, e => e.Type == "login.failed");
        Assert.DoesNotContain(events, e => (e.Detail ?? "").Contains("Wrong-Passw0rd") || (e.Subject ?? "").Contains(email));
    }

    [Fact]
    public async Task Verify_endpoint_requires_authentication_and_validates_format()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Post("/api/v1/auth/verify-email", new { code = "123456" }, false)).StatusCode);
        var a = new Api(f); await a.SignupVerified();
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Api(f).Get("/api/v1/seller/application", false)).StatusCode);
    }
}

public class UnitTests
{
    [Theory]
    [InlineData("Tr1cky-Horse-Battery!", true)]
    [InlineData("Short1!a", false)]
    [InlineData("alllowercase1!xx", false)]
    [InlineData("ALLUPPERCASE1!XX", false)]
    [InlineData("NoNumbers!!abcdef", false)]
    [InlineData("NoSymbols12345Abc", false)]
    [InlineData("Password123!", false)]
    [InlineData("P@ssw0rd123", false)]
    [InlineData("aaaaAAAA11!!aaaa", false)]
    public void Password_rules(string pw, bool valid) => Assert.Equal(valid, PasswordRulesValidator.Describe(pw).Count == 0);

    [Fact]
    public void Password_must_not_contain_email_name() =>
        Assert.NotEmpty(PasswordRulesValidator.Describe("Johnsmith-Rocks-99!", "johnsmith@example.com"));

    [Fact]
    public void File_sniffer_recognises_allowed_types_only()
    {
        Assert.Equal("application/pdf", FileSniffer.Sniff("%PDF-1.7"u8));
        Assert.Equal("image/png", FileSniffer.Sniff(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.Equal("image/jpeg", FileSniffer.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Null(FileSniffer.Sniff("<?php echo 1;"u8));
        Assert.Null(FileSniffer.Sniff("MZ\x90\0"u8));
    }

    [Theory]
    [InlineData(Domain.SellerStatus.Draft, Domain.SellerStatus.PendingReview, true)]
    [InlineData(Domain.SellerStatus.Draft, Domain.SellerStatus.Approved, false)]
    [InlineData(Domain.SellerStatus.PendingReview, Domain.SellerStatus.NeedsInfo, true)]
    [InlineData(Domain.SellerStatus.PendingReview, Domain.SellerStatus.Live, false)]
    [InlineData(Domain.SellerStatus.Approved, Domain.SellerStatus.Live, true)]
    [InlineData(Domain.SellerStatus.Live, Domain.SellerStatus.Suspended, true)]
    [InlineData(Domain.SellerStatus.Rejected, Domain.SellerStatus.Live, false)]
    public void Status_transitions(Domain.SellerStatus from, Domain.SellerStatus to, bool ok) =>
        Assert.Equal(ok, SellerStatusService.CanTransition(from, to));

    [Fact]
    public void Email_templates_have_html_and_text_and_encode_input()
    {
        var m = Templates.NeedsInfo("a@b.co", "<b>Acme</b>", "<script>x</script>");
        Assert.DoesNotContain("<script>", m.Html);
        Assert.Contains("&lt;script&gt;", m.Html);
        Assert.Contains("<script>x</script>", m.Text); // plain text is not HTML-encoded
        Assert.All(new[]
        {
            Templates.VerifyCode("a@b.co", "123456"), Templates.ApplicationReceived("a@b.co", "A"), Templates.Approved("a@b.co", "A"),
            Templates.Rejected("a@b.co", "A"), Templates.Locked("a@b.co", 15), Templates.ResetPassword("a@b.co", "https://x/y"),
            Templates.PasswordChanged("a@b.co"), Templates.ChangeEmailCode("a@b.co", "123456")
        }, t => { Assert.False(string.IsNullOrWhiteSpace(t.Html)); Assert.False(string.IsNullOrWhiteSpace(t.Text)); Assert.Contains("Solvelt", t.Html); });
    }

    [Fact]
    public void Audit_mask_hides_local_part() => Assert.Equal("a***@x.com", AuditLog.Mask("alice@x.com"));
}
