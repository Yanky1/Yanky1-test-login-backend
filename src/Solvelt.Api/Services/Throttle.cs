using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

/// <summary>Hint returned beside the 401 body. Computed from the failure counter only, so it is identical for real and non-existent accounts.</summary>
public record AttemptsHint(int? AttemptsRemaining, bool LastTry, bool CaptchaRequired, DateTime? LockedUntil, string? Message);

public class LoginThrottleService(AppDbContext db, Crypto crypto, TimeProvider time, IOptions<LockoutSettings> opt)
{
    private LockoutSettings O => opt.Value;

    public async Task<LoginThrottle?> GetAsync(string email)
    {
        var key = crypto.Hmac("login:" + Crypto.NormalizeEmail(email));
        var row = await db.LoginThrottles.FirstOrDefaultAsync(x => x.KeyHash == key);
        if (row is null) return null;
        var now = time.UtcNow();
        if (row.LockedUntil is { } lu && lu <= now) { db.LoginThrottles.Remove(row); await db.SaveChangesAsync(); return null; }
        if (row.LockedUntil is null && now - row.LastFailureAt > TimeSpan.FromMinutes(O.FailureWindowMinutes))
        { db.LoginThrottles.Remove(row); await db.SaveChangesAsync(); return null; }
        return row;
    }

    public bool IsLocked(LoginThrottle? t) => t?.LockedUntil is { } lu && lu > time.UtcNow();
    public bool CaptchaRequired(LoginThrottle? t) => t is { } x && x.Failures >= O.CaptchaAtFailures;

    /// <summary>Registers a failure; returns the new row and whether this failure caused the lock.</summary>
    public async Task<(LoginThrottle Row, bool JustLocked)> RegisterFailureAsync(string email)
    {
        var key = crypto.Hmac("login:" + Crypto.NormalizeEmail(email));
        var row = await GetAsync(email);
        var now = time.UtcNow();
        if (row is null) { row = new LoginThrottle { KeyHash = key }; db.LoginThrottles.Add(row); }
        row.Failures++; row.LastFailureAt = now;
        var locked = false;
        if (row.Failures >= O.MaxFailures && row.LockedUntil is null)
        { row.LockedUntil = now.AddMinutes(O.LockMinutes); locked = true; }
        await db.SaveChangesAsync();
        return (row, locked);
    }

    public async Task ResetAsync(string email)
    {
        var key = crypto.Hmac("login:" + Crypto.NormalizeEmail(email));
        var row = await db.LoginThrottles.FirstOrDefaultAsync(x => x.KeyHash == key);
        if (row is not null) { db.LoginThrottles.Remove(row); await db.SaveChangesAsync(); }
    }

    public AttemptsHint Hint(LoginThrottle? t)
    {
        if (t is null) return new(null, false, false, null, null);
        if (IsLocked(t)) return new(0, false, true, t.LockedUntil, $"Account locked. Try again in {O.LockMinutes} minutes or reset your password.");
        var remaining = O.MaxFailures - t.Failures;
        if (t.Failures >= O.CaptchaAtFailures) return new(remaining, true, true, null, "Last try");
        if (t.Failures >= O.WarnAtFailures) return new(remaining, false, false, null, $"{remaining} tries left");
        return new(null, false, false, null, null);
    }
}
