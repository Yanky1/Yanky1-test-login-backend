using Solvelt.Api.Services;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;

namespace Solvelt.Api.Payments;

public record HoldRequest(string? Reason);
public record AdjustmentRequest(string? Direction, long? AmountCents, string? Currency, string? Reason, string? RequestId);

/// <summary>Staff payment tools under the admin API (docs/PAYMENTS.md section 9): every write needs a reason and is audited; adjustments need Super admin + step-up.</summary>
public static class PaymentAdminEndpoints
{
    public static void MapPaymentAdmin(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/admin/payments").RequireAllowedIp();

        p.AGet("/sellers/{id:guid}", async (Guid id, AppDbContext db, LedgerService ledger) =>
        {
            var seller = await db.Sellers.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.Id, s.VendorName, s.Status }).FirstOrDefaultAsync();
            if (seller is null) return Results.NotFound();
            var acct = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.SellerId == id);
            var holds = await db.PayoutHolds.AsNoTracking().Where(h => h.SellerId == id).OrderByDescending(h => h.PlacedAt).Take(20).ToListAsync();
            return Results.Ok(new
            {
                seller = new { seller.Id, seller.VendorName, status = seller.Status.ToString() },
                balances = (await ledger.BalancesAsync(id)).Select(b => new { currency = b.Currency, pendingCents = b.Pending, availableCents = b.Available, reservedCents = b.Reserved, inTransitCents = b.Settled, paidOutCents = b.PaidOut, owedCents = b.Owed, negative = b.Negative }),
                stripe = acct is null ? null : new
                {
                    stripeAccountId = acct.StripeAccountId, onboarding = OnboardingService.Map(acct, false), acct.LastEventAt, acct.UpdatedAt
                },
                activeHold = holds.FirstOrDefault(h => h.ReleasedAt == null) is { } h0 ? new { h0.Id, h0.Reason, h0.PlacedAt, h0.PlacedBy } : null,
                holds = holds.Select(h => new { h.Id, h.Reason, h.PlacedAt, h.PlacedBy, h.ReleasedAt, h.ReleasedBy, h.ReleaseReason }),
                failedTransfers = await db.PaymentTransfers.CountAsync(t => t.SellerId == id && t.Status == TransferStatus.Failed),
                openDisputes = await db.PaymentDisputes.CountAsync(d => d.SellerId == id && (d.Status == "needs_response" || d.Status == "under_review" || d.Status == "inquiry" || d.Status == "inquiry_review"))
            });
        }, StaffPermission.PaymentsView);

        p.AGet("/sellers/{id:guid}/ledger", async (Guid id, string? type, DateTime? from, DateTime? to, Guid? orderId, Guid? bookingId, string? reference, int? page, int? pageSize, AppDbContext db) =>
        {
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = SellerPaymentEndpoints.Filter(db, db.LedgerJournals.AsNoTracking().Where(j => j.SellerId == id), type, from, to, orderId, bookingId, reference);
            var rows = await q.Include(j => j.Entries).OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new
            {
                total = await q.CountAsync(), page = pg, pageSize = size,
                items = rows.Select(j => new
                {
                    j.Id, j.CreatedAt, j.Type, j.Memo, j.Currency, j.IdempotencyKey, j.OrderId, j.BookingId, j.SourceRef, j.ActorUserId, j.Reason,
                    entries = j.Entries.OrderBy(e => e.Id).Select(e => new { account = e.Account.ToString(), e.Kind, amountCents = e.AmountCents })
                })
            });
        }, StaffPermission.PaymentsView);

        p.APost("/sellers/{id:guid}/hold", async (Guid id, HoldRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            if (await db.PayoutHolds.AnyAsync(h => h.SellerId == id && h.ReleasedAt == null)) return AdminHttp.Err(409, "already_on_hold", "The payouts of this seller are already on hold.");
            var h = new PayoutHold { SellerId = id, Reason = reason, PlacedBy = ctx.Staff().UserId, PlacedAt = time.UtcNow() };
            db.PayoutHolds.Add(h);
            await audit.WriteAsync(ctx.Staff(), "payments.hold_placed", "seller", id, reason, null, new { holdId = h.Id });
            return Results.Ok(new { holdId = h.Id });
        }, StaffPermission.PaymentsHold);

        p.APost("/sellers/{id:guid}/hold/release", async (Guid id, HoldRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var now = time.UtcNow(); var by = ctx.Staff().UserId;
            var n = await db.PayoutHolds.Where(h => h.SellerId == id && h.ReleasedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(h => h.ReleasedAt, now).SetProperty(h => h.ReleasedBy, by).SetProperty(h => h.ReleaseReason, reason));
            if (n == 0) return AdminHttp.Err(409, "no_active_hold", "There is no active hold.");
            await audit.WriteAsync(ctx.Staff(), "payments.hold_released", "seller", id, reason);
            return Results.Ok(new { released = n });
        }, StaffPermission.PaymentsHold);

        p.APost("/sellers/{id:guid}/adjustments", async (Guid id, AdjustmentRequest r, HttpContext ctx, AppDbContext db, LedgerService ledger, AdminAudit audit, PaymentNotifier notifier) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var dir = (r.Direction ?? "").ToLowerInvariant();
            if (dir is not ("credit" or "debit")) return Validation.Invalid("direction", "Use credit or debit.");
            if (r.AmountCents is not (> 0 and <= 1_000_000_000)) return Validation.Invalid("amountCents", "Use a positive integer number of cents (at most 1,000,000,000).");
            if (r.Currency is not { Length: 3 } cur || !cur.All(char.IsAsciiLetterUpper)) return Validation.Invalid("currency", "Use an ISO 4217 code in upper case, e.g. USD.");
            if (r.RequestId is { Length: > 80 }) return Validation.Invalid("requestId", "At most 80 characters.");
            if (!await db.Sellers.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var amount = r.AmountCents.Value;
            var before = (await ledger.BalanceAsync(id, cur)).Owed;
            var signed = dir == "credit" ? amount : -amount;
            var j = await ledger.PostAsync(id, cur, "adjustment", "adjustment:" + (r.RequestId ?? Guid.NewGuid().ToString("N")), $"Adjustment by Solvelt ({dir})",
                [new(LedgerAccount.Available, signed, "adjustment"), new(LedgerAccount.Platform, -signed, "adjustment")], actor: ctx.Staff().UserId, reason: reason);
            if (j is null) return Results.Ok(new { recorded = false });
            audit.Add(ctx.Staff(), "payments.adjustment", "seller", id, reason, new { owedCents = before }, new { direction = dir, amountCents = amount, currency = cur, journalId = j.Id });
            if (!await ledger.SaveAsync()) return Results.Ok(new { recorded = false });
            await notifier.NotifyAsync(id, "adjustment:" + j.Id, "Solvelt adjusted your balance", "A balance adjustment was made",
                [$"Solvelt {(dir == "credit" ? "added" : "removed")} {PaymentNotifier.Money(amount, cur)} {(dir == "credit" ? "to" : "from")} your available balance.", "Contact support if you have questions about it."]);
            var after = (await ledger.BalanceAsync(id, cur)).Owed;
            if (before >= 0 && after < 0)
                await notifier.NotifyAsync(id, "negative:" + j.Id, "Your seller balance is negative", "Your balance is negative",
                    [$"Your balance is now {PaymentNotifier.Money(after, cur)}. It will be recovered automatically from your future sales before the next transfer."]);
            return Results.Ok(new { recorded = true, journalId = j.Id, owedCents = after });
        }, StaffPermission.PaymentsAdjust);

        p.AGet("/transfers", async (string? status, int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PaymentTransfers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<TransferStatus>(status, true, out var st)) return Validation.Invalid("status", "Use pending, created, failed or reversed.");
                q = q.Where(t => t.Status == st);
            }
            var rows = await q.OrderByDescending(t => t.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(t => new { t.Id, t.SellerId, t.AmountCents, t.Currency, status = t.Status.ToString(), t.Attempts, t.Retries, t.LastError, t.StripeTransferId, t.CreatedAt, t.NextAttemptAt }) });
        }, StaffPermission.PaymentsView);

        p.APost("/transfers/{id:guid}/retry", async (Guid id, HoldRequest r, HttpContext ctx, TransferService svc, AdminAudit audit) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var (ok, code) = await svc.RetryAsync(id);
            if (code == "not_found") return Results.NotFound();
            if (!ok) return AdminHttp.Err(409, code, code switch { "not_failed" => "Only failed transfers can be retried.", "insufficient_available" => "The seller's available balance no longer covers this transfer.", "busy" => "The transfer job is running right now. Try again in a minute.", _ => "The transfer changed at the same time. Reload." });
            await audit.WriteAsync(ctx.Staff(), "payments.transfer_retried", "transfer", id, reason);
            return Results.Ok(new { queued = true });
        }, StaffPermission.PaymentsRetry);

        p.AGet("/payouts", async (string? status, int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PaymentPayouts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            var rows = await q.OrderByDescending(t => t.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(t => new { t.Id, t.SellerId, t.StripePayoutId, t.AmountCents, t.Currency, t.Status, t.FailureCode, t.ArrivalDate, t.CreatedAt }) });
        }, StaffPermission.PaymentsView);

        p.AGet("/accounts/requirements-past-due", async (int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PayoutAccounts.AsNoTracking().Where(a => a.RequirementsPastDueJson != "[]" && a.RequirementsPastDueJson != "");
            var rows = await q.OrderBy(a => a.RequirementsDeadline).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(a => new { a.SellerId, a.StripeAccountId, pastDue = OnboardingService.Strs(a.RequirementsPastDueJson), deadline = a.RequirementsDeadline, a.DisabledReason, a.PayoutsEnabled }) });
        }, StaffPermission.PaymentsView);

        p.AGet("/reconciliation", async (bool? open, int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.ReconciliationFlags.AsNoTracking().AsQueryable();
            if (open ?? true) q = q.Where(f => f.ResolvedAt == null);
            var rows = await q.OrderByDescending(f => f.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows });
        }, StaffPermission.PaymentsView);

        // Stripe disputes on a charge that covers several seller orders and that the marketplace has not (fully) allocated yet: no seller was debited
        p.AGet("/unallocated-disputes", async (bool? open, int? page, int? pageSize, AppDbContext db) =>
        {
            var (pg, size) = AdminHttp.Paging(page, pageSize);
            var q = db.UnallocatedDisputes.AsNoTracking().AsQueryable();
            if (open ?? true) q = q.Where(u => u.ResolvedAt == null);
            var rows = await q.OrderByDescending(u => u.CreatedAt).Skip((pg - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = pg, pageSize = size, items = rows.Select(u => new { u.Id, disputeId = u.ExternalId, u.MarketplaceOrderId, u.ChargeRef, u.Currency, stripeAmountCents = u.AmountCents, allocatedCents = u.AllocatedCents,
                u.Status, u.Reason, u.EvidenceDueBy, u.FlagId, u.Note, u.CreatedAt, u.UpdatedAt, u.ResolvedAt }) });
        }, StaffPermission.PaymentsView);

        p.APost("/reconciliation/run", async (HttpContext ctx, ReconciliationService svc, AdminAudit audit) =>
        {
            var r = await svc.RunAsync();
            await audit.WriteAsync(ctx.Staff(), "payments.reconciliation_run", "payments", null, null, null, r);
            return Results.Ok(r);
        }, StaffPermission.PaymentsRetry);

        p.APost("/reconciliation/{id:guid}/resolve", async (Guid id, HoldRequest r, HttpContext ctx, AppDbContext db, AdminAudit audit, TimeProvider time) =>
        {
            if (AdminHttp.NeedReason(r.Reason, out var reason) is { } bad) return bad;
            var now = time.UtcNow(); var by = ctx.Staff().UserId;
            var n = await db.ReconciliationFlags.Where(f => f.Id == id && f.ResolvedAt == null).ExecuteUpdateAsync(u => u.SetProperty(f => f.ResolvedAt, now).SetProperty(f => f.ResolvedBy, by));
            if (n == 0) return Results.NotFound();
            await db.UnallocatedDisputes.Where(u => u.FlagId == id && u.ResolvedAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.Note, "staff: " + (reason.Length > 280 ? reason[..280] : reason)));
            await audit.WriteAsync(ctx.Staff(), "payments.reconciliation_resolved", "reconciliation", id, reason);
            return Results.Ok(new { resolved = true });
        }, StaffPermission.PaymentsRetry);
    }
}
