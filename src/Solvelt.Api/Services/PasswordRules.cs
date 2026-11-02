using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

/// <summary>10+ chars, upper, lower, digit, symbol; rejects common passwords (and optionally HIBP-breached ones).</summary>
public class PasswordRulesValidator(IOptions<PasswordSettings> opt, IHttpClientFactory http, ILogger<PasswordRulesValidator> log)
    : IPasswordValidator<AppUser>
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password","password1","password12","password123","password1!","passw0rd!","p@ssw0rd","p@ssword1","p@ssw0rd123",
        "qwerty","qwertyuiop","qwerty123","qwerty123!","qwertyuiop1!","1q2w3e4r5t","1qaz2wsx","letmein","letmein123",
        "welcome","welcome1","welcome123","welcome1!","admin","admin123","administrator","iloveyou","iloveyou1",
        "abc123","abcd1234","abcdefghij","monkey","dragon","football","baseball","master","sunshine","princess",
        "trustno1","changeme","changeme123","123456789","1234567890","12345678","0123456789","000000","111111",
        "solvelt","solvelt123","marketplace","seller123","passwordpassword","correcthorse"
    };

    public static IReadOnlyList<string> Describe(string? password, string? email = null)
    {
        var errors = new List<string>();
        password ??= "";
        if (password.Length < 10) errors.Add("Use at least 10 characters.");
        if (!password.Any(char.IsUpper)) errors.Add("Add an uppercase letter.");
        if (!password.Any(char.IsLower)) errors.Add("Add a lowercase letter.");
        if (!password.Any(char.IsDigit)) errors.Add("Add a number.");
        if (!password.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c))) errors.Add("Add a symbol.");
        var stripped = new string(password.Where(char.IsLetter).ToArray());
        if (Common.Contains(password) || Common.Contains(stripped) || IsTrivial(password))
            errors.Add("That password is too common. Choose something less guessable.");
        else if (!string.IsNullOrEmpty(email) && email.Contains('@') &&
                 email[..email.IndexOf('@')].Length >= 4 &&
                 password.Contains(email[..email.IndexOf('@')], StringComparison.OrdinalIgnoreCase))
            errors.Add("Don't include your email name in your password.");
        return errors;
    }

    private static bool IsTrivial(string p) => p.Length > 0 && p.Distinct().Count() <= 3;

    public async Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        var errors = Describe(password, user.Email).ToList();
        if (errors.Count == 0 && opt.Value.CheckPwned && password is not null && await IsPwnedAsync(password))
            errors.Add("That password appears in known data breaches. Choose another.");
        return errors.Count == 0
            ? IdentityResult.Success
            : IdentityResult.Failed(errors.Select(e => new IdentityError { Code = "PasswordRules", Description = e }).ToArray());
    }

    private async Task<bool> IsPwnedAsync(string password)
    {
        try
        {
            var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
            var client = http.CreateClient("hibp");
            var body = await client.GetStringAsync("https://api.pwnedpasswords.com/range/" + sha[..5]);
            return body.Split('\n').Any(l => l.StartsWith(sha[5..], StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Pwned-password check unavailable; failing open");
            return false;
        }
    }
}
