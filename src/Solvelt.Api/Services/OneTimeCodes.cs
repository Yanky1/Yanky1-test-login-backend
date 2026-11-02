using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Services;

public class OneTimeCodeService(AppDbContext db, Crypto crypto, TimeProvider time)
{
    public const int ValidMinutes = 10;
    public const int ResendSeconds = 60;
    public const int MaxAttempts = 5;

    public enum VerifyOutcome { Ok, Invalid, Expired, TooManyAttempts }
    public record IssueResult(string? Code, int RetryAfterSeconds);

    private string Hash(Guid userId, string purpose, string code) => crypto.Hmac($"otc:{userId}:{purpose}:{code}");

    /// <summary>Creates a new code unless one was sent less than 60 s ago (then Code is null and RetryAfterSeconds is set).</summary>
    public async Task<IssueResult> IssueAsync(Guid userId, string purpose, string? target = null)
    {
        var now = time.UtcNow();
        var last = await db.OneTimeCodes.Where(c => c.UserId == userId && c.Purpose == purpose)
            .OrderByDescending(c => c.SentAt).FirstOrDefaultAsync();
        if (last is not null && (now - last.SentAt).TotalSeconds < ResendSeconds)
            return new(null, ResendSeconds - (int)(now - last.SentAt).TotalSeconds);
        var old = await db.OneTimeCodes.Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null).ToListAsync();
        foreach (var o in old) o.ConsumedAt = now;
        var code = Crypto.SixDigits();
        db.OneTimeCodes.Add(new OneTimeCode
        {
            UserId = userId, Purpose = purpose, Target = target, CodeHash = Hash(userId, purpose, code),
            SentAt = now, ExpiresAt = now.AddMinutes(ValidMinutes)
        });
        await db.SaveChangesAsync();
        return new(code, ResendSeconds);
    }

    public async Task<(VerifyOutcome Outcome, OneTimeCode? Row)> VerifyAsync(Guid userId, string purpose, string code)
    {
        var now = time.UtcNow();
        var row = await db.OneTimeCodes.Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null)
            .OrderByDescending(c => c.SentAt).FirstOrDefaultAsync();
        if (row is null) return (VerifyOutcome.Invalid, null);
        if (row.ExpiresAt <= now) return (VerifyOutcome.Expired, null);
        if (row.Attempts >= MaxAttempts) return (VerifyOutcome.TooManyAttempts, null);
        row.Attempts++;
        if (!Crypto.FixedEquals(row.CodeHash, Hash(userId, purpose, code.Trim())))
        {
            await db.SaveChangesAsync();
            return (row.Attempts >= MaxAttempts ? VerifyOutcome.TooManyAttempts : VerifyOutcome.Invalid, null);
        }
        row.ConsumedAt = now;
        await db.SaveChangesAsync();
        return (VerifyOutcome.Ok, row);
    }
}
