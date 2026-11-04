using Solvelt.Api.Delivery;
using Solvelt.Api.Catalog;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Solvelt.Api;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Options;
using Solvelt.Api.Assistant;
using Solvelt.Api.Bookings;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Shipping;
using Solvelt.Api.Phone;
using Solvelt.Api.Products;
using Solvelt.Api.Security;
using Solvelt.Api.Services;
using Solvelt.Api.Teams;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;

builder.Services.Configure<AppOptions>(cfg.GetSection(AppOptions.Section));
builder.Services.Configure<JwtOptions>(cfg.GetSection(JwtOptions.Section));
builder.Services.Configure<SessionSettings>(cfg.GetSection(SessionSettings.Section));
builder.Services.Configure<LockoutSettings>(cfg.GetSection(LockoutSettings.Section));
builder.Services.Configure<RateLimitOptions>(cfg.GetSection(RateLimitOptions.Section));
builder.Services.Configure<TurnstileOptions>(cfg.GetSection(TurnstileOptions.Section));
builder.Services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
{
    // The footer of every email shows this address: a placeholder must never ship (same rule as the other secrets and keys).
    var support = EmailLayout.ResolveSupport(cfg[$"{EmailOptions.Section}:SupportAddress"], cfg[$"{PhoneOptions.Section}:HelpContact"], out var supportIsDefault);
    if (!env.IsDevelopment() && !env.IsEnvironment("Testing") && (supportIsDefault || EmailLayout.IsPlaceholderAddress(support)))
        throw new InvalidOperationException("Email:SupportAddress must be set to a real address outside Development/Testing (it is shown in every email footer; the default and example.* addresses are placeholders).");
}
builder.Services.Configure<PasskeyConfig>(cfg.GetSection(PasskeyConfig.Section));
builder.Services.Configure<StorageOptions>(cfg.GetSection(StorageOptions.Section));
builder.Services.Configure<ExternalAuthOptions>(cfg.GetSection(ExternalAuthOptions.Section));
builder.Services.Configure<AdminSeedOptions>(cfg.GetSection(AdminSeedOptions.Section));
builder.Services.Configure<PasswordSettings>(cfg.GetSection(PasswordSettings.Section));

var app0 = cfg.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
if (string.IsNullOrWhiteSpace(app0.HmacSecret))
{
    if (!env.IsDevelopment() && !env.IsEnvironment("Testing")) throw new InvalidOperationException("App:HmacSecret must be configured outside Development/Testing.");
    builder.Services.PostConfigure<AppOptions>(o => o.HmacSecret = "dev-only-hmac-secret-change-me-0123456789");
}

// ---- Database (SQLite for dev/tests, PostgreSQL in production)
var provider = cfg["Database:Provider"] ?? "Sqlite";
var conn = cfg["Database:ConnectionString"] ?? "Data Source=App_Data/solvelt.db";
builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(conn);
    else o.UseSqlite(conn);
});

// ---- Identity (+ built-in passkeys)
builder.Services.AddIdentityCore<AppUser>(o =>
{
    o.User.RequireUniqueEmail = true;
    o.Password.RequiredLength = 10;
    o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = true;
    o.Lockout.AllowedForNewUsers = false; // lockout handled by LoginThrottleService (per submitted email)
    o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
})
.AddRoles<IdentityRole<Guid>>()
.AddEntityFrameworkStores<AppDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();
builder.Services.AddScoped<IPasswordValidator<AppUser>, PasswordRulesValidator>();
{
    var pk = cfg.GetSection(PasskeyConfig.Section).Get<PasskeyConfig>() ?? new PasskeyConfig();
    builder.Services.Configure<IdentityPasskeyOptions>(o =>
    {
        o.ServerDomain = pk.RelyingPartyId;
        o.ValidateOrigin = ctx => ValueTask.FromResult(pk.AllowedOrigins.Contains(ctx.Origin ?? "", StringComparer.OrdinalIgnoreCase));
        o.UserVerificationRequirement = "required"; // a user-verified passkey is a 2-factor credential (possession + biometric/PIN), so it satisfies MFA
        o.ResidentKeyRequirement = "preferred";
    });
}
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromMinutes(30));
var dp = builder.Services.AddDataProtection().SetApplicationName("Solvelt");
if (cfg["DataProtection:KeysPath"] is { Length: > 0 } kp) dp.PersistKeysToFileSystem(new DirectoryInfo(kp));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<Crypto>();
builder.Services.AddSingleton<JwtKeys>();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AccountSecurityService>();
builder.Services.AddScoped<LoginThrottleService>();
builder.Services.AddScoped<OneTimeCodeService>();
builder.AddTeams();
builder.AddProducts();
builder.AddCatalog();
builder.AddOrders();
builder.AddDelivery();
builder.AddBookings();
builder.AddAssistant();
builder.AddAdmin();
builder.AddPayments();
builder.AddShipping();
builder.Services.AddScoped<KosherService>();
builder.Services.AddScoped<SellerStatusService>();
builder.Services.AddScoped<AuthFlows>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>();
builder.Services.AddSingleton<EmailSink>();
builder.Services.AddScoped<Mailer>();
switch ((cfg["Email:Provider"] ?? "Sink").ToLowerInvariant())
{
    case "smtp": builder.Services.AddScoped<IEmailSender, SmtpEmailSender>(); break;
    case "resend": builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(); break;
    default: builder.Services.AddScoped<IEmailSender, SinkEmailSender>(); break;
}

// ---- AuthN / AuthZ
var authBuilder = builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.RequireHttpsMetadata = true;
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            // A staff token is valid on the staff area only (never on seller, account or session endpoints).
            if (ctx.Principal?.FindFirstValue("ctx") == "staff" && !ctx.HttpContext.Request.Path.StartsWithSegments("/api/v1/admin"))
            { ctx.Fail("staff_token_outside_admin"); return; }
            var sid = ctx.Principal?.FindFirstValue("sid");
            var sub = ctx.Principal?.FindFirstValue("sub");
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<SessionService>();
            if (!Guid.TryParse(sid, out var sessionId) || !Guid.TryParse(sub, out var userId) ||
                !await sessions.ValidateAndTouchAsync(sessionId, userId))
            { ctx.Fail("session_invalid"); return; }
            // Active seller account: the token's `acc` claim is only a hint. Membership, role, brand scope and account status are re-read from
            // the database on EVERY request, so removal/demotion/suspension takes effect on the next request (no waiting for token expiry).
            if (Guid.TryParse(ctx.Principal?.FindFirstValue("acc"), out var accId))
            {
                var access = await ctx.HttpContext.RequestServices.GetRequiredService<AccountContextService>().ResolveAsync(userId, accId);
                if (access is not null) ctx.HttpContext.SetAccess(access);
            }
        }
    };
});
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtKeys, IOptions<JwtOptions>, TimeProvider>((o, keys, jwt, time) =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Value.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Value.Audience,
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero, RequireExpirationTime = true,
            ValidateIssuerSigningKey = true, RequireSignedTokens = true,
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = "sub", RoleClaimType = ClaimTypes.Role,
            LifetimeValidator = (nbf, exp, _, _) =>
                exp is not null && time.GetUtcNow().UtcDateTime < exp.Value && (nbf is null || time.GetUtcNow().UtcDateTime >= nbf.Value)
        };
    });
builder.Services.AddIdentityCookiesForPasskeys();
builder.Services.AddExternalProviders(cfg, env);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"))
    .AddPolicy("VerifiedEmail", p => p.RequireAuthenticatedUser().RequireClaim("email_verified", "true"))
    .AddPolicy("Seller", p => p.RequireRole("Seller"));

// ---- Rate limiting (per IP)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var rl = cfg.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter("auth:" + Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rl.AuthPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("sensitive", c => RateLimitPartition.GetFixedWindowLimiter("sens:" + Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rl.SensitivePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { type = "about:blank", title = "Too many requests", status = 429 }, ct);
    };
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(app0.CorsOrigins)
    .WithHeaders("Authorization", "Content-Type", "X-CSRF-Token").WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").AllowCredentials()));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 12 * 1024 * 1024);

if (cfg.GetValue<bool>("Turnstile:UseTestStub") && !env.IsEnvironment("Testing"))
    throw new InvalidOperationException("Turnstile:UseTestStub may only be enabled in the Testing environment.");

builder.AddPhoneChannels();

var app = builder.Build();

var appOpts = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
if (appOpts.TrustedProxies.Length > 0)
{
    var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fh.KnownIPNetworks.Clear(); fh.KnownProxies.Clear();
    foreach (var p in appOpts.TrustedProxies) fh.KnownProxies.Add(System.Net.IPAddress.Parse(p));
    app.UseForwardedHeaders(fh);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!env.IsDevelopment() && !env.IsEnvironment("Testing") && appOpts.UseHsts) app.UseHsts();
if (appOpts.UseHttpsRedirection) app.UseHttpsRedirection();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDriverGuard();

await Seeder.InitAsync(app);

if (env.IsDevelopment()) app.MapOpenApi();
app.MapHealthChecks("/health").AllowAnonymous();
var v1 = app.MapGroup("/api/v1");
v1.MapAuthEndpoints();
v1.MapExternalEndpoints();
v1.MapPasskeyEndpoints();
v1.MapAccountEndpoints();
v1.MapSellerEndpoints();
v1.MapAdminEndpoints();
app.MapPhoneChannels(v1);
v1.MapTeams();
app.MapProducts(v1);
app.MapOrders(v1);
app.MapCatalogInternal();
app.MapDelivery(v1);
app.MapBookings(v1);
v1.MapAssistant();
app.MapAdmin(v1);
app.MapPayments(v1);
app.MapShipping(v1);
if (env.IsEnvironment("Testing")) app.MapTestEndpoints();

app.Run();

public partial class Program;
