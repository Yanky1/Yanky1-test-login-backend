using Solvelt.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;

namespace Solvelt.Api.Payments;

/// <summary>Payment emails to the account Owners and Admins who opted in (<c>NotificationPreference.PaymentEmails</c>). Each (seller, key) is sent once.</summary>
public class PaymentNotifier(AppDbContext db, Mailer mailer, IOptions<AppOptions> app, TimeProvider time, ILogger<PaymentNotifier> log)
{
    public async Task<List<string>> RecipientsAsync(Guid sellerId)
    {
        var members = await db.Memberships.AsNoTracking().Where(m => m.SellerId == sellerId && m.Status == MembershipStatus.Active && m.Role >= AccountRole.Admin).ToListAsync();
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => p.SellerId == sellerId).ToListAsync();
        var ids = members.Where(m => m.Role == AccountRole.Owner || prefs.FirstOrDefault(p => p.UserId == m.UserId)?.PaymentEmails == true).Select(m => m.UserId).ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.Email != null).Select(u => u.Email!).ToListAsync();
    }

    /// <summary>Returns true when the mail was sent now (false: already sent for this key, or no recipient). Mail failures never break a payment flow.</summary>
    public async Task<bool> NotifyAsync(Guid sellerId, string key, string subject, string heading, string[] lines, string path = "/payments")
    {
        try
        {
            db.PaymentNotices.Add(new PaymentNotice { SellerId = sellerId, Key = key, SentAt = time.UtcNow() });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (DbErrors.IsUnique(ex)) { db.ChangeTracker.Clear(); return false; }
            var url = app.Value.FrontendBaseUrl.TrimEnd('/') + path;
            foreach (var to in await RecipientsAsync(sellerId))
                await mailer.SendAsync(Templates.Notification(to, "payments", subject, heading, lines, "Open Payments", url));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Payment notification {Key} failed", key); return false; }
    }

    public static string Money(long cents, string currency) => $"{(cents < 0 ? "-" : "")}{Math.Abs(cents) / 100}.{Math.Abs(cents) % 100:D2} {currency}";
}
