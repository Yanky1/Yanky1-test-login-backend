using Solvelt.Api.Services;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Options;

namespace Solvelt.Api.Payments;

public record OnboardingStatus(string State, bool HasAccount, bool DetailsSubmitted, bool ChargesEnabled, bool PayoutsEnabled, string[] CurrentlyDue, string[] PastDue,
    string? DisabledReason, DateTime? Deadline, List<string> NextSteps, object? PayoutSettings, bool Sandbox);

/// <summary>Stripe Express connected account per seller ACCOUNT: create on demand, hosted onboarding links, dashboard login links, status mapping.</summary>
public class OnboardingService(AppDbContext db, IPaymentProvider provider, TimeProvider time, IOptions<AppOptions> app, IOptions<StripeOptions> stripe)
{
    public static string[] Strs(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    /// <summary>Plain-language next steps for Stripe's requirement keys.</summary>
    public static List<string> Steps(IEnumerable<string> keys, string? disabledReason)
    {
        var steps = new List<string>();
        void Add(string s) { if (!steps.Contains(s)) steps.Add(s); }
        foreach (var k in keys)
        {
            if (k.StartsWith("external_account")) Add("Add the bank account where you want to be paid.");
            else if (k.Contains("verification.document") || k.Contains("verification.additional_document")) Add("Upload an identity or business document.");
            else if (k.Contains("tax_id") || k.Contains("ssn") || k.Contains("id_number")) Add("Provide your tax identification number.");
            else if (k.StartsWith("business_profile") || k.StartsWith("company.") || k.StartsWith("business_type")) Add("Confirm your business details.");
            else if (k.StartsWith("tos_acceptance")) Add("Accept Stripe's terms of service.");
            else if (k.StartsWith("individual.") || k.StartsWith("representative.") || k.StartsWith("owners.") || k.StartsWith("person_")) Add("Verify your identity and the owners of your business.");
            else Add("Provide the remaining information Stripe asks for.");
        }
        if (disabledReason is not null && steps.Count == 0) Add("Stripe paused payouts for this account. Open the setup to see what is needed.");
        return steps;
    }

    public static OnboardingStatus Map(PayoutAccount? a, bool sandbox)
    {
        if (a is null) return new("not_started", false, false, false, false, [], [], null, null, ["Set up payouts to start receiving your earnings."], null, sandbox);
        var due = Strs(a.RequirementsDueJson); var past = Strs(a.RequirementsPastDueJson);
        var state = a.PayoutsEnabled && due.Length == 0 && past.Length == 0 && a.DisabledReason is null ? "active"
            : past.Length > 0 || a.DisabledReason is not null ? "restricted"
            : !a.DetailsSubmitted ? "in_progress" : "action_required";
        var steps = state == "active" ? [] : Steps(due.Concat(past), a.DisabledReason);
        if (state == "in_progress" && steps.Count == 0) steps.Add("Finish the Stripe setup: it only takes a few minutes.");
        return new(state, true, a.DetailsSubmitted, a.ChargesEnabled, a.PayoutsEnabled, due, past, a.DisabledReason, a.RequirementsDeadline, steps,
            new { interval = a.PayoutInterval, delayDays = a.PayoutDelayDays, bankLast4 = a.BankLast4, bankName = a.BankName }, sandbox);
    }

    public async Task<OnboardingStatus> StatusAsync(Guid sellerId) =>
        Map(await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.SellerId == sellerId), provider.IsFake || stripe.Value.SecretKey.Contains("_test_"));

    public async Task<(PayoutAccount Account, LinkResult Link)> StartAsync(Guid sellerId, Guid ownerUserId, CancellationToken ct = default)
    {
        var acct = await db.PayoutAccounts.FirstOrDefaultAsync(a => a.SellerId == sellerId, ct);
        if (acct is null)
        {
            var email = await db.Users.AsNoTracking().Where(u => u.Id == ownerUserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            var country = await db.Sellers.AsNoTracking().Where(s => s.Id == sellerId).Select(s => s.Country).FirstOrDefaultAsync(ct);
            country = country is { Length: 2 } ? country.ToUpperInvariant() : stripe.Value.DefaultCountry;
            var id = await provider.CreateExpressAccountAsync(sellerId, email, country, ct);
            var now = time.UtcNow();
            acct = new PayoutAccount { SellerId = sellerId, StripeAccountId = id, CreatedAt = now, UpdatedAt = now, RequirementsDueJson = "[\"external_account\"]" };
            db.PayoutAccounts.Add(acct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (DbErrors.IsUnique(ex))
            {   // a concurrent request created it first: use theirs
                db.ChangeTracker.Clear();
                acct = await db.PayoutAccounts.AsNoTracking().FirstAsync(a => a.SellerId == sellerId, ct);
            }
        }
        var baseUrl = app.Value.FrontendBaseUrl.TrimEnd('/');
        var link = await provider.CreateAccountLinkAsync(acct.StripeAccountId, baseUrl + "/payments?onboarding=return", baseUrl + "/payments?onboarding=refresh", ct);
        return (acct, link);
    }

    /// <summary>Pulls the account from Stripe (used when the seller returns from the hosted flow before the webhook arrived).</summary>
    public async Task<OnboardingStatus> SyncAsync(Guid sellerId, CancellationToken ct = default)
    {
        var acct = await db.PayoutAccounts.FirstOrDefaultAsync(a => a.SellerId == sellerId, ct);
        if (acct is null) return await StatusAsync(sellerId);
        var snap = await provider.RetrieveAccountAsync(acct.StripeAccountId, ct);
        StripeWebhookService.Apply(acct, snap, time.UtcNow());
        acct.UpdatedAt = time.UtcNow();
        await db.SaveChangesAsync(ct);
        return await StatusAsync(sellerId);
    }

    public async Task<LinkResult?> DashboardAsync(Guid sellerId, CancellationToken ct = default)
    {
        var acct = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.SellerId == sellerId, ct);
        return acct is null || !acct.DetailsSubmitted ? null : await provider.CreateLoginLinkAsync(acct.StripeAccountId, ct);
    }
}
