using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Api.Data;
using Solvelt.Api.Email;

namespace Solvelt.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "solvelt-tests-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Extra { get; } = new();
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Qu1et-Marble-Fox-Test!";
    public const string Strong = "Tr1cky-Horse-Battery!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Dir);
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={Path.Combine(Dir, "t.db")}",
            ["App:HmacSecret"] = "test-hmac-secret-0123456789abcdef",
            ["App:UseHttpsRedirection"] = "false",
            ["App:CorsOrigins:0"] = "http://localhost:5173",
            ["App:FrontendBaseUrl"] = "http://localhost:5173",
            ["Email:Provider"] = "Sink",
            ["Turnstile:UseTestStub"] = "true",
            ["Turnstile:SecretKey"] = "1x0000000000000000000000000000000AA",
            ["Turnstile:SiteKey"] = "1x00000000000000000000AA",
            ["AdminSeed:Email"] = AdminEmail,
            ["AdminSeed:Password"] = AdminPassword,
            ["Storage:LocalRoot"] = Path.Combine(Dir, "uploads"),
            ["RateLimit:AuthPerMinute"] = "100000",
            ["RateLimit:SensitivePerMinute"] = "100000",
            ["Passkeys:RelyingPartyId"] = "localhost",
            ["Passkeys:AllowedOrigins:0"] = "http://localhost:5173",
        };
        foreach (var (k, v) in Extra) settings[k] = v;
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
        builder.ConfigureLogging(l => l.ClearProviders());
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, StubOidcHandler>("Google", _ => { });
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false
    });

    public IReadOnlyList<EmailMessage> Mails(string to, string? kind = null) =>
        Services.GetRequiredService<EmailSink>().All()
            .Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase) && (kind is null || m.Kind == kind)).ToList();

    public string LastCode(string to, string kind = "verify-code") =>
        Regex.Match(Mails(to, kind).Last().Text, @"\b(\d{6})\b").Groups[1].Value;

    public T WithDb<T>(Func<AppDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, true); } catch { /* ignore */ }
    }
}

/// <summary>Stub OIDC provider: challenge redirects straight to the callback with claims taken from the query-string-free properties.</summary>
public class StubOidcHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public static string Sub = "g-sub-1", Email = "google.user@example.com", Verified = "true";
    public static readonly object Lock = new();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Emulates the OIDC round-trip: sign the external identity into the External cookie, then go to RedirectUri.
        var id = new ClaimsIdentity("Google");
        id.AddClaim(new Claim("sub", Sub)); id.AddClaim(new Claim("email", Email)); id.AddClaim(new Claim("email_verified", Verified));
        await Context.SignInAsync(Solvelt.Api.Extensions.ExternalScheme, new ClaimsPrincipal(id), properties);
        Response.Redirect(properties.RedirectUri!);
    }
}

public record Login(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken, Guid SellerId, string Email,
    string VendorName, string AccountStatus, string[] Roles);

public class Api(ApiFactory f)
{
    public HttpClient Http { get; } = f.NewClient();
    public Login? Session { get; private set; }
    public string? Csrf { get; private set; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NewEmail() => $"seller-{Guid.NewGuid():N}@example.com";

    public async Task<HttpResponseMessage> Post(string url, object? body = null, bool auth = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        return await Send(req, auth);
    }

    public Task<HttpResponseMessage> Get(string url, bool auth = true) => Send(new HttpRequestMessage(HttpMethod.Get, url), auth);
    public Task<HttpResponseMessage> Put(string url, object body) => Send(new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) }, true);
    public Task<HttpResponseMessage> Delete(string url) => Send(new HttpRequestMessage(HttpMethod.Delete, url), true);

    public async Task<HttpResponseMessage> Send(HttpRequestMessage req, bool auth = true)
    {
        if (auth && Session is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
        var res = await Http.SendAsync(req);
        CaptureCsrf(res);
        return res;
    }

    private void CaptureCsrf(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var cs)) return;
        foreach (var c in cs.Where(c => c.StartsWith("sl_csrf=")))
        { var v = c["sl_csrf=".Length..].Split(';')[0]; Csrf = v.Length == 0 ? null : v; }
    }

    public async Task<HttpResponseMessage> Refresh(bool csrf = true, string? bodyToken = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { refreshToken = bodyToken }) };
        if (csrf && Csrf is not null) req.Headers.Add("X-CSRF-Token", Csrf);
        var res = await Send(req, false);
        if (res.IsSuccessStatusCode) Session = await res.Content.ReadFromJsonAsync<Login>(Json);
        return res;
    }

    public async Task<Login> LoginAs(string email, string password)
    {
        var res = await Post("/api/v1/auth/login", new { email, password }, false);
        res.EnsureSuccessStatusCode();
        Session = await res.Content.ReadFromJsonAsync<Login>(Json);
        return Session!;
    }

    /// <summary>Signs up, verifies the email and returns the signed-in api.</summary>
    public async Task<string> SignupVerified(string? email = null, string password = ApiFactory.Strong)
    {
        email ??= NewEmail();
        var res = await Post("/api/v1/auth/signup", new { email, password, acceptTerms = true }, false);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Session = await res.Content.ReadFromJsonAsync<Login>(Json);
        var v = await Post("/api/v1/auth/verify-email", new { code = f.LastCode(email) });
        Assert.Equal(HttpStatusCode.OK, v.StatusCode);
        var tok = await v.Content.ReadFromJsonAsync<JsonElement>();
        Session = Session! with { AccessToken = tok.GetProperty("accessToken").GetString()! };
        return email;
    }

    public static readonly object ValidBusiness = new
    {
        vendorName = "Acme Co", contactName = "Dana Seller", contactPhone = "+1 555 123 4567", addressLine1 = "1 Main St",
        city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", smsOptIn = false
    };

    public async Task CompleteApplicationAndSubmit(bool food = false)
    {
        Assert.True((await Put("/api/v1/seller/application/business", ValidBusiness)).IsSuccessStatusCode);
        var ans = food ? new Dictionary<string, string> { ["sells_food"] = "no" } : new Dictionary<string, string> { ["sells_food"] = "no" };
        Assert.True((await Put("/api/v1/seller/application/kosher", new { answers = ans })).IsSuccessStatusCode);
        var s = await Post("/api/v1/seller/application/submit", new { acceptSellerAgreement = true });
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
    }

    public async Task<JsonElement> JsonBody(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
}
