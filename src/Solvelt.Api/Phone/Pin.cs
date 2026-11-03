using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

public static class PinRules
{
    private static readonly HashSet<string> Common =
    [
        "123456", "654321", "123123", "121212", "112233", "123321", "159753", "147258", "258369", "246810",
        "135790", "696969", "420420", "202020", "101010", "111222", "000111", "123654", "654123", "789456", "456789"
    ];

    public static bool IsSixDigits(string? pin) => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

    /// <summary>Returns a reason, or null if the PIN is acceptable.</summary>
    public static string? Weakness(string? pin)
    {
        if (!IsSixDigits(pin)) return "Use exactly 6 digits.";
        var p = pin!;
        if (p.Distinct().Count() == 1) return "Don't repeat one digit.";
        var asc = true; var desc = true;
        for (var i = 1; i < 6; i++) { if (p[i] - p[i - 1] != 1) asc = false; if (p[i - 1] - p[i] != 1) desc = false; }
        if (asc || desc) return "Don't use a sequence like 123456.";
        if (p[..3] == p[3..] || (p[0] == p[2] && p[2] == p[4] && p[1] == p[3] && p[3] == p[5]) || (p[0] == p[1] && p[2] == p[3] && p[4] == p[5]))
            return "Don't use a repeating pattern.";
        if (Common.Contains(p)) return "That PIN is too common.";
        return null;
    }
}

/// <summary>Phone PIN storage. Never stores or logs the PIN itself; hashed with the Identity password hasher.</summary>
public class PinService(AppDbContext db, TimeProvider time, IPasswordHasher<AppUser> hasher)
{
    private static readonly AppUser Dummy = new();
    private string? _dummyHash;
    private string DummyHash => _dummyHash ??= hasher.HashPassword(Dummy, "493021");

    public string Hash(string pin) => hasher.HashPassword(Dummy, pin);

    public bool VerifyHash(string hash, string pin) =>
        hasher.VerifyHashedPassword(Dummy, hash, pin) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;

    public async Task<bool> HasPinAsync(Guid userId) => await db.PhoneCredentials.AnyAsync(c => c.UserId == userId);

    /// <summary>Always does one hash verification (also for unknown users / users without a PIN) so timing does not reveal which case applied.</summary>
    public async Task<bool> VerifyAsync(Guid? userId, string pin)
    {
        var cred = userId is null ? null : await db.PhoneCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId);
        var ok = VerifyHash(cred?.PinHash ?? DummyHash, pin);
        return cred is not null && ok;
    }

    public async Task SetHashAsync(Guid userId, string pinHash)
    {
        var cred = await db.PhoneCredentials.FirstOrDefaultAsync(c => c.UserId == userId);
        if (cred is null) db.PhoneCredentials.Add(new PhoneCredential { UserId = userId, PinHash = pinHash, SetAt = time.UtcNow() });
        else { cred.PinHash = pinHash; cred.SetAt = time.UtcNow(); }
        await db.SaveChangesAsync();
    }

    public Task SetAsync(Guid userId, string pin) => SetHashAsync(userId, Hash(pin));
    public Task RemoveAsync(Guid userId) => db.PhoneCredentials.Where(c => c.UserId == userId).ExecuteDeleteAsync();
}
