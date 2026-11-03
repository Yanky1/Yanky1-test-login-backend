using Solvelt.Api.Services;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Endpoints;
using Solvelt.Api.Orders;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Payments;

public static class SellerPaymentEndpoints
{
    private static IResult Err(int status, string code, string message) =>
        Results.Json(new { type = "about:blank", title = message, status, code }, statusCode: status);

    private static readonly LedgerAccount[] SellerAccounts = [LedgerAccount.Pending, LedgerAccount.Available, LedgerAccount.Reserved, LedgerAccount.Settled, LedgerAccount.PaidOut];
    private static readonly LedgerAccount[] OwedAccounts = [LedgerAccount.Pending, LedgerAccount.Available, LedgerAccount.Reserved];

    public static object Money(long cents, string currency) => new { amountCents = cents, currency };

    public static string TransferState(TransferStatus s) => s switch { TransferStatus.Pending => "scheduled", TransferStatus.Created => "sent", TransferStatus.Failed => "failed", _ => "reversed" };

    /// <summary>What a journal did to the seller's balance (pending + available + reserved). Releases and payouts are movements and show 0.</summary>
    public static long OwedDelta(LedgerJournal j) => j.Entries.Where(e => OwedAccounts.Contains(e.Account)).Sum(e => e.AmountCents);

    private static async Task<object> Rows(AppDbContext db, List<LedgerJournal> journals)
    {
        var orderIds = journals.Where(j => j.OrderId != null).Select(j => j.OrderId!.Value).Distinct().ToList();
        var bookingIds = journals.Where(j => j.BookingId != null).Select(j => j.BookingId!.Value).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number);
        var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number);
        return journals.Select(j => new
        {
            id = j.Id, createdAt = j.CreatedAt, type = j.Type, memo = j.Memo, currency = j.Currency, amountCents = OwedDelta(j),
            orderId = j.OrderId, orderNumber = j.OrderId is { } o && orders.TryGetValue(o, out var on) ? on : null,
            bookingId = j.BookingId, bookingNumber = j.BookingId is { } b && bookings.TryGetValue(b, out var bn) ? bn : null,
            lines = j.Entries.Where(e => SellerAccounts.Contains(e.Account)).OrderBy(e => e.Id).Select(e => new { account = e.Account.ToString().ToLowerInvariant(), kind = e.Kind, amountCents = e.AmountCents })
        }).ToList();
    }

    public static IQueryable<LedgerJournal> Filter(IQueryable<LedgerJournal> q, string? type, DateTime? from, DateTime? to, Guid? orderId, Guid? bookingId)
    {
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(j => j.Type == type);
        if (from is { } f) q = q.Where(j => j.CreatedAt >= f.Date);
        if (to is { } t) q = q.Where(j => j.CreatedAt < t.Date.AddDays(1));
        if (orderId is { } o) q = q.Where(j => j.OrderId == o);
        if (bookingId is { } b) q = q.Where(j => j.BookingId == b);
        return q;
    }

    public static string Dec(long cents) => (cents < 0 ? "-" : "") + (Math.Abs(cents) / 100).ToString(CultureInfo.InvariantCulture) + "." + (Math.Abs(cents) % 100).ToString("D2", CultureInfo.InvariantCulture);

    public static void MapSellerPayments(this RouteGroupBuilder g)
    {
        var grp = g.MapGroup("/seller/payments").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequireRateLimiting("payments")
            .RequirePermission(Permission.PaymentsRead);
        var manage = (RouteHandlerBuilder b) => b.RequirePermission(Permission.PaymentsManage);

        // ---- balances ----
        grp.MapGet("/summary", async (HttpContext ctx, AppDbContext db, LedgerService ledger, OnboardingService onboarding, IOptions<PaymentOptions> opt, TimeProvider time) =>
        {
            var a = ctx.GetAccess()!;
            var balances = await ledger.BalancesAsync(a.SellerId);
            var acct = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.SellerId == a.SellerId);
            var (paused, why) = await TransferService.PausedAsync(db, a.SellerId, acct, a.AccountStatus);
            var now = time.UtcNow();
            var min = opt.Value.MinTransferCents;
            var pausedWhy = why == "staff_hold" ? "under_review" : why;   // the staff reason is never shown to sellers
            var openDisputes = await db.PaymentDisputes.CountAsync(d => d.SellerId == a.SellerId && (d.Status == "needs_response" || d.Status == "under_review"));
            return Results.Ok(new
            {
                balances = balances.Select(b => new
                {
                    currency = b.Currency, pendingCents = b.Pending, availableCents = b.Available, reservedCents = b.Reserved, inTransitCents = b.Settled, paidOutCents = b.PaidOut,
                    owedCents = b.Owed, negative = b.Negative,
                    nextTransferAt = !paused && b.Available >= min ? now.Date.AddDays(1) : (DateTime?)null
                }),
                payoutsPaused = paused, pausedReason = pausedWhy, minTransferCents = min, releaseDays = opt.Value.ReleaseDays, openDisputes,
                onboarding = await onboarding.StatusAsync(a.SellerId)
            });
        });

        // ---- ledger ----
        grp.MapGet("/ledger", async (HttpContext ctx, AppDbContext db, string? type, DateTime? from, DateTime? to, Guid? orderId, Guid? bookingId, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!;
            var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = Filter(db.LedgerJournals.AsNoTracking().Where(j => j.SellerId == a.SellerId), type, from, to, orderId, bookingId);
            var total = await q.CountAsync();
            var rows = await q.Include(j => j.Entries).OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = await Rows(db, rows) });
        });

        grp.MapGet("/ledger/{id:guid}", async (HttpContext ctx, AppDbContext db, Guid id) =>
        {
            var a = ctx.GetAccess()!;
            var j = await db.LedgerJournals.AsNoTracking().Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id && x.SellerId == a.SellerId);
            return j is null ? Results.NotFound() : Results.Ok(((IEnumerable<object>)await Rows(db, [j])).First());
        });

        // ---- transfers, payouts, disputes ----
        grp.MapGet("/transfers", async (HttpContext ctx, AppDbContext db, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!; var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PaymentTransfers.AsNoTracking().Where(t => t.SellerId == a.SellerId);
            var rows = await q.OrderByDescending(t => t.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = p, pageSize = size, items = rows.Select(t => new { id = t.Id, createdAt = t.CreatedAt, amountCents = t.AmountCents, currency = t.Currency, status = TransferState(t.Status), batchDate = t.BatchDate }) });
        });

        grp.MapGet("/payouts", async (HttpContext ctx, AppDbContext db, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!; var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PaymentPayouts.AsNoTracking().Where(t => t.SellerId == a.SellerId);
            var rows = await q.OrderByDescending(t => t.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total = await q.CountAsync(), page = p, pageSize = size, items = rows.Select(t => new { id = t.Id, createdAt = t.CreatedAt, amountCents = t.AmountCents, currency = t.Currency, status = t.Status, arrivalDate = t.ArrivalDate, failureCode = t.FailureCode }) });
        });

        grp.MapGet("/disputes", async (HttpContext ctx, AppDbContext db, int? page, int? pageSize) =>
        {
            var a = ctx.GetAccess()!; var (p, size) = AdminHttp.Paging(page, pageSize);
            var q = db.PaymentDisputes.AsNoTracking().Where(d => d.SellerId == a.SellerId);
            var rows = await q.OrderByDescending(d => d.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            var orderIds = rows.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).ToList();
            var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number);
            var bookingIds = rows.Where(r => r.BookingId != null).Select(r => r.BookingId!.Value).ToList();
            var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number);
            return Results.Ok(new
            {
                total = await q.CountAsync(), page = p, pageSize = size,
                items = rows.Select(d => new
                {
                    id = d.Id, createdAt = d.CreatedAt, amountCents = d.AmountCents, feeCents = d.FeeCents, currency = d.Currency, status = d.Status, reason = d.Reason,
                    evidenceDueBy = d.EvidenceDueBy, closedAt = d.ClosedAt, orderId = d.OrderId, orderNumber = d.OrderId is { } o && orders.TryGetValue(o, out var on) ? on : null,
                    bookingId = d.BookingId, bookingNumber = d.BookingId is { } b && bookings.TryGetValue(b, out var bn) ? bn : null,
                    canRespond = d.Status == "needs_response"   // answer in the Stripe Express dashboard (POST /seller/payments/dashboard-link, Owners)
                })
            });
        });

        // ---- statements ----
        grp.MapGet("/statements", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = ctx.GetAccess()!;
            var dates = await db.LedgerJournals.AsNoTracking().Where(j => j.SellerId == a.SellerId).Select(j => j.CreatedAt).ToListAsync();
            return Results.Ok(new { months = dates.Select(d => d.ToString("yyyy-MM", CultureInfo.InvariantCulture)).Distinct().OrderDescending() });
        });

        grp.MapGet("/statements/{month}", async (HttpContext ctx, AppDbContext db, string month) =>
        {
            var a = ctx.GetAccess()!;
            if (!TryMonth(month, out var start)) return Err(422, "invalid_month", "Use YYYY-MM.");
            var st = await Statement(db, a.SellerId, start);
            return Results.Ok(st);
        });

        grp.MapGet("/statements/{month}/csv", async (HttpContext ctx, AppDbContext db, string month) =>
        {
            var a = ctx.GetAccess()!;
            if (!TryMonth(month, out var start)) return Err(422, "invalid_month", "Use YYYY-MM.");
            var end = start.AddMonths(1);
            var js = await db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.SellerId == a.SellerId && j.CreatedAt >= start && j.CreatedAt < end).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync();
            var orderIds = js.Where(j => j.OrderId != null).Select(j => j.OrderId!.Value).ToList(); var bookingIds = js.Where(j => j.BookingId != null).Select(j => j.BookingId!.Value).ToList();
            var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number);
            var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number);
            var sb = new StringBuilder();
            sb.Append("date,type,description,reference,currency,amount\r\n");
            foreach (var j in js)
            {
                var reference = j.OrderId is { } o && orders.TryGetValue(o, out var on) ? on : j.BookingId is { } b && bookings.TryGetValue(b, out var bn) ? bn : "";
                // text cells are formula-neutralised by Csv.Cell; the amount is a plain decimal number written unquoted
                sb.Append(Csv.Cell(j.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(',').Append(Csv.Cell(j.Type)).Append(',').Append(Csv.Cell(j.Memo)).Append(',')
                  .Append(Csv.Cell(reference)).Append(',').Append(Csv.Cell(j.Currency)).Append(',').Append(Dec(OwedDelta(j))).Append("\r\n");
            }
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"statement-{month}.csv\"";
            return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
        });

        // ---- onboarding (Owner only for the actions) ----
        grp.MapGet("/onboarding", async (HttpContext ctx, OnboardingService svc) => Results.Ok(await svc.StatusAsync(ctx.GetAccess()!.SellerId)));

        manage(grp.MapPost("/onboarding", async (HttpContext ctx, OnboardingService svc) =>
        {
            var a = ctx.GetAccess()!;
            if (a.AccountStatus is not (SellerStatus.Approved or SellerStatus.Live)) return Err(409, "not_approved", "Payouts can be set up once your seller account is approved.");
            try
            {
                var (_, link) = await svc.StartAsync(a.SellerId, a.UserId, ctx.RequestAborted);
                return Results.Ok(new { url = link.Url, expiresAt = link.ExpiresAt, onboarding = await svc.StatusAsync(a.SellerId) });
            }
            catch (ProviderException ex) { return ProviderError(ex); }
        }));

        manage(grp.MapPost("/onboarding/sync", async (HttpContext ctx, OnboardingService svc) =>
        {
            try { return Results.Ok(await svc.SyncAsync(ctx.GetAccess()!.SellerId, ctx.RequestAborted)); }
            catch (ProviderException ex) { return ProviderError(ex); }
        }));

        manage(grp.MapPost("/dashboard-link", async (HttpContext ctx, OnboardingService svc) =>
        {
            try
            {
                var link = await svc.DashboardAsync(ctx.GetAccess()!.SellerId, ctx.RequestAborted);
                return link is null ? Err(409, "onboarding_incomplete", "Finish the payout setup first.") : Results.Ok(new { url = link.Url, expiresAt = link.ExpiresAt });
            }
            catch (ProviderException ex) { return ProviderError(ex); }
        }));
    }

    private static IResult ProviderError(ProviderException ex) =>
        ex is UnconfiguredException ? Err(503, "payments_not_configured", "Payments are not configured on this server.") : Err(502, "provider_error", "The payment provider could not complete the request. Try again.");

    private static bool TryMonth(string m, out DateTime start) =>
        DateTime.TryParseExact(m, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);

    public static async Task<object> Statement(AppDbContext db, Guid sellerId, DateTime start)
    {
        var end = start.AddMonths(1);
        var before = await db.LedgerEntries.AsNoTracking().Where(e => e.SellerId == sellerId && e.CreatedAt < start && OwedAccounts.Contains(e.Account)).GroupBy(e => e.Currency).Select(g => new { c = g.Key, s = g.Sum(x => x.AmountCents) }).ToListAsync();
        var js = await db.LedgerJournals.AsNoTracking().Include(j => j.Entries).Where(j => j.SellerId == sellerId && j.CreatedAt >= start && j.CreatedAt < end).ToListAsync();
        var currencies = js.Select(j => j.Currency).Concat(before.Select(b => b.c)).Distinct().OrderBy(c => c);
        return new
        {
            month = start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            currencies = currencies.Select(c =>
            {
                var mine = js.Where(j => j.Currency == c).ToList();
                var opening = before.FirstOrDefault(b => b.c == c)?.s ?? 0;
                long Sum(params string[] types) => mine.Where(j => types.Contains(j.Type)).Sum(OwedDelta);
                var sales = mine.Where(j => j.Type is "sale" or "policy_fee" or "extra_charge").SelectMany(j => j.Entries).Where(e => e.Account == LedgerAccount.Pending && e.Kind is "items" or "shipping").Sum(e => e.AmountCents);
                var fees = mine.Where(j => j.Type is "sale" or "policy_fee" or "extra_charge").SelectMany(j => j.Entries).Where(e => e.Account == LedgerAccount.Pending && e.Kind == "fee").Sum(e => e.AmountCents);
                var closing = opening + mine.Sum(OwedDelta);
                return new
                {
                    currency = c, openingCents = opening, salesCents = sales, feesCents = fees, refundsCents = Sum("refund"), disputesCents = Sum("dispute", "dispute_fee", "dispute_won"),
                    adjustmentsCents = Sum("adjustment"), transfersCents = Sum("transfer", "transfer_failed", "transfer_reversed"), closingCents = closing,
                    paidOutCents = mine.Where(j => j.Type is "payout" or "payout_reversed").SelectMany(j => j.Entries).Where(e => e.Account == LedgerAccount.PaidOut).Sum(e => e.AmountCents)
                };
            })
        };
    }
}
