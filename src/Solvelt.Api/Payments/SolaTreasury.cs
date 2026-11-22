using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Payments;

/// <summary>One currency of the Sola treasury view (minor units).</summary>
/// <param name="ReleasedCents">Seller money of Sola-paid sales that left Pending (released to Available / Reserved, so it is transferred or can be transferred from the Stripe balance),
/// net of what Sola refunds, chargebacks and chargeback fees took back from Available afterwards (and plus won chargebacks).</param>
/// <param name="FundedCents">Net of the <c>provider_funding</c> journals: money staff recorded as moved from the Sola settlement to the Stripe balance.</param>
/// <param name="UnfundedCents"><c>max(0, released - funded)</c>: how much Ronald still has to move from Sola to Stripe.</param>
/// <param name="HeldAtSolaCents">What the ledger says sits at Sola: Sola sales minus Sola refunds and chargebacks (plus won ones) minus the funded amount.</param>
public record SolaTreasuryLine(string Currency, long ReleasedCents, long FundedCents, long UnfundedCents, long OverfundedCents, long HeldAtSolaCents, bool TransfersHeld);

/// <summary>
/// C19 treasury: transfers to sellers are paid from the platform's STRIPE balance, while the buyer money of phone orders sits at Sola. Staff record each move from Sola to Stripe
/// as a <c>provider_funding</c> journal (Platform -x, PlatformSola +x; the reverse corrects); the unfunded figure tells Ronald how much still has to move.
/// </summary>
public class SolaTreasuryService(AppDbContext db, LedgerService ledger, IOptions<PaymentOptions> opt)
{
    public const string FundingType = "provider_funding";
    /// <summary>Journals that move a Sola sale's seller money in or out of Available / Reserved: the release, and what Sola takes back (or gives back) after it.</summary>
    private static readonly string[] DrawTypes = ["release", "refund", "dispute", "dispute_fee", "dispute_won"];

    public bool HoldSwitch => opt.Value.Sola.HoldTransfersWhenUnfunded;

    public async Task<List<SolaTreasuryLine>> LinesAsync(CancellationToken ct = default)
    {
        var solaOrders = db.LedgerJournals.Where(j => j.Type == "sale" && j.Provider == PaymentProviders.Sola && j.OrderId != null).Select(j => j.OrderId);
        var drawn = await db.LedgerEntries.AsNoTracking()
            .Where(e => (e.Account == LedgerAccount.Available || e.Account == LedgerAccount.Reserved) && e.OrderId != null && solaOrders.Contains(e.OrderId)
                && db.LedgerJournals.Any(j => j.Id == e.JournalId && DrawTypes.Contains(j.Type)))
            .GroupBy(e => e.Currency).Select(g => new { g.Key, Sum = g.Sum(x => x.AmountCents) }).ToListAsync(ct);
        var sola = await db.LedgerEntries.AsNoTracking().Where(e => e.Account == LedgerAccount.PlatformSola)
            .Select(e => new { e.Currency, e.AmountCents, Funding = db.LedgerJournals.Any(j => j.Id == e.JournalId && j.Type == FundingType) })
            .GroupBy(x => new { x.Currency, x.Funding }).Select(g => new { g.Key.Currency, g.Key.Funding, Sum = g.Sum(x => x.AmountCents) }).ToListAsync(ct);
        var currencies = drawn.Select(d => d.Key).Concat(sola.Select(s => s.Currency)).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        var hold = HoldSwitch;
        return currencies.Select(c =>
        {
            var released = drawn.Where(d => d.Key == c).Sum(d => d.Sum);
            var funded = sola.Where(s => s.Currency == c && s.Funding).Sum(s => s.Sum);
            var held = -sola.Where(s => s.Currency == c).Sum(s => s.Sum);
            var unfunded = Math.Max(0, released - funded);
            return new SolaTreasuryLine(c, released, funded, unfunded, Math.Max(0, funded - released), held, hold && unfunded > 0);
        }).ToList();
    }

    /// <summary>Currencies whose new transfer batches are held (only with the owner switch on, and only while that currency's unfunded Sola balance is above zero).</summary>
    public async Task<HashSet<string>> HeldCurrenciesAsync(CancellationToken ct = default) =>
        HoldSwitch ? (await LinesAsync(ct)).Where(l => l.TransfersHeld).Select(l => l.Currency).ToHashSet() : [];

    public static string Key(string requestId) => "provider-funding:" + requestId;

    /// <summary>Adds (does not save) a funding journal. direction: sola_to_stripe (Platform -x, PlatformSola +x) or stripe_to_sola (the reverse, a correction).</summary>
    public async Task<LedgerJournal?> AddFundingAsync(string direction, long amount, string currency, string reference, string note, string requestId, Guid actor)
    {
        var x = direction == "sola_to_stripe" ? amount : -amount;
        return await ledger.PostAsync(Guid.Empty, currency, FundingType, Key(requestId), $"Funding {(x > 0 ? "Sola to Stripe" : "Stripe to Sola (correction)")} {reference}",
            [new(LedgerAccount.Platform, -x, "funding"), new(LedgerAccount.PlatformSola, x, "funding")], sourceRef: reference, actor: actor, reason: note, provider: PaymentProviders.Sola);
    }
}
