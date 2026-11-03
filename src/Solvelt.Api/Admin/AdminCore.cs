using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Admin;

public class AdminOptions
{
    public const string Section = "Admin";
    /// <summary>Absolute staff session lifetime (no refresh: staff sign in again).</summary>
    public int SessionMinutes { get; set; } = 60;
    public int IdleMinutes { get; set; } = 15;
    /// <summary>How long a step-up re-authentication stays valid for sensitive actions.</summary>
    public int StepUpMinutes { get; set; } = 5;
    public int InviteDays { get; set; } = 3;
    /// <summary>Optional allowlist of client IPs / CIDR ranges (e.g. "203.0.113.0/24"). Empty: no restriction.</summary>
    public string[] IpAllowlist { get; set; } = [];
    public int ReadsPerMinute { get; set; } = 240;
    public int WritesPerMinute { get; set; } = 60;
    public int RevealsPerMinute { get; set; } = 20;
    public int MaxExportRows { get; set; } = 50_000;
}

public sealed record StaffContext(Guid UserId, string Email, StaffRole Role, Guid SessionId, DateTime? StepUpAt);

public static class AdminHttp
{
    private const string Key = "solvelt.staff";
    public static StaffContext Staff(this HttpContext c) => (StaffContext)c.Items[Key]!;
    internal static void SetStaff(this HttpContext c, StaffContext s) => c.Items[Key] = s;

    public static IResult Err(int status, string code, string message, object? extra = null) =>
        Results.Json(new { type = "about:blank", title = message, status, code, errors = extra }, statusCode: status);

    /// <summary>Every staff write carries a reason (5-500 chars). Returns the error result or null.</summary>
    public static IResult? NeedReason(string? reason, out string clean)
    {
        clean = (reason ?? "").Trim();
        if (clean.Length < 5) return Err(422, "reason_required", "A reason of at least 5 characters is required.");
        if (clean.Length > 500) return Err(422, "reason_too_long", "The reason can be at most 500 characters.");
        return null;
    }

    public static (int Page, int Size) Paging(int? page, int? size) => (Math.Max(1, page ?? 1), Math.Clamp(size ?? 25, 1, 100));

    public static bool IpAllowed(HttpContext c, AdminOptions o)
    {
        if (o.IpAllowlist.Length == 0) return true;
        var ip = c.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var entry in o.IpAllowlist)
        {
            if (entry.Contains('/')) { if (IPNetwork.TryParse(entry, out var net) && net.Contains(ip)) return true; }
            else if (IPAddress.TryParse(entry, out var one) && one.Equals(ip)) return true;
        }
        return false;
    }

    /// <summary>IP allowlist for the whole admin surface (including the staff sign-in endpoints).</summary>
    public static TBuilder RequireAllowedIp<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
            IpAllowed(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value)
                ? await next(ctx) : Err(403, "ip_not_allowed", "Staff access is not allowed from this network."));

    /// <summary>The single gate of every staff endpoint: staff session + 2FA + active staff member + permission (+ recent step-up).</summary>
    public static TBuilder RequireStaff<TBuilder>(this TBuilder b, StaffPermission perm, string bucket = "read") where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var deny = await AdminGate.CheckAsync(ctx.HttpContext, perm);
            if (deny is not null) return deny;
            // Per-staff-member limits run AFTER authentication (the global rate limiter runs before it and could only partition by IP).
            var limiter = ctx.HttpContext.RequestServices.GetRequiredService<AdminRateLimiter>();
            return limiter.TryAcquire(bucket, ctx.HttpContext.Staff().UserId) ? await next(ctx) : Err(429, "rate_limited", "Too many requests. Slow down.");
        });

    public static RouteHandlerBuilder AGet(this RouteGroupBuilder g, string p, Delegate h, StaffPermission perm) => g.MapGet(p, h).RequireStaff(perm, "read");
    public static RouteHandlerBuilder APost(this RouteGroupBuilder g, string p, Delegate h, StaffPermission perm, string bucket = "write") => g.MapPost(p, h).RequireStaff(perm, bucket);
    public static RouteHandlerBuilder APut(this RouteGroupBuilder g, string p, Delegate h, StaffPermission perm) => g.MapPut(p, h).RequireStaff(perm, "write");
    public static RouteHandlerBuilder ADelete(this RouteGroupBuilder g, string p, Delegate h, StaffPermission perm) => g.MapDelete(p, h).RequireStaff(perm, "write");
}

/// <summary>Fixed-window per-staff-member limits for admin reads / writes / PII reveals.</summary>
public class AdminRateLimiter(IOptions<AdminOptions> opt, TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> _w = new();

    public bool TryAcquire(string bucket, Guid user)
    {
        var o = opt.Value;
        var limit = bucket switch { "write" => o.WritesPerMinute, "reveal" => o.RevealsPerMinute, _ => o.ReadsPerMinute };
        var window = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = bucket + ":" + user;
        var state = _w.AddOrUpdate(key, (window, 1), (_, s) => s.Window == window ? (window, s.Count + 1) : (window, 1));
        if (_w.Count > 10_000) foreach (var k in _w.Where(x => x.Value.Window < window).Select(x => x.Key).ToList()) _w.TryRemove(k, out _);
        return state.Count <= limit;
    }
}

public static class AdminGate
{
    public static async Task<IResult?> CheckAsync(HttpContext http, StaffPermission perm)
    {
        if (http.User.Identity?.IsAuthenticated != true) return AdminHttp.Err(401, "unauthenticated", "Sign in first.");
        var sp = http.RequestServices;
        var opt = sp.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (!AdminHttp.IpAllowed(http, opt)) return AdminHttp.Err(403, "ip_not_allowed", "Staff access is not allowed from this network.");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var uid) || !Guid.TryParse(http.User.FindFirstValue("sid"), out var sid))
            return AdminHttp.Err(401, "unauthenticated", "Sign in first.");
        var db = sp.GetRequiredService<AppDbContext>();
        var now = sp.GetRequiredService<TimeProvider>().UtcNow();
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.UserId == uid);
        // A seller session (even of a staff member or an Admin-role user) is never a staff session: separate session context.
        if (session is null || session.RevokedAt is not null || session.Kind != "staff") return AdminHttp.Err(403, "not_staff_session", "Staff area: sign in through the staff sign-in.");
        if (session.MfaAt is null) return AdminHttp.Err(403, "two_factor_required", "Two-factor authentication is required for staff.");
        var staff = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == uid);
        if (staff is null || !staff.Active) return AdminHttp.Err(403, "not_staff", "This account is not an active staff member.");
        if (!StaffPermissions.Has(staff.Role, perm)) return AdminHttp.Err(403, "forbidden", "Your staff role doesn't allow this.");
        if (StaffPermissions.StepUp.Contains(perm) && !(session.StepUpAt is { } su && now - su <= TimeSpan.FromMinutes(opt.StepUpMinutes)))
            return AdminHttp.Err(403, "step_up_required", "Confirm it's you again with your authenticator code to do this.");
        http.SetStaff(new StaffContext(uid, http.User.FindFirstValue("email") ?? "", staff.Role, sid, session.StepUpAt));
        return null;
    }
}

/// <summary>Writes the staff audit trail: who (user+role), what, target, why, before/after.</summary>
public class AdminAudit(AppDbContext db, TimeProvider time, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public void Add(StaffContext s, string action, string targetType, object? targetId, string? reason, object? before = null, object? after = null, string? extra = null)
    {
        string? detail = null;
        if (before is not null || after is not null || extra is not null)
        {
            detail = JsonSerializer.Serialize(new { before, after, extra }, J);
            if (detail.Length > 4000) detail = detail[..4000];
        }
        db.AuditEvents.Add(new AuditEvent
        {
            Type = "admin." + action, UserId = s.UserId, Subject = AuditLog.Mask(s.Email), IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Detail = detail, At = time.UtcNow(), TargetType = targetType, TargetId = targetId?.ToString(), Reason = reason, ActorRole = s.Role.ToString()
        });
    }

    public async Task WriteAsync(StaffContext s, string action, string targetType, object? targetId, string? reason, object? before = null, object? after = null, string? extra = null)
    {
        Add(s, action, targetType, targetId, reason, before, after, extra);
        await db.SaveChangesAsync();
    }
}

public static class Mask
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"\+?\d[\d\-\s().]{6,}\d", RegexOptions.Compiled);

    public static string? EmailAddr(string? e)
    {
        if (string.IsNullOrEmpty(e)) return e;
        var at = e.IndexOf('@');
        if (at < 1) return "***";
        var dom = e[(at + 1)..]; var dot = dom.LastIndexOf('.');
        return e[0] + "***@" + (dot > 0 ? dom[0] + "***" + dom[dot..] : "***");
    }

    public static string? Name(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return n;
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0] + "***"));
    }

    public static string? PhoneNo(string? p) => string.IsNullOrEmpty(p) ? p : p.Length <= 4 ? "****" : new string('*', p.Length - 2) + p[^2..];
    public static string? Postal(string? p) => string.IsNullOrEmpty(p) ? p : p.Length <= 2 ? "**" : p[..2] + new string('*', p.Length - 2);

    /// <summary>Hides e-mail addresses and phone numbers inside free text (messages).</summary>
    public static string Text(string? t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        t = Email.Replace(t, m => EmailAddr(m.Value)!);
        return Phone.Replace(t, m => PhoneNo(m.Value)!);
    }
}

/// <summary>Redacts outbox payloads for display: personal fields and secrets are replaced; ids, numbers, states stay.</summary>
public static class PayloadRedactor
{
    private static readonly Regex Sensitive = new("(email|phone|name|address|street|recipient|company|contact|line|city|postal|zip|region|token|secret|signature|password|pin|code|note|message|text|gift|reason|details|body|answer|key)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string json, int max = 4000)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var red = Walk(node, null);
            var s = red?.ToJsonString() ?? "null";
            return s.Length > max ? s[..max] + "…" : s;
        }
        catch (JsonException) { return "[unparseable payload withheld]"; }
    }

    private static JsonNode? Walk(JsonNode? n, string? key)
    {
        switch (n)
        {
            case JsonObject o:
                var res = new JsonObject();
                foreach (var (k, v) in o) res[k] = Walk(v, k);
                return res;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var v in a) arr.Add(Walk(v, key));
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (key is not null && Sensitive.IsMatch(key) && !key.EndsWith("Id", StringComparison.Ordinal) && !key.EndsWith("_id", StringComparison.Ordinal)) return "[redacted]";
                return Mask.Text(s);
            default: return n?.DeepClone();
        }
    }
}

public static class Csv
{
    /// <summary>One CSV cell, always quoted; cells that a spreadsheet would execute as a formula (= + - @ TAB CR) are prefixed with an apostrophe.</summary>
    public static string Cell(string? v)
    {
        v ??= "";
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public static string Row(params string?[] cells) => string.Join(',', cells.Select(Cell));
}

/// <summary>Dispatches creation of staff-only JWTs: separate session kind, short lifetime, no refresh token, no seller account.</summary>
public class StaffSessionService(AppDbContext db, JwtKeys keys, TimeProvider time, IOptions<JwtOptions> jwt, IOptions<AdminOptions> opt)
{
    public async Task<(string Token, int ExpiresIn, Guid SessionId)> CreateAsync(AppUser user, string? ip, string? ua)
    {
        var o = opt.Value; var now = time.UtcNow();
        var s = new UserSession
        {
            UserId = user.Id, CreatedAt = now, LastActivityAt = now, Method = "staff-2fa", Kind = "staff", MfaAt = now,
            IdleLimitMinutes = o.IdleMinutes, ExpiresAt = now.AddMinutes(o.SessionMinutes), IpAddress = ip, UserAgent = ua is { Length: > 300 } ? ua[..300] : ua
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("sid", s.Id.ToString()), new(JwtRegisteredClaimNames.Email, user.Email ?? ""), new("email_verified", user.EmailConfirmed ? "true" : "false"),
            new("ctx", "staff")
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer, Audience = jwt.Value.Audience, Subject = new ClaimsIdentity(claims),
            NotBefore = now, IssuedAt = now, Expires = now.AddMinutes(o.SessionMinutes),
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.RsaSha256)
        });
        return (token, o.SessionMinutes * 60, s.Id);
    }
}

/// <summary>Platform settings stored in the database (override the config defaults).</summary>
public class PlatformSettings(AppDbContext db, IOptions<AppOptions> app, IOptions<Solvelt.Api.Orders.OrderOptions> orders)
{
    public const string FeeKey = "marketplace_fee_bps", TermsKey = "terms_version", AgreementKey = "seller_agreement_version";

    private async Task<string?> GetAsync(string key) => await db.PlatformSettings.AsNoTracking().Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync();
    public async Task<int> FeeBasisPointsAsync() => int.TryParse(await GetAsync(FeeKey), out var v) ? v : orders.Value.FeeBasisPoints;
    public async Task<string> TermsVersionAsync() => await GetAsync(TermsKey) ?? app.Value.TermsVersion;
    public async Task<string> AgreementVersionAsync() => await GetAsync(AgreementKey) ?? app.Value.SellerAgreementVersion;

    /// <summary>Same lookups without DI (for code paths that already hold a DbContext).</summary>
    public static async Task<string> TermsAsync(AppDbContext db, string fallback) =>
        await db.PlatformSettings.AsNoTracking().Where(x => x.Key == TermsKey).Select(x => x.Value).FirstOrDefaultAsync() ?? fallback;
    public static async Task<string> AgreementAsync(AppDbContext db, string fallback) =>
        await db.PlatformSettings.AsNoTracking().Where(x => x.Key == AgreementKey).Select(x => x.Value).FirstOrDefaultAsync() ?? fallback;
    public static async Task<int> FeeAsync(AppDbContext db, int fallback) =>
        int.TryParse(await db.PlatformSettings.AsNoTracking().Where(x => x.Key == FeeKey).Select(x => x.Value).FirstOrDefaultAsync(), out var v) ? v : fallback;

    public async Task SetAsync(string key, string value, Guid by, DateTime now)
    {
        var row = await db.PlatformSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (row is null) db.PlatformSettings.Add(new PlatformSetting { Key = key, Value = value, UpdatedAt = now, UpdatedBy = by });
        else { row.Value = value; row.UpdatedAt = now; row.UpdatedBy = by; }
    }
}

/// <summary>Records the last run of background jobs for the integration-health page.</summary>
public class JobRecorder(AppDbContext db, TimeProvider time)
{
    public async Task<T> RunAsync<T>(string name, Func<Task<T>> job)
    {
        var started = time.UtcNow();
        try
        {
            var result = await job();
            await RecordAsync(name, started, true, JsonSerializer.Serialize(result));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordAsync(name, started, false, ex.GetType().Name);
            throw;
        }
    }

    private async Task RecordAsync(string name, DateTime started, bool ok, string? result)
    {
        db.ChangeTracker.Clear();
        var row = await db.JobRuns.FirstOrDefaultAsync(j => j.Name == name);
        if (row is null) { row = new JobRun { Name = name }; db.JobRuns.Add(row); }
        row.LastStartedAt = started; row.LastFinishedAt = time.UtcNow(); row.LastSucceeded = ok;
        row.LastResult = result is { Length: > 500 } ? result[..500] : result;
        row.Runs++; if (!ok) row.Failures++;
        await db.SaveChangesAsync();
    }
}

public static class AdminModule
{
    public static WebApplicationBuilder AddAdmin(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        builder.Services.Configure<AdminOptions>(cfg.GetSection(AdminOptions.Section));
        var o = cfg.GetSection(AdminOptions.Section).Get<AdminOptions>() ?? new AdminOptions();
        builder.Services.AddScoped<StaffSessionService>();
        builder.Services.AddScoped<AdminAudit>();
        builder.Services.AddScoped<PlatformSettings>();
        builder.Services.AddScoped<JobRecorder>();
        builder.Services.AddScoped<StaffService>();
        builder.Services.AddScoped<SellerAdminService>();
        builder.Services.AddScoped<ModerationService>();
        builder.Services.AddSingleton<AdminRateLimiter>();
        return builder;
    }

    public static void MapAdmin(this WebApplication app, RouteGroupBuilder v1)
    {
        app.MapAdminInternalEndpoints();
        var g = v1.MapGroup("/admin").RequireAllowedIp();
        g.MapStaffAuthEndpoints();
        g.MapStaffManagementEndpoints();
        g.MapDashboardEndpoints();
        g.MapApplicationEndpoints();
        g.MapSellerAdminEndpoints();
        g.MapUserAdminEndpoints();
        g.MapModerationEndpoints();
        g.MapSupportEndpoints();
        g.MapIntegrationEndpoints();
        g.MapAuditEndpoints();
        g.MapSettingsEndpoints();
    }

    /// <summary>Startup: maps the legacy Identity role "Admin" to Super admin (backfill) and keeps both in step.</summary>
    public static async Task SeedAsync(IServiceProvider sp, TimeProvider time)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var now = time.UtcNow();
        foreach (var u in await users.GetUsersInRoleAsync("Admin"))
            if (!await db.StaffMembers.AnyAsync(s => s.UserId == u.Id))
                db.StaffMembers.Add(new StaffMember { UserId = u.Id, Role = StaffRole.SuperAdmin, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }
}
