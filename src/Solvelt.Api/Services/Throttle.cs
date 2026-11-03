using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

/// <summary>Hint returned beside the 401 body. Computed from the failure counter only, so it is identical for real and non-existent accounts.</summary>
public record AttemptsHint(int? AttemptsRemaining, bool LastTry, bool CaptchaRequired, DateTime? LockedUntil, string? Message);

/// <summary>
/// Per-submitted-email attempt counter. All writes are single atomic statements (ExecuteUpdate / insert with PK conflict retry), and an attempt is
/// *reserved* (counted) before the password is checked, so concurrent guesses can never exceed <see cref="LockoutSettings.MaxFailures"/>.
/// </summary>
public class LoginThrottleService(AppDbContext db, Crypto crypto, TimeProvider time, IOptions<LockoutSettings> opt)
{
    private LockoutSettings O => opt.Value;
    private string Key(string email) => crypto.Hmac("login:" + Crypto.NormalizeEmail(email));

    public async Task<LoginThrottle?> GetAsync(string email)
    {
        var key = Key(email);
        var row = await db.LoginThrottles.AsNoTracking().FirstOrDefaultAsync(x => x.KeyHash == key);
        if (row is null) return null;
        var now = time.UtcNow();
        var expiredLock = row.LockedUntil is { } lu && lu <= now;
        var staleWindow = row.LockedUntil is null && now - row.LastFailureAt > TimeSpan.FromMinutes(O.FailureWindowMinutes);
        if (expiredLock || staleWindow)
        {
            await db.LoginThrottles.Where(x => x.KeyHash == key).ExecuteDeleteAsync();
            return null;
        }
        return row;
    }

    public bool IsLocked(LoginThrottle? t) => t is not null && (t.Failures >= O.MaxFailures || (t.LockedUntil is { } lu && lu > time.UtcNow()));
    public bool CaptchaRequired(LoginThrottle? t) => t is { } x && x.Failures >= O.CaptchaAtFailures;

    /// <summary>Atomically counts one attempt and returns the new total (> MaxFailures means the caller must NOT check the password).</summary>
    public async Task<int> ReserveAttemptAsync(string email)
    {
        var key = Key(email);
        for (var i = 0; i < 5; i++)
        {
            var now = time.UtcNow();
            // One statement increments AND returns the new value, so concurrent requests each get a distinct number (no read-after-write gap).
            var returned = await db.Database.SqlQuery<int>(
                $"UPDATE \"LoginThrottles\" SET \"Failures\" = \"Failures\" + 1, \"LastFailureAt\" = {now} WHERE \"KeyHash\" = {key} RETURNING \"Failures\" AS \"Value\"")
                .ToListAsync();
            if (returned.Count == 0)
            {
                try
                {
                    db.LoginThrottles.Add(new LoginThrottle { KeyHash = key, Failures = 1, LastFailureAt = now });
                    await db.SaveChangesAsync();
                    return 1;
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear(); // another request created the row first: retry as an update
                    continue;
                }
            }
            return returned[0];
        }
        return O.MaxFailures + 1; // could not reserve: fail closed
    }

    /// <summary>Gives back a reservation for an attempt that should not count (e.g. correct password, 2FA code still to be entered).</summary>
    public Task ReleaseAttemptAsync(string email)
    {
        var key = Key(email);
        return db.LoginThrottles.Where(x => x.KeyHash == key && x.Failures > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Failures, x => x.Failures - 1));
    }

    /// <summary>Locks the key once (returns true only for the request that set the lock).</summary>
    public async Task<bool> LockIfReachedAsync(string email, int attempts)
    {
        if (attempts < O.MaxFailures) return false;
        var key = Key(email);
        var until = time.UtcNow().AddMinutes(O.LockMinutes);
        var n = await db.LoginThrottles.Where(x => x.KeyHash == key && x.LockedUntil == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, until));
        return n == 1;
    }

    /// <summary>Counts a failure that was not reserved up front (used by flows that verify a second factor separately).</summary>
    public async Task<(LoginThrottle? Row, bool JustLocked)> RegisterFailureAsync(string email)
    {
        var attempts = await ReserveAttemptAsync(email);
        var locked = await LockIfReachedAsync(email, attempts);
        return (await GetAsync(email), locked);
    }

    public Task ResetAsync(string email)
    {
        var key = Key(email);
        return db.LoginThrottles.Where(x => x.KeyHash == key).ExecuteDeleteAsync();
    }

    public AttemptsHint Hint(LoginThrottle? t)
    {
        if (t is null) return new(null, false, false, null, null);
        if (IsLocked(t))
            return new(0, false, true, t.LockedUntil ?? time.UtcNow().AddMinutes(O.LockMinutes), $"Account locked. Try again in {O.LockMinutes} minutes or reset your password.");
        var remaining = O.MaxFailures - t.Failures;
        if (t.Failures >= O.CaptchaAtFailures) return new(remaining, true, true, null, "Last try");
        if (t.Failures >= O.WarnAtFailures) return new(remaining, false, false, null, $"{remaining} tries left");
        return new(null, false, false, null, null);
    }
}
