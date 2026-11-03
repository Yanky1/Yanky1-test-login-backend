using Solvelt.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Payments;

/// <summary>Atomic lease on a named job (UPDATE ... WHERE expired): at most one instance runs it at a time, a crashed holder's lease simply expires.</summary>
public class JobLeaseService(AppDbContext db, TimeProvider time, IOptions<PaymentOptions> opt)
{
    public async Task<bool> TryAcquireAsync(string name, CancellationToken ct = default)
    {
        var now = time.UtcNow(); var until = now.AddSeconds(opt.Value.LeaseSeconds);
        if (await db.PaymentJobLeases.Where(l => l.Name == name && l.LockedUntil < now).ExecuteUpdateAsync(u => u.SetProperty(l => l.LockedUntil, until), ct) == 1) return true;
        try { db.PaymentJobLeases.Add(new PaymentJobLease { Name = name, LockedUntil = until }); await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (DbErrors.IsUnique(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task ReleaseAsync(string name, CancellationToken ct = default) =>
        await db.PaymentJobLeases.Where(l => l.Name == name).ExecuteUpdateAsync(u => u.SetProperty(l => l.LockedUntil, time.UtcNow().AddSeconds(-1)), CancellationToken.None);
}

public record ReleaseSummary(int Released, int ReserveReleased, long ReleasedCents);

/// <summary>Moves funds Pending -> Available (and Reserved) once the release rules are met, and Reserved -> Available after the reserve period.</summary>
public class ReleaseService(AppDbContext db, LedgerService ledger, IOptions<PaymentOptions> opt, IOptions<OrderOptions> orders, TimeProvider time)
{
    private static readonly OrderStatus[] ReleasableOrder = [OrderStatus.Delivered, OrderStatus.Completed];
    private static readonly BookingStatus[] ReleasableBooking = [BookingStatus.Completed, BookingStatus.NoShow, BookingStatus.CancelledByBuyer, BookingStatus.CancelledBySeller];

    public async Task<ReleaseSummary> RunAsync(CancellationToken ct = default)
    {
        var o = opt.Value; var now = time.UtcNow();
        int released = 0; long cents = 0;

        // sources with a positive pending balance
        var pendingOrders = await db.LedgerEntries.AsNoTracking().Where(e => e.Account == LedgerAccount.Pending && e.OrderId != null)
            .GroupBy(e => e.OrderId!.Value).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.AmountCents), Max = g.Max(x => x.Id) }).Where(x => x.Sum > 0).ToListAsync(ct);
        foreach (var p in pendingOrders)
        {
            var ord = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.Id, ct);
            if (ord is null || !Eligible(ord, now)) continue;
            if (await ReleaseAsync(ord.SellerId, ord.Currency, p.Id, null, p.Sum, p.Max, ord.Number, ct)) { released++; cents += p.Sum; }
        }
        var pendingBookings = await db.LedgerEntries.AsNoTracking().Where(e => e.Account == LedgerAccount.Pending && e.BookingId != null)
            .GroupBy(e => e.BookingId!.Value).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.AmountCents), Max = g.Max(x => x.Id), Cur = g.Max(x => x.Currency)! }).Where(x => x.Sum > 0).ToListAsync(ct);
        foreach (var p in pendingBookings)
        {
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.Id, ct);
            if (b is null || !ReleasableBooking.Contains(b.Status)) continue;
            var anchor = b.Status == BookingStatus.Completed ? b.CompletedAt ?? b.UpdatedAt : b.UpdatedAt;
            if (anchor.AddDays(o.ReleaseDays) > now) continue;
            if (await ReleaseAsync(b.SellerId, p.Cur, null, p.Id, p.Sum, p.Max, b.Number, ct)) { released++; cents += p.Sum; }
        }
        return new(released, await ReleaseReservesAsync(ct), cents);
    }

    private bool Eligible(SellerOrder o, DateTime now)
    {
        if (!ReleasableOrder.Contains(o.Status) || o.DeliveredAt is not { } d) return false;
        var days = opt.Value.ReleaseDays;
        if (opt.Value.RequireReturnWindowClosed) days = Math.Max(days, orders.Value.ReturnWindowDays);
        return d.AddDays(days) <= now;
    }

    private async Task<bool> ReleaseAsync(Guid seller, string currency, Guid? orderId, Guid? bookingId, long amount, long lastEntryId, string number, CancellationToken ct)
    {
        var o = opt.Value;
        var sellerRow = await db.Sellers.AsNoTracking().Where(s => s.Id == seller).Select(s => new { s.CreatedAt }).FirstOrDefaultAsync(ct);
        var isNew = sellerRow is not null && o.NewSellerReservePercent > 0 && sellerRow.CreatedAt.AddDays(o.NewSellerDays) > time.UtcNow();
        var reserve = isNew ? LedgerService.RoundHalfUp(amount * o.NewSellerReservePercent, 100) : 0;
        var key = $"release:{orderId ?? bookingId}:{lastEntryId}";
        var j = await ledger.PostAsync(seller, currency, "release", key, $"Released {number}", [
            new(LedgerAccount.Pending, -amount, "release"), new(LedgerAccount.Available, amount - reserve, "release"), new(LedgerAccount.Reserved, reserve, "reserve")],
            orderId, bookingId);
        return j is not null && await ledger.SaveAsync(ct);
    }

    private async Task<int> ReleaseReservesAsync(CancellationToken ct)
    {
        var o = opt.Value; var cutoff = time.UtcNow().AddDays(-o.ReserveReleaseDays);
        var reserves = await db.LedgerEntries.AsNoTracking().Where(e => e.Account == LedgerAccount.Reserved && e.AmountCents > 0 && e.Kind == "reserve" && e.CreatedAt <= cutoff)
            .OrderBy(e => e.Id).Take(500).ToListAsync(ct);
        var n = 0;
        foreach (var r in reserves)
        {
            var j = await ledger.PostAsync(r.SellerId, r.Currency, "reserve_release", $"reserve-release:{r.Id}", "Reserve released",
                [new(LedgerAccount.Reserved, -r.AmountCents, "reserve"), new(LedgerAccount.Available, r.AmountCents, "reserve")], r.OrderId, r.BookingId);
            if (j is not null && await ledger.SaveAsync(ct)) n++;
        }
        return n;
    }
}

public record TransferSummary(int Created, int Sent, int Failed, int Paused, int Skipped);

/// <summary>
/// Batches available balances into one transfer per seller + currency + UTC day (unique idempotency key), then sends due transfers to Stripe with retries.
/// The funds are moved Available -> Settled in the SAME transaction that creates the transfer row, so they can never be sent twice or spent twice.
/// </summary>
public class TransferService(AppDbContext db, LedgerService ledger, IPaymentProvider provider, IOptions<PaymentOptions> opt, TimeProvider time,
    PaymentNotifier notifier, ILogger<TransferService> log)
{
    public const string JobName = "payments-transfers";

    public static async Task<(bool Paused, string? Why)> PausedAsync(AppDbContext db, Guid sellerId, PayoutAccount? acct, SellerStatus status)
    {
        if (status is SellerStatus.Suspended or SellerStatus.Closed or SellerStatus.Rejected) return (true, "account_suspended");
        if (await db.PayoutHolds.AnyAsync(h => h.SellerId == sellerId && h.ReleasedAt == null)) return (true, "staff_hold");
        if (acct is null) return (true, "no_payout_account");
        if (!acct.PayoutsEnabled) return (true, "payouts_disabled");
        return (false, null);
    }

    public async Task<TransferSummary> RunAsync(CancellationToken ct = default)
    {
        int created = 0, paused = 0, skipped = 0;
        var o = opt.Value; var now = time.UtcNow(); var day = now.Date;
        var sellers = await db.LedgerEntries.AsNoTracking().Where(e => e.Account == LedgerAccount.Available)
            .GroupBy(e => new { e.SellerId, e.Currency }).Select(g => new { g.Key.SellerId, g.Key.Currency, Sum = g.Sum(x => x.AmountCents) }).Where(x => x.Sum > 0).ToListAsync(ct);
        foreach (var s in sellers)
        {
            if (s.Sum < o.MinTransferCents) { skipped++; continue; }
            var acct = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.SellerId == s.SellerId, ct);
            var status = await db.Sellers.AsNoTracking().Where(x => x.Id == s.SellerId).Select(x => x.Status).FirstOrDefaultAsync(ct);
            if ((await PausedAsync(db, s.SellerId, acct, status)).Paused) { paused++; continue; }
            var key = $"{s.SellerId:N}:{s.Currency}:{day:yyyyMMdd}";
            if (await db.PaymentTransfers.AnyAsync(t => t.IdempotencyKey == key, ct)) { skipped++; continue; }
            var t = new PaymentTransfer
            {
                SellerId = s.SellerId, Currency = s.Currency, AmountCents = s.Sum, IdempotencyKey = key, BatchDate = day, DestinationAccountId = acct!.StripeAccountId,
                NextAttemptAt = now, CreatedAt = now, UpdatedAt = now
            };
            db.PaymentTransfers.Add(t);
            await ledger.PostAsync(s.SellerId, s.Currency, "transfer", $"transfer:{t.Id}", "Transfer to your Stripe account",
                [new(LedgerAccount.Available, -s.Sum, "transfer"), new(LedgerAccount.Settled, s.Sum, "transfer")], sourceRef: t.Id.ToString());
            if (await ledger.SaveAsync(ct)) created++; else skipped++;   // lost the race for this batch: someone else created it
        }
        var (sent, failed, pausedSend) = await SendDueAsync(ct);
        return new(created, sent, failed, paused + pausedSend, skipped);
    }

    public async Task<(int Sent, int Failed, int Paused)> SendDueAsync(CancellationToken ct = default)
    {
        int sent = 0, failed = 0, paused = 0;
        var o = opt.Value; var now = time.UtcNow();
        var due = await db.PaymentTransfers.AsNoTracking().Where(t => t.Status == TransferStatus.Pending && t.NextAttemptAt <= now && (t.LockedUntil == null || t.LockedUntil < now))
            .OrderBy(t => t.CreatedAt).Take(100).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in due)
        {
            var lease = time.UtcNow().AddMinutes(2);
            var claimed = await db.PaymentTransfers.Where(t => t.Id == id && t.Status == TransferStatus.Pending && (t.LockedUntil == null || t.LockedUntil < now))
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.LockedUntil, lease), ct);
            if (claimed != 1) continue;
            var t = await db.PaymentTransfers.AsNoTracking().FirstAsync(x => x.Id == id, ct);
            var acct = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.SellerId == t.SellerId, ct);
            var status = await db.Sellers.AsNoTracking().Where(x => x.Id == t.SellerId).Select(x => x.Status).FirstOrDefaultAsync(ct);
            if ((await PausedAsync(db, t.SellerId, acct, status)).Paused)
            {
                paused++;
                await db.PaymentTransfers.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedUntil, (DateTime?)null), CancellationToken.None);
                continue;
            }
            string? stripeId = null; string? error = null; var transient = true; string? stripeStatus = null;
            try
            {
                var r = await provider.CreateTransferAsync(new TransferRequest(t.AmountCents, t.Currency, acct!.StripeAccountId, "transfer-" + t.IdempotencyKey + (t.Retries > 0 ? "-r" + t.Retries : ""), "seller-" + t.SellerId.ToString("N"), t.Id), ct);
                stripeId = r.Id; stripeStatus = r.Status;
            }
            catch (ProviderException ex) { error = ex.Message; transient = ex.Transient; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = ex.GetType().Name; }
            await FinishAttemptAsync(t, stripeId, stripeStatus, error, transient);
            if (error is null) sent++; else if (await db.PaymentTransfers.AsNoTracking().AnyAsync(x => x.Id == id && x.Status == TransferStatus.Failed, ct)) failed++;
        }
        return (sent, failed, paused);
    }

    private async Task FinishAttemptAsync(PaymentTransfer t, string? stripeId, string? stripeStatus, string? error, bool transient)
    {
        var o = opt.Value; var now = time.UtcNow();
        var attempts = t.Attempts + 1;
        if (error is null)
        {
            // never overwrite a state a webhook already moved forward (reversed)
            await db.PaymentTransfers.Where(x => x.Id == t.Id && x.Status == TransferStatus.Pending).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, TransferStatus.Created).SetProperty(x => x.StripeTransferId, stripeId).SetProperty(x => x.Attempts, attempts)
                .SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.UpdatedAt, now), CancellationToken.None);
            await db.PaymentTransfers.Where(x => x.Id == t.Id && x.StripeTransferId == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.StripeTransferId, stripeId), CancellationToken.None);
            return;
        }
        var final = !transient || attempts >= o.TransferMaxAttempts;
        if (!final)
        {
            var next = now.AddSeconds(Math.Min(3600, o.TransferBackoffSeconds * Math.Pow(2, attempts - 1)));
            await db.PaymentTransfers.Where(x => x.Id == t.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Attempts, attempts).SetProperty(x => x.LastError, Trunc(error))
                .SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.UpdatedAt, now), CancellationToken.None);
            return;
        }
        // permanent failure: the funds go back to the available balance (compensating journal) and staff can retry
        db.ChangeTracker.Clear();
        var row = await db.PaymentTransfers.FirstAsync(x => x.Id == t.Id);
        row.Status = TransferStatus.Failed; row.Attempts = attempts; row.LastError = Trunc(error); row.LockedUntil = null; row.UpdatedAt = now;
        await ledger.PostAsync(t.SellerId, t.Currency, "transfer_failed", $"transfer-failed:{t.Id}:{t.Retries}", "Transfer failed, funds returned",
            [new(LedgerAccount.Settled, -t.AmountCents, "transfer"), new(LedgerAccount.Available, t.AmountCents, "transfer")], sourceRef: t.Id.ToString());
        await ledger.SaveAsync();
        log.LogWarning("Transfer {Id} failed permanently after {N} attempts", t.Id, attempts);
        await notifier.NotifyAsync(t.SellerId, $"transfer-failed:{t.Id}:{t.Retries}", "A transfer to your Stripe account failed", "A transfer failed",
            [$"We could not move {PaymentNotifier.Money(t.AmountCents, t.Currency)} to your Stripe account. The money is still yours and our team has been alerted."]);
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>Staff retry of a Failed transfer: re-moves the funds Available -> Settled and queues the send again (distinct journal key + Stripe key per retry).</summary>
    public async Task<(bool Ok, string Code)> RetryAsync(Guid transferId)
    {
        var t = await db.PaymentTransfers.FirstOrDefaultAsync(x => x.Id == transferId);
        if (t is null) return (false, "not_found");
        if (t.Status != TransferStatus.Failed) return (false, "not_failed");
        var avail = (await ledger.BalanceAsync(t.SellerId, t.Currency)).Available;
        if (avail < t.AmountCents) return (false, "insufficient_available");
        var claimed = await db.PaymentTransfers.Where(x => x.Id == transferId && x.Status == TransferStatus.Failed && x.Retries == t.Retries)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, TransferStatus.Pending).SetProperty(x => x.Retries, t.Retries + 1).SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.NextAttemptAt, time.UtcNow()).SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.UpdatedAt, time.UtcNow()));
        if (claimed != 1) return (false, "conflict");
        db.ChangeTracker.Clear();
        await ledger.PostAsync(t.SellerId, t.Currency, "transfer", $"transfer-retry:{t.Id}:{t.Retries + 1}", "Transfer retried",
            [new(LedgerAccount.Available, -t.AmountCents, "transfer"), new(LedgerAccount.Settled, t.AmountCents, "transfer")], sourceRef: t.Id.ToString());
        await ledger.SaveAsync();
        return (true, "ok");
    }
}

public record ReconcileSummary(int Checked, int Flagged);

/// <summary>Compares the ledger's transfers with Stripe (the fake provider in tests) and flags mismatches for staff. Re-running never duplicates an open flag.</summary>
public class ReconciliationService(AppDbContext db, IPaymentProvider provider, TimeProvider time)
{
    public async Task<ReconcileSummary> RunAsync(CancellationToken ct = default)
    {
        var now = time.UtcNow(); int checkedN = 0, flagged = 0;
        var transfers = await db.PaymentTransfers.AsNoTracking().Where(t => t.Status == TransferStatus.Created || t.Status == TransferStatus.Reversed).ToListAsync(ct);
        foreach (var t in transfers)
        {
            checkedN++;
            string? problem = null;
            if (t.StripeTransferId is null) problem = "ledger transfer has no Stripe transfer id";
            else
            {
                var r = await provider.RetrieveTransferAsync(t.StripeTransferId, ct);
                if (r is null) problem = $"Stripe has no transfer {t.StripeTransferId}";
                else if (r.AmountCents != t.AmountCents || r.Currency != t.Currency) problem = $"amount differs: ledger {t.AmountCents} {t.Currency}, Stripe {r.AmountCents} {r.Currency}";
                else if (r.Destination != t.DestinationAccountId) problem = "destination account differs";
                else if (r.Status == "reversed" && t.Status != TransferStatus.Reversed) problem = "Stripe reversed the transfer but the ledger did not";
            }
            if (problem is null) continue;
            var kind = "transfer_mismatch";
            if (await db.ReconciliationFlags.AnyAsync(f => f.TransferId == t.Id && f.Kind == kind && f.ResolvedAt == null, ct)) continue;
            db.ReconciliationFlags.Add(new ReconciliationFlag { SellerId = t.SellerId, TransferId = t.Id, Kind = kind, Detail = problem, CreatedAt = now });
            flagged++;
        }
        // internal consistency: every journal balances to zero
        var unbalanced = await db.LedgerEntries.AsNoTracking().GroupBy(e => e.JournalId).Select(g => new { g.Key, Sum = g.Sum(x => x.AmountCents) }).Where(x => x.Sum != 0).Select(x => x.Key).ToListAsync(ct);
        foreach (var jid in unbalanced)
            if (!await db.ReconciliationFlags.AnyAsync(f => f.Kind == "unbalanced_journal" && f.Detail.Contains(jid.ToString()) && f.ResolvedAt == null, ct))
            { db.ReconciliationFlags.Add(new ReconciliationFlag { Kind = "unbalanced_journal", Detail = "Journal " + jid + " does not sum to zero", CreatedAt = now }); flagged++; }
        await db.SaveChangesAsync(ct);
        return new(checkedN, flagged);
    }
}

/// <summary>Background worker: leases the job, releases funds, sends transfers, reconciles. Skipped in Testing (tests call the services directly).</summary>
public class PaymentsWorker(IServiceScopeFactory scopes, IOptions<PaymentOptions> opt, ILogger<PaymentsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var lease = sp.GetRequiredService<JobLeaseService>();
                if (await lease.TryAcquireAsync(TransferService.JobName, stop))
                {
                    try
                    {
                        var rec = sp.GetRequiredService<Solvelt.Api.Admin.JobRecorder>();
                        await rec.RunAsync("payments-release", async () => await sp.GetRequiredService<ReleaseService>().RunAsync(stop));
                        await rec.RunAsync("payments-transfers", async () => await sp.GetRequiredService<TransferService>().RunAsync(stop));
                        await rec.RunAsync("payments-reconcile", async () => await sp.GetRequiredService<ReconciliationService>().RunAsync(stop));
                    }
                    finally { await lease.ReleaseAsync(TransferService.JobName); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Payments background job failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(opt.Value.JobIntervalSeconds), stop); } catch (OperationCanceledException) { }
        }
    }
}
