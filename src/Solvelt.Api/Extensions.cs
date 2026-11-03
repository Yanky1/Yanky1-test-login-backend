using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api;

public static class Extensions
{
    public const string ExternalScheme = "External";

    /// <summary>Identity's passkey flows keep short-lived challenge state in its own cookie schemes; the API itself uses JWT.</summary>
    public static IServiceCollection AddIdentityCookiesForPasskeys(this IServiceCollection s)
    {
        s.AddAuthentication().AddIdentityCookies(o =>
        {
            o.TwoFactorUserIdCookie?.Configure(c => { c.Cookie.SameSite = SameSiteMode.Strict; c.Cookie.SecurePolicy = CookieSecurePolicy.Always; });
        });
        return s;
    }

    public static IServiceCollection AddExternalProviders(this IServiceCollection s, IConfiguration cfg, IHostEnvironment env)
    {
        var o = cfg.GetSection(ExternalAuthOptions.Section).Get<ExternalAuthOptions>() ?? new ExternalAuthOptions();
        var ab = s.AddAuthentication().AddCookie(ExternalScheme, c =>
        {
            c.Cookie.Name = "sl_ext"; c.Cookie.HttpOnly = true; c.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            c.Cookie.SameSite = SameSiteMode.Lax; c.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        if (!string.IsNullOrEmpty(o.GoogleClientId))
        {
            ab.AddOpenIdConnect("Google", "Google", oidc =>
            {
                oidc.SignInScheme = ExternalScheme;
                oidc.Authority = "https://accounts.google.com";
                oidc.ClientId = o.GoogleClientId; oidc.ClientSecret = o.GoogleClientSecret;
                oidc.ResponseType = "code"; oidc.UsePkce = true; oidc.CallbackPath = "/signin-google";
                oidc.Scope.Clear(); oidc.Scope.Add("openid"); oidc.Scope.Add("email"); oidc.Scope.Add("profile");
                oidc.SaveTokens = false; oidc.GetClaimsFromUserInfoEndpoint = false; oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "name";
            });
        }
        if (o.AppleEnabled)
        {
            ab.AddOpenIdConnect("Apple", "Apple", oidc =>
            {
                oidc.SignInScheme = ExternalScheme;
                oidc.Authority = "https://appleid.apple.com";
                oidc.ClientId = o.AppleClientId;
                oidc.ResponseType = "code"; oidc.ResponseMode = "form_post"; oidc.UsePkce = true; oidc.CallbackPath = "/signin-apple";
                oidc.Scope.Clear(); oidc.Scope.Add("openid"); oidc.Scope.Add("email"); oidc.Scope.Add("name");
                oidc.MapInboundClaims = false; oidc.SaveTokens = false;
                oidc.Events = new OpenIdConnectEvents
                {
                    OnAuthorizationCodeReceived = ctx =>
                    {
                        ctx.TokenEndpointRequest!.ClientSecret = AppleClientSecret(o);
                        return Task.CompletedTask;
                    }
                };
            });
        }
        return s;
    }

    /// <summary>Apple's client secret is a short-lived ES256 JWT signed with the .p8 key.</summary>
    internal static string AppleClientSecret(ExternalAuthOptions o)
    {
        var ec = ECDsa.Create(); ec.ImportFromPem(o.ApplePrivateKeyPem);
        var now = DateTimeOffset.UtcNow;
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.AppleTeamId, Audience = "https://appleid.apple.com", Expires = now.AddMinutes(5).UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = o.AppleClientId },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ec) { KeyId = o.AppleKeyId }, SecurityAlgorithms.EcdsaSha256)
        });
    }
}

public static class Seeder
{
    public static async Task InitAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var cfg = sp.GetRequiredService<IConfiguration>();
        var provider = cfg["Database:Provider"] ?? "Sqlite";
        if (!provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var ds = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cfg["Database:ConnectionString"] ?? "Data Source=App_Data/solvelt.db").DataSource;
            if (Path.GetDirectoryName(Path.GetFullPath(ds)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        }
        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            if (cfg.GetValue("Database:AutoMigrate", false)) await db.Database.MigrateAsync();
        }
        else await db.Database.EnsureCreatedAsync();
        AppDbContext.Seed(db);
        await Solvelt.Api.Teams.TeamService.BackfillAsync(db, sp.GetRequiredService<TimeProvider>());

        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var r in new[] { "Seller", "Admin" })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole<Guid>(r));

        var seed = sp.GetRequiredService<IOptions<AdminSeedOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(seed.Email) && !string.IsNullOrWhiteSpace(seed.Password))
        {
            var um = sp.GetRequiredService<UserManager<AppUser>>();
            var email = seed.Email.Trim().ToLowerInvariant();
            if (await um.FindByEmailAsync(email) is null)
            {
                var u = new AppUser { UserName = email, Email = email, EmailConfirmed = true, HasPassword = true, CreatedAt = DateTime.UtcNow };
                var res = await um.CreateAsync(u, seed.Password);
                if (res.Succeeded) await um.AddToRoleAsync(u, "Admin");
                else app.Logger.LogError("Admin seed failed: {Errors}", string.Join("; ", res.Errors.Select(e => e.Code)));
            }
        }
    }
}
