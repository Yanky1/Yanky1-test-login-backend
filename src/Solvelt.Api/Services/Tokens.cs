using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

public class JwtKeys
{
    public RsaSecurityKey SigningKey { get; }
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    public JwtKeys(IOptions<JwtOptions> opt, IHostEnvironment env)
    {
        var o = opt.Value;
        var rsa = RSA.Create(3072);
        if (!string.IsNullOrWhiteSpace(o.PrivateKeyPem)) rsa.ImportFromPem(o.PrivateKeyPem);
        else if (env.IsProduction()) throw new InvalidOperationException("Jwt:PrivateKeyPem must be configured in Production.");
        SigningKey = new RsaSecurityKey(rsa) { KeyId = o.KeyId };
        var keys = new List<SecurityKey> { new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = o.KeyId } };
        foreach (var (kid, pem) in o.PreviousPublicKeys)
        {
            var r = RSA.Create(); r.ImportFromPem(pem);
            keys.Add(new RsaSecurityKey(r.ExportParameters(false)) { KeyId = kid });
        }
        ValidationKeys = keys;
    }
}

public record IssuedSession(Guid SessionId, string AccessToken, int ExpiresIn, string RefreshToken);

public class SessionService(AppDbContext db, UserManager<AppUser> users, JwtKeys keys, TimeProvider time,
    IOptions<JwtOptions> jwt, IOptions<SessionSettings> sess, IAuditLog audit)
{
    public int AccessSeconds => jwt.Value.AccessTokenMinutes * 60;

    public async Task<IssuedSession> CreateAsync(AppUser user, string method, string? ip, string? ua)
    {
        var now = time.UtcNow();
        var s = new UserSession
        {
            UserId = user.Id, CreatedAt = now, LastActivityAt = now, Method = method,
            IpAddress = ip, UserAgent = ua is { Length: > 300 } ? ua[..300] : ua
        };
        db.Sessions.Add(s);
        var raw = AddRefreshToken(s.Id, s.Id, now);
        await db.SaveChangesAsync();
        return new IssuedSession(s.Id, await IssueAccessAsync(user, s.Id), AccessSeconds, raw);
    }

    private string AddRefreshToken(Guid sessionId, Guid family, DateTime now)
    {
        var raw = Crypto.RandomToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            SessionId = sessionId, FamilyId = family, TokenHash = Crypto.Sha256(raw),
            CreatedAt = now, ExpiresAt = now.AddDays(sess.Value.RefreshTokenDays)
        });
        return raw;
    }

    public async Task<string> IssueAccessAsync(AppUser user, Guid sessionId)
    {
        var roles = await users.GetRolesAsync(user);
        var now = time.GetUtcNow();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("sid", sessionId.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new("email_verified", user.EmailConfirmed ? "true" : "false")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer, Audience = jwt.Value.Audience, Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime, IssuedAt = now.UtcDateTime,
            Expires = now.UtcDateTime.AddSeconds(AccessSeconds),
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.RsaSha256)
        });
    }

    public bool IsIdle(UserSession s) => time.UtcNow() - s.LastActivityAt > TimeSpan.FromMinutes(sess.Value.IdleTimeoutMinutes);

    public enum RotateOutcome { Ok, Invalid, Reuse, Idle, Expired }
    public record RotateResult(RotateOutcome Outcome, AppUser? User = null, IssuedSession? Session = null);

    public async Task<RotateResult> RotateAsync(string rawToken)
    {
        var hash = Crypto.Sha256(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null) return new(RotateOutcome.Invalid);
        var now = time.UtcNow();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == token.SessionId);
        if (session is null || session.RevokedAt is not null || token.RevokedAt is not null) return new(RotateOutcome.Invalid);
        if (token.UsedAt is not null)
        {
            await RevokeSessionAsync(session.Id, "refresh-reuse");
            await audit.WriteAsync("refresh.reuse_detected", session.UserId, null, $"session={session.Id}");
            return new(RotateOutcome.Reuse);
        }
        if (token.ExpiresAt <= now) { await RevokeSessionAsync(session.Id, "expired"); return new(RotateOutcome.Expired); }
        if (IsIdle(session)) { await RevokeSessionAsync(session.Id, "idle"); await audit.WriteAsync("session.idle_revoked", session.UserId); return new(RotateOutcome.Idle); }
        var user = await users.FindByIdAsync(session.UserId.ToString());
        if (user is null) return new(RotateOutcome.Invalid);

        token.UsedAt = now;
        session.LastActivityAt = now;
        var raw = AddRefreshToken(session.Id, token.FamilyId, now);
        await db.SaveChangesAsync();
        return new(RotateOutcome.Ok, user, new IssuedSession(session.Id, await IssueAccessAsync(user, session.Id), AccessSeconds, raw));
    }

    public async Task RevokeSessionAsync(Guid sessionId, string reason)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || s.RevokedAt is not null) return;
        s.RevokedAt = time.UtcNow(); s.RevokedReason = reason;
        var toks = await db.RefreshTokens.Where(t => t.SessionId == sessionId && t.RevokedAt == null).ToListAsync();
        foreach (var t in toks) t.RevokedAt = s.RevokedAt;
        await db.SaveChangesAsync();
    }

    public async Task RevokeAllAsync(Guid userId, string reason, Guid? except = null)
    {
        var now = time.UtcNow();
        var active = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != except).ToListAsync();
        var ids = active.Select(a => a.Id).ToList();
        foreach (var s in active) { s.RevokedAt = now; s.RevokedReason = reason; }
        var toks = await db.RefreshTokens.Where(t => ids.Contains(t.SessionId) && t.RevokedAt == null).ToListAsync();
        foreach (var t in toks) t.RevokedAt = now;
        await db.SaveChangesAsync();
    }

    /// <summary>Validates sid claim: session exists, not revoked, not idle. Touches activity.</summary>
    public async Task<bool> ValidateAndTouchAsync(Guid sessionId, Guid userId)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId);
        if (s is null || s.RevokedAt is not null) return false;
        if (IsIdle(s)) { await RevokeSessionAsync(s.Id, "idle"); return false; }
        var now = time.UtcNow();
        if (now - s.LastActivityAt > TimeSpan.FromSeconds(10)) { s.LastActivityAt = now; await db.SaveChangesAsync(); }
        return true;
    }
}
