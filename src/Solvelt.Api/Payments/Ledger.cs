using Solvelt.Api.Services;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Payments;

public record Posting(LedgerAccount Account, long AmountCents, string Kind);

/// <summary>Balances of one seller in one currency (minor units). <see cref="Owed"/> = pending + available + reserved (what the platform owes the seller).</summary>
public record Balances(string Currency, long Pending, long Available, long Reserved, long Settled, long PaidOut)
{
    public long Owed => Pending + Available + Reserved;
    /// <summary>Negative = the seller owes the platform (pending + available + reserved below zero). A shipping label debits Available while the sale is still Pending: that is not a negative balance.</summary>
    public bool Negative => Owed < 0;
    public static Balances Zero(string c) => new(c, 0, 0, 0, 0, 0);
}

public static class DbErrors
{
    /// <summary>True for a unique-constraint violation on SQLite or PostgreSQL.</summary>
    /// <summary>PostgreSQL serialization failure (40001) or deadlock (40P01): the transaction lost a race and nothing was applied; retry it.</summary>
    public static bool IsSerialization(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Npgsql.PostgresException { SqlState: "40001" or "40P01" }) return true;
        return false;
    }

    public static bool IsUnique(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }) return true;
            if (e is Npgsql.PostgresException { SqlState: "23505" }) return true;
        }
        return false;
    }
}

/// <summary>
/// The append-only double-entry ledger. A journal is a balanced set of entries (sum = 0 per journal) with a unique idempotency key: posting the same
/// source event twice creates nothing new. Callers add journals to the context and save them together with their own state change (one transaction).
/// </summary>
public class LedgerService(AppDbContext db, TimeProvider time)
{
    public static long RoundHalfUp(long numerator, long denominator) =>
        denominator <= 0 || numerator <= 0 ? 0 : (numerator * 2 + denominator) / (denominator * 2);

    public async Task<bool> ExistsAsync(string key) =>
        db.ChangeTracker.Entries<LedgerJournal>().Any(e => e.Entity.IdempotencyKey == key && e.State == EntityState.Added)
        || await db.LedgerJournals.AnyAsync(j => j.IdempotencyKey == key);

    /// <summary>Adds a balanced journal (not saved). Returns null when the idempotency key was already posted. Throws when the entries do not sum to zero.</summary>
    public async Task<LedgerJournal?> PostAsync(Guid sellerId, string currency, string type, string key, string memo, IEnumerable<Posting> postings,
        Guid? orderId = null, Guid? bookingId = null, string? sourceRef = null, Guid? actor = null, string? reason = null)
    {
        var list = postings.Where(p => p.AmountCents != 0).ToList();
        if (list.Count == 0) return null;
        if (list.Sum(p => p.AmountCents) != 0) throw new InvalidOperationException($"Unbalanced journal {type}/{key}");
        if (await ExistsAsync(key)) return null;
        var now = time.UtcNow();
        var j = new LedgerJournal
        {
            SellerId = sellerId, Currency = currency, Type = type, IdempotencyKey = key, OrderId = orderId, BookingId = bookingId, SourceRef = sourceRef,
            Memo = memo.Length > 300 ? memo[..300] : memo, ActorUserId = actor, Reason = reason, CreatedAt = now
        };
        foreach (var p in list)
            j.Entries.Add(new LedgerEntry { SellerId = sellerId, Currency = currency, Account = p.Account, AmountCents = p.AmountCents, Kind = p.Kind, OrderId = orderId, BookingId = bookingId, CreatedAt = now });
        db.LedgerJournals.Add(j);
        return j;
    }

    /// <summary>Saves pending changes; false when a concurrent writer won a unique key (the duplicate is dropped, nothing is half-applied).</summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (DbErrors.IsUnique(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task<List<Balances>> BalancesAsync(Guid sellerId)
    {
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => e.SellerId == sellerId)
            .GroupBy(e => new { e.Currency, e.Account }).Select(g => new { g.Key.Currency, g.Key.Account, Sum = g.Sum(x => x.AmountCents) }).ToListAsync();
        return rows.GroupBy(r => r.Currency).Select(g =>
        {
            long S(LedgerAccount a) => g.Where(x => x.Account == a).Sum(x => x.Sum);
            return new Balances(g.Key, S(LedgerAccount.Pending), S(LedgerAccount.Available), S(LedgerAccount.Reserved), S(LedgerAccount.Settled), S(LedgerAccount.PaidOut));
        }).OrderBy(b => b.Currency).ToList();
    }

    public async Task<Balances> BalanceAsync(Guid sellerId, string currency) =>
        (await BalancesAsync(sellerId)).FirstOrDefault(b => b.Currency == currency) ?? Balances.Zero(currency);

    /// <summary>Pending balance still attached to one order or booking (what a refund before release reduces, what a release moves).</summary>
    public async Task<long> PendingForAsync(Guid? orderId, Guid? bookingId) =>
        await db.LedgerEntries.Where(e => e.Account == LedgerAccount.Pending && (orderId != null ? e.OrderId == orderId : e.BookingId == bookingId)).SumAsync(e => (long?)e.AmountCents) ?? 0;

    public async Task<long> SumAsync(Guid? orderId, Guid? bookingId, LedgerAccount account, string kind) =>
        await db.LedgerEntries.Where(e => e.Account == account && e.Kind == kind && (orderId != null ? e.OrderId == orderId : e.BookingId == bookingId)).SumAsync(e => (long?)e.AmountCents) ?? 0;
}
