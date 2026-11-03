using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Services;

namespace Solvelt.Api.Phone;

/// <summary>Fixed-window counters per key (caller number, source IP). In-memory: fine per instance; the DB-backed lockout is the real brute-force defence.</summary>
public class PhoneRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (DateTime Start, int Count)> _w = new();
    public bool Allow(string key, int limitPerMinute)
    {
        var now = time.UtcNow();
        var r = _w.AddOrUpdate(key, _ => (now, 1), (_, v) => now - v.Start >= TimeSpan.FromMinutes(1) ? (now, 1) : (v.Start, v.Count + 1));
        if (_w.Count > 20000) foreach (var k in _w.Where(kv => now - kv.Value.Start > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList()) _w.TryRemove(k, out _);
        return r.Count <= limitPerMinute;
    }
}

public record AttemptOutcome(AttemptsHint Hint, bool Locked);

/// <summary>Phone sign-in rules on top of the web's identity services: same account, same lockout counter (3/4/5), same lockout email.</summary>
public class PhoneIdentityService(UserManager<AppUser> users, PinService pins, LoginThrottleService throttle, OneTimeCodeService codes,
    SessionService sessions, Mailer mailer, SmsService sms, IAuditLog audit, IOptions<LockoutSettings> lo, IOptions<PhoneOptions> po, AuthFlows flows)
{
    public const string SignInPurpose = "phone-signin";

    public Task<AppUser?> FindByPhoneAsync(string e164) =>
        users.Users.FirstOrDefaultAsync(u => u.PhoneNumber == e164 && u.PhoneNumberConfirmed);

    /// <summary>Same eligibility as the web login: verified email, and not a rejected/suspended seller (admins excepted).</summary>
    public async Task<bool> CanSignInAsync(AppUser u) => u.EmailConfirmed && await flows.CanSignInAsync(u);

    /// <summary>The counter is shared with the web login (keyed by the account email). Numbers without an account get their own key so the behaviour is identical.</summary>
    public static string Key(string phone, AppUser? user) => user?.Email ?? "phone:" + phone;

    /// <summary>Sends a sign-in code to the account's verified number. Nothing is sent for unknown numbers, and the caller cannot tell the difference.</summary>
    public async Task SendSignInCodeAsync(AppUser? user)
    {
        if (user is null || user.PhoneNumber is null) return;
        var issued = await codes.IssueAsync(user.Id, SignInPurpose);
        if (issued.Code is not null)
            await sms.SendAsync(user.PhoneNumber, $"{po.Value.BrandName} sign-in code: {issued.Code}. It expires in {OneTimeCodeService.ValidMinutes} minutes. Never share it.");
    }

    public record Begin(bool Blocked, int Attempts, AppUser? User, AttemptsHint Hint);

    /// <summary>Counts the attempt atomically BEFORE anything is checked (as the web does). Blocked = locked or over the limit: nothing may be checked.</summary>
    public async Task<Begin> BeginAttemptAsync(string phone)
    {
        var user = await FindByPhoneAsync(phone);
        var key = Key(phone, user);
        var t = await throttle.GetAsync(key);
        if (throttle.IsLocked(t)) { await audit.WriteAsync("phone.blocked_locked", user?.Id, null, $"phone={PhoneFormat.Mask(phone)}"); return new(true, 0, user, throttle.Hint(t)); }
        var attempts = await throttle.ReserveAttemptAsync(key);
        if (attempts > lo.Value.MaxFailures) return new(true, attempts, user, throttle.Hint(await throttle.GetAsync(key)));
        return new(false, attempts, user, throttle.Hint(await throttle.GetAsync(key)));
    }

    public async Task<bool> CheckCodeAsync(AppUser? user, string code)
    {
        if (user is null) return false;
        var (o, _) = await codes.VerifyAsync(user.Id, SignInPurpose, code);
        return o == OneTimeCodeService.VerifyOutcome.Ok;
    }

    public async Task<bool> CheckPinAsync(AppUser? user, string pin)
    {
        var ok = await pins.VerifyAsync(user?.Id, pin); // always hashes, even for unknown numbers
        return ok && user is not null && await CanSignInAsync(user);
    }

    public async Task<AttemptOutcome> FailAsync(string phone, AppUser? user, int attempts)
    {
        var key = Key(phone, user);
        var justLocked = await throttle.LockIfReachedAsync(key, attempts);
        await audit.WriteAsync("phone.signin_failed", user?.Id, null, $"phone={PhoneFormat.Mask(phone)}");
        if (justLocked)
        {
            await audit.WriteAsync("account.locked", user?.Id, null, "channel=phone");
            if (user is not null)
            {
                await sessions.RevokeAllAsync(user.Id, "lockout");
                await mailer.SendAsync(Templates.Locked(user.Email!, lo.Value.LockMinutes));
            }
        }
        var row = await throttle.GetAsync(key);
        return new(throttle.Hint(row), throttle.IsLocked(row));
    }

    public async Task SucceedAsync(string phone, AppUser user, string channel)
    {
        await throttle.ResetAsync(Key(phone, user));
        await audit.WriteAsync("phone.signin_succeeded", user.Id, null, $"channel={channel}");
    }

    /// <summary>Spoken/texted hint, derived from the counter only (identical for real and unknown numbers).</summary>
    public string HintText(AttemptsHint h) => h.LockedUntil is not null
        ? $"Too many attempts. Try again in {lo.Value.LockMinutes} minutes, or reset your password on the website."
        : h.Message ?? "";
}

/// <summary>
/// A number + PIN chosen during a phone/link sign-up are only HELD (protected, in phone_links) until the mailbox is proven, and they attach ONLY where
/// the person who chose them proves it:
///  * phone/SMS sign-up: when the emailed code is entered in the same phone conversation (never through the web verify path);
///  * link sign-up: when the web verify happens in the browser that completed the link (an HttpOnly cookie carries a nonce).
/// Any other verification of the email (e.g. the real owner verifying on the web) drops the held credentials. Pre-hijacking defence.
/// </summary>
public class PendingPhoneService(AppDbContext db, PhoneLinkService links, UserManager<AppUser> users, PinService pins, TimeProvider time, IAuditLog audit)
{
    public const string Purpose = "phone-pending";
    public const string Cookie = "sl_pp";

    /// <param name="nonce">Set for link sign-ups (the same value goes to the completing browser in a cookie); null for phone-conversation sign-ups.</param>
    public async Task HoldAsync(Guid userId, string phone, string pinHash, string? nonce = null)
    {
        await db.PhoneLinks.Where(l => l.UserId == userId && l.Purpose == Purpose).ExecuteDeleteAsync();
        await links.CreateAsync(Purpose, userId, phone, pinHash + "|" + (nonce is null ? "" : Crypto.Sha256(nonce)), minutes: 24 * 60);
    }

    public Task DropAsync(Guid userId) => db.PhoneLinks.Where(l => l.UserId == userId && l.Purpose == Purpose).ExecuteDeleteAsync();

    /// <summary>
    /// Attaches the held number + PIN when allowed (see class comment) and always discards the held data. Returns true when attached.
    /// <paramref name="phoneConversation"/>: called from the phone/SMS email-code step; otherwise from web verify with the browser's nonce cookie.
    /// </summary>
    public async Task<bool> AttachAsync(AppUser user, bool phoneConversation, string? webNonce = null)
    {
        var now = time.UtcNow();
        var row = await db.PhoneLinks.FirstOrDefaultAsync(l => l.UserId == user.Id && l.Purpose == Purpose && l.UsedAt == null && l.ExpiresAt > now);
        if (row is null || row.Phone is null) return false;
        if (!await links.ConsumeAsync(row)) return false;
        var parts = (links.Unprotect(row) ?? "|").Split('|');
        var pinHash = parts[0]; var nonceHash = parts.Length > 1 ? parts[1] : "";
        await db.PhoneLinks.Where(l => l.Id == row.Id).ExecuteDeleteAsync(); // the held credentials never outlive this decision
        var allowed = phoneConversation ? nonceHash.Length == 0
            : nonceHash.Length > 0 && !string.IsNullOrEmpty(webNonce) && Crypto.FixedEquals(nonceHash, Crypto.Sha256(webNonce));
        if (!allowed || pinHash.Length == 0)
        {
            await audit.WriteAsync("phone.pending_dropped", user.Id, null, $"channel={(phoneConversation ? "phone" : "web")}");
            return false;
        }
        user.PhoneNumber = row.Phone; user.PhoneNumberConfirmed = true;
        try
        {
            if (!(await users.UpdateAsync(user)).Succeeded) throw new DbUpdateException("phone");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            user.PhoneNumber = null; user.PhoneNumberConfirmed = false;
            return false;
        }
        await pins.SetHashAsync(user.Id, pinHash);
        await audit.WriteAsync("phone.attached", user.Id, null, $"phone={PhoneFormat.Mask(row.Phone)}");
        return true;
    }
}

public class PhoneLinkService(AppDbContext db, TimeProvider time, IDataProtectionProvider dp, IOptions<AppOptions> app, IOptions<PhoneOptions> po)
{
    private IDataProtector P => dp.CreateProtector("solvelt.phone.link");
    public IDataProtector StateProtector => dp.CreateProtector("solvelt.phone.state");

    /// <summary>Returns the URL to text. The token is 192 random bits, stored only as a hash, single use, expiring after SignupLinkMinutes.</summary>
    public async Task<string> CreateAsync(string purpose, Guid? userId, string? phone, string? payload, int? minutes = null)
    {
        var token = Crypto.RandomToken(24);
        db.PhoneLinks.Add(new PhoneLink
        {
            TokenHash = Crypto.Sha256(token), Purpose = purpose, UserId = userId, Phone = phone,
            Payload = payload is null ? null : P.Protect(payload), ExpiresAt = time.UtcNow().AddMinutes(minutes ?? po.Value.SignupLinkMinutes)
        });
        await db.SaveChangesAsync();
        return $"{app.Value.FrontendBaseUrl.TrimEnd('/')}/l/{token}";
    }

    public async Task<PhoneLink?> FindAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;
        var h = Crypto.Sha256(token);
        var l = await db.PhoneLinks.FirstOrDefaultAsync(x => x.TokenHash == h);
        return l is null || l.UsedAt is not null || l.ExpiresAt <= time.UtcNow() ? null : l;
    }

    public string? Unprotect(PhoneLink l) { try { return l.Payload is null ? null : P.Unprotect(l.Payload); } catch { return null; } }

    /// <summary>Atomic single use: only one caller wins.</summary>
    public async Task<bool> ConsumeAsync(PhoneLink l)
    {
        var now = time.UtcNow();
        return await db.PhoneLinks.Where(x => x.Id == l.Id && x.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now)) == 1;
    }

    /// <summary>A reset link for the account: the payload is the Identity reset token (30 min, killed by the security stamp), encoded like the web reset page expects.</summary>
    public async Task<string> CreateResetAsync(UserManager<AppUser> users, AppUser user)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        return await CreateAsync("reset", user.Id, user.PhoneNumber, WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token)));
    }
}

public static class PhoneTemplates
{
    private static EmailMessage M(string to, string kind, string subject, string line)
    {
        var e = WebUtility.HtmlEncode(line);
        return new EmailMessage(to, kind, subject,
            $"<!doctype html><html><body style=\"font-family:Arial,sans-serif;color:#1f2933\"><p><b>Solvelt</b></p><p>{e}</p><p style=\"color:#6b7280;font-size:12px\">You received this email because of activity on your Solvelt seller account.</p></body></html>",
            $"Solvelt\n\n{line}\n");
    }
    private const string Tail = " If this wasn't you, reset your password and contact support.";
    public static EmailMessage PhoneAdded(string to, string masked) => M(to, "phone-added", "A phone number was added to your account", $"The mobile number ending in {masked[^4..]} was added to your account.{Tail}");
    public static EmailMessage PhoneChanged(string to, string masked) => M(to, "phone-changed", "Your account's phone number was changed", $"The mobile number on your account was changed to one ending in {masked[^4..]}.{Tail}");
    public static EmailMessage PhoneRemoved(string to) => M(to, "phone-removed", "The phone number was removed from your account", "The mobile number and phone PIN were removed from your account." + Tail);
    public static EmailMessage StepUpEmail(string to, string code) => new(to, "phone-stepup-code", "Confirm it's you",
        $"<!doctype html><html><body style=\"font-family:Arial,sans-serif\"><p><b>Solvelt</b></p><p>Someone asked to add a phone number to your account. Enter this code to continue. It expires in 10 minutes. If this wasn't you, ignore this email and reset your password.</p><p style=\"font-size:28px;letter-spacing:6px\"><b>{WebUtility.HtmlEncode(code)}</b></p></body></html>",
        $"Solvelt\n\nSomeone asked to add a phone number to your account. Enter this code to continue (10 minutes). If this wasn't you, ignore this email and reset your password.\n\n{code}\n");
    public static EmailMessage PinChanged(string to) => M(to, "phone-pin-changed", "Your phone PIN was changed", "The PIN used to sign in by phone was set or changed." + Tail);
    public static EmailMessage SignedOutAll(string to) => M(to, "phone-signout-all", "You were signed out of all devices", "A phone session signed you out of all devices." + Tail);
    public static EmailMessage ResetByText(string to) => M(to, "phone-reset-link", "A password reset link was texted to you", "A password reset link was sent to the mobile number on your account." + Tail);
}

/// <summary>Business-detail rules for the conversational sign-up. Same limits as PUT /seller/application/business.</summary>
public static class BizRules
{
    public static readonly string[] Order = AiActions.BusinessFields;
    private static readonly Dictionary<string, (int Max, string Label)> Limits = new()
    {
        ["vendorName"] = (200, "business name"), ["contactName"] = (120, "contact name"), ["addressLine1"] = (200, "street address"),
        ["city"] = (100, "city"), ["postalCode"] = (20, "postal code"), ["country"] = (2, "country")
    };
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["united states"] = "US", ["usa"] = "US", ["america"] = "US", ["united states of america"] = "US", ["canada"] = "CA", ["israel"] = "IL",
        ["united kingdom"] = "GB", ["uk"] = "GB", ["england"] = "GB", ["australia"] = "AU", ["germany"] = "DE", ["france"] = "FR", ["mexico"] = "MX"
    };

    public static string Prompt(string field) => field switch
    {
        "vendorName" => "What is the name of your business?",
        "contactName" => "What is the contact person's name?",
        "addressLine1" => "What is the street address?",
        "city" => "Which city is that in?",
        "postalCode" => "What is the postal code?",
        _ => "Which country? Say it, or give the two letter code."
    };

    /// <summary>Returns the normalised value, or an error message.</summary>
    public static (string? Value, string? Error) Validate(string field, string? raw)
    {
        if (!Limits.TryGetValue(field, out var l)) return (null, "Unknown field.");
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v)) return (null, "That was empty.");
        if (field == "country")
        {
            v = Countries.TryGetValue(v, out var c) ? c : v.ToUpperInvariant();
            if (!(v.Length == 2 && v.All(char.IsAsciiLetterUpper))) return (null, "Please use a two letter country code, like US.");
            return (v, null);
        }
        if (v.Length > l.Max) return (null, $"The {l.Label} is too long.");
        if (v.Any(char.IsControl) || v.Contains('<') || v.Contains('>')) return (null, $"The {l.Label} has characters that aren't allowed.");
        if (field != "postalCode" && field != "addressLine1" && v.All(char.IsDigit)) return (null, $"That doesn't look like a {l.Label}.");
        return (v, null);
    }
}
