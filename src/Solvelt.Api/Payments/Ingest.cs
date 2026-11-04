using Solvelt.Api.Services;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Payments;

public record MarketplacePaymentEvent(string? EventId, string? Type, JsonElement Data);
public record IngestResult(int Status, string Code, string Message, object? Body = null)
{
    public static IngestResult Ok(object? body = null, bool duplicate = false) => new(200, duplicate ? "duplicate" : "ok", "", body);
    public static IngestResult Err(int status, string code, string message) => new(status, code, message);
}

/// <summary>
/// Marketplace -> portal payment events (docs/PAYMENTS.md section 4): payment.captured, refund.processed, dispute.created/updated/closed.
/// Idempotent on the event id AND on the source ids of every journal; a retried event after a crash can never post twice.
/// </summary>
public class PaymentIngestService(AppDbContext db, LedgerService ledger, PlatformSettings settings, PaymentNotifier notifier, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<PaymentOptions> opt, PaymentTestHooks hooks)
{
    private static readonly JsonSerializerOptions J = OrderJson.Opts;
    private const long MaxCents = 1_000_000_000;
    private static readonly string[] CaptureKinds = ["order", "booking", "booking_cancellation_fee", "booking_no_show_fee", "booking_extra_charge"];

    public async Task<IngestResult> HandleAsync(MarketplacePaymentEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.EventId) || ev.EventId.Length > 100) return IngestResult.Err(422, "invalid_event", "eventId is required (at most 100 characters).");
        if (ev.Data.ValueKind != JsonValueKind.Object) return IngestResult.Err(422, "invalid_event", "data must be an object.");
        if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Source == "marketplace" && p.EventId == ev.EventId)) return IngestResult.Ok(duplicate: true);
        IngestResult r;
        // refunds and disputes read balances (pending left, existing debits) and write against them: serializable, so a release committing at the same moment can not
        // leave an order's Pending negative; on PostgreSQL the loser gets a serialization failure -> 409 and the sender retries
        var serializable = ev.Type is "refund.processed" or "dispute.created" or "dispute.updated" or "dispute.closed";
        await using var tx = serializable ? await hooks.BeginSerializableAsync(db) : null;
        bool saved;
        try
        {
            if (hooks.InHandler is { } ih) await ih(ev.Type ?? "");
            r = ev.Type switch
            {
                "payment.captured" => await CapturedAsync(ev),
                "refund.processed" => await RefundAsync(ev),
                "dispute.created" or "dispute.updated" or "dispute.closed" => await DisputeAsync(ev),
                _ => IngestResult.Err(422, "unknown_type", "Unknown event type.")
            };
            if (r.Status != 200) { db.ChangeTracker.Clear(); return r; }
            db.ProcessedPaymentEvents.Add(new ProcessedPaymentEvent { Source = "marketplace", EventId = ev.EventId, Type = ev.Type!, ReceivedAt = time.UtcNow() });
            saved = await ledger.SaveAsync();
            if (saved && tx is not null) await hooks.CommitAsync(tx);   // a PostgreSQL serialization failure (40001) can surface here, at COMMIT
        }
        catch (JsonException) { db.ChangeTracker.Clear(); return IngestResult.Err(422, "invalid_event", "data does not have the expected shape."); }
        catch (Exception ex) when (ex is DbUpdateException || DbErrors.IsSerialization(ex))
        {
            // a serialization failure from a read, an ExecuteUpdate, the save or the commit: nothing was applied, the sender retries.
            // NO rollback here: after a failed COMMIT the transaction is already completed (Npgsql: "This NpgsqlTransaction has completed"); disposing it cleans up.
            db.ChangeTracker.Clear();
            return IngestResult.Err(409, "concurrent_update", "A concurrent change prevented this event from being stored. Retry it.");
        }
        if (!saved)
        {
            // on PostgreSQL the failed statement aborted the transaction (25P02): roll back BEFORE any further query (still active here, so this is safe)
            if (tx is not null) { try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* already completed */ } }
            // only a conflict on the processed-event key means "another delivery of this very event won"; any other unique conflict (e.g. a dispute created by a concurrent event)
            // is NOT acknowledged: the sender gets a retryable 409 and re-sends, so the event is never lost
            if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Source == "marketplace" && p.EventId == ev.EventId)) return IngestResult.Ok(duplicate: true);
            return IngestResult.Err(409, "concurrent_update", "A concurrent change prevented this event from being stored. Retry it.");
        }
        await AfterAsync();
        return r;
    }

    private readonly List<(Guid Seller, long Before, string Currency)> _negativeChecks = [];

    private async Task AfterSaveAsync()
    {
        foreach (var (seller, before, cur) in _negativeChecks)
        {
            var after = await ledger.BalanceAsync(seller, cur);
            if (before >= 0 && after.Negative)
                await notifier.NotifyAsync(seller, $"negative:{cur}:{Guid.NewGuid():N}", "Your seller balance is negative", "Your balance is negative",
                    [$"A refund or dispute took your balance to {PaymentNotifier.Money(after.Owed, cur)}. It will be recovered automatically from your future sales before the next transfer."]);
        }
        _negativeChecks.Clear();
    }

    private async Task TrackNegativeAsync(Guid seller, string currency) => _negativeChecks.Add((seller, (await ledger.BalanceAsync(seller, currency)).Owed, currency));

    // ---- payment.captured ------------------------------------------------------------------------------------------------------
    public record CapturedData(string? Kind, string? MarketplaceOrderId, string? SellerOrderNumber, string? MarketplaceBookingId, string? PaymentRef, string? Currency, long? AmountCents, DateTime? CapturedAt);

    private static bool ValidCurrency(string? c) => c is { Length: 3 } && c.All(char.IsAsciiLetterUpper);

    private async Task<IngestResult> CapturedAsync(MarketplacePaymentEvent ev)
    {
        var d = ev.Data.Deserialize<CapturedData>(J)!;
        var kind = d.Kind ?? "order";
        if (!CaptureKinds.Contains(kind)) return IngestResult.Err(422, "invalid_kind", "kind must be one of " + string.Join(", ", CaptureKinds) + ".");
        if (string.IsNullOrWhiteSpace(d.PaymentRef) || d.PaymentRef.Length > 120) return IngestResult.Err(422, "invalid_payment_ref", "paymentRef (Stripe charge or payment intent id) is required.");
        var now = time.UtcNow();
        var results = new List<object>();
        if (kind == "order")
        {
            if (string.IsNullOrWhiteSpace(d.MarketplaceOrderId)) return IngestResult.Err(422, "invalid_event", "marketplaceOrderId is required.");
            var orders = await FindOrders(d.MarketplaceOrderId, d.SellerOrderNumber);
            if (orders.Count == 0) return IngestResult.Err(404, "order_not_found", "No such order (yet). Retry after the order was ingested.");
            if (ValidCurrency(d.Currency) && orders.Any(o => o.Currency != d.Currency)) return IngestResult.Err(422, "currency_mismatch", "The currency does not match the order.");
            foreach (var o in orders)
            {
                var items = o.ItemsCents - o.DiscountCents;
                var j = await ledger.PostAsync(o.SellerId, o.Currency, "sale", $"sale:{o.Id}", $"Sale {o.Number}", [
                    new(LedgerAccount.Platform, -(items + o.ShippingCents), "sale"),
                    new(LedgerAccount.Pending, items, "items"), new(LedgerAccount.Pending, o.ShippingCents, "shipping"),
                    new(LedgerAccount.Pending, -o.FeeCents, "fee"), new(LedgerAccount.Fees, o.FeeCents, "fee")], orderId: o.Id, sourceRef: d.PaymentRef);
                results.Add(new { sellerOrderId = o.Id, number = o.Number, recorded = j is not null });
            }
            AddRef(ev, d, "order", now);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(d.MarketplaceBookingId)) return IngestResult.Err(422, "invalid_event", "marketplaceBookingId is required.");
            if (!ValidCurrency(d.Currency)) return IngestResult.Err(422, "invalid_currency", "currency (ISO 4217, upper case) is required for bookings.");
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == d.MarketplaceBookingId);
            if (b is null) return IngestResult.Err(404, "booking_not_found", "No such booking (yet). Retry after the booking was created.");
            var bps = await settings.FeeBasisPointsAsync();
            if (kind == "booking")
            {
                var fee = LedgerService.RoundHalfUp(b.TotalCents * bps, 10_000);
                var j = await ledger.PostAsync(b.SellerId, d.Currency!, "sale", $"sale:{b.Id}", $"Booking {b.Number}", [
                    new(LedgerAccount.Platform, -b.TotalCents, "sale"), new(LedgerAccount.Pending, b.TotalCents, "items"),
                    new(LedgerAccount.Pending, -fee, "fee"), new(LedgerAccount.Fees, fee, "fee")], bookingId: b.Id, sourceRef: d.PaymentRef);
                results.Add(new { bookingId = b.Id, number = b.Number, recorded = j is not null });
            }
            else
            {
                var amount = d.AmountCents ?? 0;
                if (amount <= 0 || amount > MaxCents) return IngestResult.Err(422, "invalid_amount", "amountCents must be a positive integer.");
                var (type, cap, label) = kind switch
                {
                    "booking_extra_charge" => ("extra_charge", b.AdditionalChargeCents, "Extra charge"),
                    "booking_no_show_fee" => ("policy_fee", b.CancellationFeeCents, "No-show fee"),
                    _ => ("policy_fee", b.CancellationFeeCents, "Cancellation fee")
                };
                // cancellation and no-show fees share ONE cap (the booking ends either way); extra charges have their own
                var kinds = kind == "booking_extra_charge" ? new[] { kind } : new[] { "booking_cancellation_fee", "booking_no_show_fee" };
                var already = await db.LedgerEntries.Where(e => e.BookingId == b.Id && e.Account == LedgerAccount.Platform && kinds.Contains(e.Kind)).SumAsync(e => (long?)-e.AmountCents) ?? 0;
                if (already + amount > cap) return IngestResult.Err(422, "exceeds_booking", $"{label} of {amount} exceeds what the booking allows ({cap - already} left).");
                var fee = LedgerService.RoundHalfUp(amount * bps, 10_000);
                await ledger.PostAsync(b.SellerId, d.Currency!, type, $"{kind}:{ev.EventId}", $"{label} {b.Number}", [
                    new(LedgerAccount.Platform, -amount, kind), new(LedgerAccount.Pending, amount, "items"),
                    new(LedgerAccount.Pending, -fee, "fee"), new(LedgerAccount.Fees, fee, "fee")], bookingId: b.Id, sourceRef: d.PaymentRef);
                results.Add(new { bookingId = b.Id, number = b.Number, recorded = true });
            }
            AddRef(ev, d, kind, now);
        }
        return IngestResult.Ok(new { results });
    }

    private void AddRef(MarketplacePaymentEvent ev, CapturedData d, string kind, DateTime now) =>
        db.PaymentReferences.Add(new PaymentReference
        {
            Kind = kind, MarketplaceOrderId = d.MarketplaceOrderId, MarketplaceBookingId = d.MarketplaceBookingId, Ref = d.PaymentRef!,
            Currency = ValidCurrency(d.Currency) ? d.Currency! : "USD", CapturedAt = d.CapturedAt ?? now, EventId = ev.EventId!
        });

    private async Task<List<SellerOrder>> FindOrders(string marketplaceOrderId, string? number)
    {
        var q = db.Orders.AsNoTracking().Where(o => o.MarketplaceOrderId == marketplaceOrderId);
        if (!string.IsNullOrWhiteSpace(number)) q = q.Where(o => o.Number == number);
        return await q.OrderBy(o => o.Number).ToListAsync();
    }

    // ---- refund.processed ------------------------------------------------------------------------------------------------------
    public record RefundData(string? RefundId, string? MarketplaceOrderId, string? SellerOrderNumber, string? MarketplaceBookingId, long? AmountCents, long? ShippingCents, string? Currency, string? Reason);

    private async Task<IngestResult> RefundAsync(MarketplacePaymentEvent ev)
    {
        var d = ev.Data.Deserialize<RefundData>(J)!;
        if (string.IsNullOrWhiteSpace(d.RefundId) || d.RefundId.Length > 100) return IngestResult.Err(422, "invalid_event", "refundId is required.");
        long items = d.AmountCents ?? 0, ship = d.ShippingCents ?? 0;
        if (items < 0 || ship < 0 || items > MaxCents || ship > MaxCents || items + ship == 0) return IngestResult.Err(422, "invalid_amount", "amountCents / shippingCents must be non-negative integers, at least one above zero.");

        Guid seller; string currency; Guid? orderId = null, bookingId = null; string number;
        if (!string.IsNullOrWhiteSpace(d.MarketplaceOrderId))
        {
            var orders = await FindOrders(d.MarketplaceOrderId, d.SellerOrderNumber);
            if (orders.Count == 0) return IngestResult.Err(404, "order_not_found", "No such order.");
            if (orders.Count > 1) return IngestResult.Err(422, "ambiguous_order", "The marketplace order has several seller orders: send sellerOrderNumber.");
            seller = orders[0].SellerId; currency = orders[0].Currency; orderId = orders[0].Id; number = orders[0].Number;
        }
        else if (!string.IsNullOrWhiteSpace(d.MarketplaceBookingId))
        {
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == d.MarketplaceBookingId);
            if (b is null) return IngestResult.Err(404, "booking_not_found", "No such booking.");
            seller = b.SellerId; bookingId = b.Id; number = b.Number;
            var cur = await db.LedgerJournals.Where(j => j.BookingId == b.Id).Select(j => j.Currency).FirstOrDefaultAsync();
            currency = cur ?? (ValidCurrency(d.Currency) ? d.Currency! : "USD");
        }
        else return IngestResult.Err(422, "invalid_event", "marketplaceOrderId or marketplaceBookingId is required.");
        if (ValidCurrency(d.Currency) && d.Currency != currency) return IngestResult.Err(422, "currency_mismatch", "The currency does not match the sale.");

        var sale = await db.LedgerJournals.AsNoTracking().FirstOrDefaultAsync(j => j.IdempotencyKey == "sale:" + (orderId ?? bookingId));
        if (sale is null) return IngestResult.Err(409, "sale_not_recorded", "The payment for this order/booking has not been recorded yet (send payment.captured first). Retry later.");
        var key = $"refund:{d.RefundId}";
        if (await ledger.ExistsAsync(key)) return IngestResult.Ok(new { recorded = false });

        var saleEntries = await db.LedgerEntries.AsNoTracking().Where(e => e.JournalId == sale.Id).ToListAsync();
        long saleItems = saleEntries.Where(e => e.Account == LedgerAccount.Pending && e.Kind == "items").Sum(e => e.AmountCents);
        long saleShip = saleEntries.Where(e => e.Account == LedgerAccount.Pending && e.Kind == "shipping").Sum(e => e.AmountCents);
        long saleFee = saleEntries.Where(e => e.Account == LedgerAccount.Fees).Sum(e => e.AmountCents);
        long doneItems = await ledger.SumAsync(orderId, bookingId, LedgerAccount.Platform, "refund_items");
        long doneShip = await ledger.SumAsync(orderId, bookingId, LedgerAccount.Platform, "refund_shipping");
        long doneFee = -await ledger.SumAsync(orderId, bookingId, LedgerAccount.Fees, "fee_refund");
        if (doneItems + items > saleItems || doneShip + ship > saleShip)
            return IngestResult.Err(422, "exceeds_sale", $"The refund exceeds what was sold (items left {saleItems - doneItems}, shipping left {saleShip - doneShip}).");
        // The fee is refunded in proportion to the refunded items, rounded half up; the last refund takes the remainder so the fee is never over- or under-refunded.
        var feeBack = doneItems + items == saleItems ? saleFee - doneFee : Math.Min(saleFee - doneFee, LedgerService.RoundHalfUp(saleFee * items, saleItems));
        if (items == 0) feeBack = 0;
        var gross = items + ship;
        var debit = gross - feeBack;
        await TrackNegativeAsync(seller, currency);
        var pendingLeft = Math.Max(0, await ledger.PendingForAsync(orderId, bookingId));
        var fromPending = Math.Min(pendingLeft, debit);
        await ledger.PostAsync(seller, currency, "refund", key, $"Refund {number}", [
            new(LedgerAccount.Platform, items, "refund_items"), new(LedgerAccount.Platform, ship, "refund_shipping"), new(LedgerAccount.Fees, -feeBack, "fee_refund"),
            new(LedgerAccount.Pending, -fromPending, "refund"), new(LedgerAccount.Available, -(debit - fromPending), "refund")], orderId, bookingId, d.RefundId);
        return IngestResult.Ok(new { recorded = true, debitCents = debit, feeRefundedCents = feeBack, fromPendingCents = fromPending });
    }

    // ---- dispute.* -------------------------------------------------------------------------------------------------------------
    public record DisputeData(string? DisputeId, string? MarketplaceOrderId, string? SellerOrderNumber, string? MarketplaceBookingId, long? AmountCents, long? FeeCents,
        string? Currency, string? Reason, string? Status, DateTime? EvidenceDueBy, DateTime? CreatedAt);

    private async Task<IngestResult> DisputeAsync(MarketplacePaymentEvent ev)
    {
        var d = ev.Data.Deserialize<DisputeData>(J)!;
        if (string.IsNullOrWhiteSpace(d.DisputeId) || d.DisputeId.Length > 100) return IngestResult.Err(422, "invalid_event", "disputeId is required.");
        // One Stripe dispute can span several seller orders: the key is (disputeId, seller order) and every event carries that seller order's share.
        PaymentDispute? existing; Guid seller; string currency; Guid? orderId = null, bookingId = null; long? sale = null;
        if (!string.IsNullOrWhiteSpace(d.MarketplaceOrderId))
        {
            var orders = await FindOrders(d.MarketplaceOrderId, d.SellerOrderNumber);
            if (orders.Count == 0) return IngestResult.Err(404, "order_not_found", "No such order.");
            if (orders.Count > 1) return IngestResult.Err(422, "seller_order_required", "The marketplace order has several seller orders: send sellerOrderNumber (amountCents and feeCents are that seller order's share).");
            var o = orders[0];
            seller = o.SellerId; currency = o.Currency; orderId = o.Id; sale = o.ItemsCents - o.DiscountCents + o.ShippingCents;
            existing = await db.PaymentDisputes.FirstOrDefaultAsync(x => x.ExternalId == d.DisputeId && x.OrderId == o.Id);
        }
        else if (!string.IsNullOrWhiteSpace(d.MarketplaceBookingId))
        {
            var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == d.MarketplaceBookingId);
            if (b is null) return IngestResult.Err(404, "booking_not_found", "No such booking.");
            seller = b.SellerId; bookingId = b.Id;
            existing = await db.PaymentDisputes.FirstOrDefaultAsync(x => x.ExternalId == d.DisputeId && x.BookingId == b.Id);
            currency = existing?.Currency ?? (ValidCurrency(d.Currency) ? d.Currency! : "USD");
        }
        else
        {
            // no order reference: accepted only as a status update of a dispute that is already known by id alone
            var rows = await db.PaymentDisputes.Where(x => x.ExternalId == d.DisputeId).Take(2).ToListAsync();
            if (rows.Count > 1) return IngestResult.Err(422, "seller_order_required", "The dispute covers several seller orders: send marketplaceOrderId and sellerOrderNumber.");
            if (rows.Count == 0) return IngestResult.Err(422, "invalid_event", "marketplaceOrderId or marketplaceBookingId is required.");
            existing = rows[0]; seller = existing.SellerId; currency = existing.Currency; orderId = existing.OrderId; bookingId = existing.BookingId;
        }
        if (existing is not null) { seller = existing.SellerId; currency = existing.Currency; }
        var amount = existing?.AmountCents ?? d.AmountCents ?? 0;
        if (amount <= 0 || amount > MaxCents) return IngestResult.Err(422, "invalid_amount", "amountCents must be a positive integer.");
        if (existing is null && sale is { } s && amount > s) return IngestResult.Err(422, "exceeds_sale", "amountCents is above this seller order's sale (items plus shipping).");
        var fee = existing?.FeeCents ?? d.FeeCents ?? opt.Value.DefaultDisputeFeeCents;
        if (fee < 0 || fee > MaxCents) return IngestResult.Err(422, "invalid_amount", "feeCents must be a non-negative integer.");
        var status = (d.Status ?? (ev.Type == "dispute.created" ? "needs_response" : existing?.Status ?? "needs_response")).ToLowerInvariant();
        if (ev.Type == "dispute.closed" && status is not ("won" or "lost" or "warning_closed")) return IngestResult.Err(422, "invalid_status", "A closed dispute is won, lost or warning_closed.");
        if (status is not ("needs_response" or "under_review" or "won" or "lost" or "warning_closed" or "inquiry" or "inquiry_review")) return IngestResult.Err(422, "invalid_status", "Unknown dispute status.");
        var now = time.UtcNow();
        var evAt = d.CreatedAt ?? now;
        await ApplyDisputeAsync(existing, d.DisputeId, seller, currency, orderId, bookingId, amount, fee, status, d.Reason, d.EvidenceDueBy, evAt, now);
        await SyncUnallocatedAsync(d.DisputeId, now);   // a Stripe dispute that waited for its allocation is linked / cleared once the shares add up
        return IngestResult.Ok(new { disputeId = d.DisputeId, status, sellerOrderId = orderId });
    }

    /// <summary>The status / evidence part of a dispute update: event-time ordered, and a closed dispute is never reopened.</summary>
    public static void ApplyDisputeStatus(PaymentDispute row, string status, string? reason, DateTime? evidenceDue, DateTime eventAt, DateTime now)
    {
        var closedAlready = row.Status is "won" or "lost" or "warning_closed";
        if (eventAt >= row.EventAt && !(closedAlready && status is "needs_response" or "under_review" or "inquiry" or "inquiry_review"))
        {
            row.Status = status; row.EventAt = eventAt; row.UpdatedAt = now;
            if (reason is not null) row.Reason = reason;
            if (evidenceDue is not null) row.EvidenceDueBy = evidenceDue;
            if (status is "won" or "lost" or "warning_closed") row.ClosedAt ??= now;
        }
    }

    /// <summary>Shared by the marketplace events and the Stripe charge.dispute.* webhooks (single-seller charges). Order independent: the debit is posted whatever event comes first,
    /// and under one idempotency key per (dispute, seller order), so the two sources can never debit twice.</summary>
    public async Task ApplyDisputeAsync(PaymentDispute? row, string externalId, Guid seller, string currency, Guid? orderId, Guid? bookingId, long amount, long fee, string status,
        string? reason, DateTime? evidenceDue, DateTime eventAt, DateTime now)
    {
        var isNew = row is null;
        row ??= db.PaymentDisputes.Local.FirstOrDefault(x => x.ExternalId == externalId && x.OrderId == orderId && x.BookingId == bookingId);
        if (row is null)
        {
            row = new PaymentDispute { SellerId = seller, ExternalId = externalId, OrderId = orderId, BookingId = bookingId, Currency = currency, AmountCents = amount, FeeCents = fee, CreatedAt = now, EventAt = DateTime.MinValue };
            db.PaymentDisputes.Add(row);
        }
        ApplyDisputeStatus(row, status, reason, evidenceDue, eventAt, now);
        // a multi-seller Stripe event that arrived before this allocation only recorded its state: a newer Stripe state (won, lost, ...) applies to the new row too
        if (isNew && await db.UnallocatedDisputes.AsNoTracking().FirstOrDefaultAsync(x => x.ExternalId == externalId) is { } waiting)
            ApplyDisputeStatus(row, waiting.Status, waiting.Reason, waiting.EvidenceDueBy, waiting.EventAt, now);
        // An inquiry (Stripe warning_*) moves no money at Stripe, so nothing is debited for it or for its warning_closed; the debit + fee are posted once,
        // the first time the dispute reaches a state in which money moves (escalation to needs_response, or a marketplace event that is already past that).
        var moves = row.Status is not ("inquiry" or "inquiry_review" or "warning_closed");
        var sfx = "";
        if (moves)
        {
            sfx = await KeySuffixAsync(externalId, orderId, bookingId);
            if (isNew || !await ledger.ExistsAsync($"dispute:{externalId}{sfx}")) await TrackNegativeAsync(seller, currency);
            await PostDisputeDebitAsync(seller, currency, orderId, bookingId, amount, fee, externalId, sfx, row.Status);
        }
        if (moves && (isNew || row.Status == "needs_response")) _disputeMails.Add((seller, externalId + sfx, amount, currency, row.EvidenceDueBy));
        if (row.Status == "inquiry" && isNew) _inquiryMails.Add((seller, externalId + ":" + (orderId ?? bookingId), amount, currency, row.EvidenceDueBy));
    }

    /// <summary>Rows booked before the key became (dispute, seller order) keep their original idempotency keys ("dispute:{id}"), so a replay never posts twice.</summary>
    private async Task<string> KeySuffixAsync(string dp, Guid? orderId, Guid? bookingId)
    {
        var legacy = $"dispute:{dp}";
        var old = await db.LedgerJournals.AsNoTracking().AnyAsync(j => j.IdempotencyKey == legacy && j.OrderId == orderId && j.BookingId == bookingId);
        return old ? "" : ":" + (orderId ?? bookingId);
    }

    private async Task PostDisputeDebitAsync(Guid seller, string currency, Guid? orderId, Guid? bookingId, long amount, long fee, string externalId, string sfx, string status)
    {
        await ledger.PostAsync(seller, currency, "dispute", $"dispute:{externalId}{sfx}", "Dispute", [new(LedgerAccount.Platform, amount, "dispute"), new(LedgerAccount.Available, -amount, "dispute")], orderId, bookingId, externalId);
        await ledger.PostAsync(seller, currency, "dispute_fee", $"dispute-fee:{externalId}{sfx}", "Dispute fee", [new(LedgerAccount.Platform, fee, "dispute_fee"), new(LedgerAccount.Available, -fee, "dispute_fee")], orderId, bookingId, externalId);
        if (status == "won")
            await ledger.PostAsync(seller, currency, "dispute_won", $"dispute-won:{externalId}{sfx}", "Dispute won", [new(LedgerAccount.Platform, -amount, "dispute_won"), new(LedgerAccount.Available, amount, "dispute_won")], orderId, bookingId, externalId);
    }

    // ---- Stripe disputes on a charge that covers several seller orders ----------------------------------------------------------
    private async Task<long> AllocatedAsync(string dp)
    {
        var saved = await db.PaymentDisputes.AsNoTracking().Where(x => x.ExternalId == dp).Select(x => new { x.Id, x.AmountCents }).ToListAsync();
        var ids = saved.Select(x => x.Id).ToHashSet();
        return saved.Sum(x => x.AmountCents) + db.PaymentDisputes.Local.Where(x => x.ExternalId == dp && !ids.Contains(x.Id)).Sum(x => x.AmountCents);
    }

    /// <summary>Records what Stripe says about a multi-seller dispute WITHOUT moving money: status and evidence date go to every allocated row, and an open "unallocated" item
    /// (plus a reconciliation flag staff already look at) exists while the allocated shares are below the Stripe amount.</summary>
    public async Task<string> RecordStripeMultiSellerAsync(string dp, List<PaymentDispute> rows, string? marketplaceOrderId, string? chargeRef, string currency, long amount, string status,
        string? reason, DateTime? due, DateTime eventAt, DateTime now)
    {
        foreach (var r in rows) ApplyDisputeStatus(r, status, reason, due, eventAt, now);
        var item = db.UnallocatedDisputes.Local.FirstOrDefault(x => x.ExternalId == dp) ?? await db.UnallocatedDisputes.FirstOrDefaultAsync(x => x.ExternalId == dp);
        if (item is null)
        {
            if (await AllocatedAsync(dp) >= amount) return "applied_multi";
            item = new UnallocatedDispute { ExternalId = dp, MarketplaceOrderId = marketplaceOrderId, ChargeRef = chargeRef, Currency = currency, Status = status, Reason = reason, EvidenceDueBy = due, EventAt = eventAt, CreatedAt = now };
            db.UnallocatedDisputes.Add(item);
        }
        else
        {
            var closed = item.Status is "won" or "lost" or "warning_closed";
            if (eventAt >= item.EventAt && !(closed && status is "needs_response" or "under_review" or "inquiry" or "inquiry_review"))
            {
                item.Status = status; item.EventAt = eventAt;
                if (reason is not null) item.Reason = reason;
                if (due is not null) item.EvidenceDueBy = due;
            }
        }
        item.AmountCents = amount; item.Currency = currency; item.MarketplaceOrderId ??= marketplaceOrderId; item.ChargeRef ??= chargeRef;
        await SyncUnallocatedAsync(dp, now);
        return rows.Count > 0 ? "applied_multi" : "unallocated";
    }

    /// <summary>Opens or clears the unallocated item (and its flag) from the shares allocated so far. A staff resolution is final.</summary>
    public async Task SyncUnallocatedAsync(string dp, DateTime now)
    {
        var item = db.UnallocatedDisputes.Local.FirstOrDefault(x => x.ExternalId == dp) ?? await db.UnallocatedDisputes.FirstOrDefaultAsync(x => x.ExternalId == dp);
        if (item is null) return;
        var allocated = await AllocatedAsync(dp);
        item.AllocatedCents = allocated; item.UpdatedAt = now;
        var open = allocated < item.AmountCents;
        var byStaff = item.Note?.StartsWith("staff", StringComparison.Ordinal) == true;
        if (open && item.ResolvedAt is null && item.FlagId is not null) return;
        if (open && !byStaff)
        {
            var flag = new ReconciliationFlag { Kind = "dispute_unallocated", CreatedAt = now,
                Detail = $"Stripe dispute {dp}{(item.MarketplaceOrderId is { } mo ? $" on marketplace order {mo}" : "")} covers several seller orders: Stripe reports {item.AmountCents} {item.Currency} (minor units), {allocated} are allocated to seller orders. No seller was debited; waiting for the marketplace's per-seller-order dispute.* events." };
            db.ReconciliationFlags.Add(flag);
            item.FlagId = flag.Id; item.ResolvedAt = null; item.Note = null;
        }
        else if (!open && item.ResolvedAt is null)
        {
            item.ResolvedAt = now; item.Note = $"allocated {allocated} of {item.AmountCents}";
            if (item.FlagId is { } fid && await db.ReconciliationFlags.FirstOrDefaultAsync(f => f.Id == fid && f.ResolvedAt == null) is { } f) f.ResolvedAt = now;
        }
    }

    private readonly List<(Guid Seller, string Id, long Amount, string Currency, DateTime? Due)> _disputeMails = [];

    private readonly List<(Guid Seller, string Id, long Amount, string Currency, DateTime? Due)> _inquiryMails = [];

    public async Task FlushDisputeMailsAsync()
    {
        foreach (var (seller, id, amount, cur, due) in _inquiryMails.ToList())
            await notifier.NotifyAsync(seller, $"inquiry:{id}", "A buyer's bank asked about a payment", "A payment inquiry needs your response",
                [$"The bank of a buyer is asking about a payment of {PaymentNotifier.Money(amount, cur)}. No money has been taken from your balance, but you should respond" + (due is { } dd ? $" by {dd:yyyy-MM-dd}" : "") + " to avoid a formal dispute. Respond through your Stripe Express dashboard."]);
        _inquiryMails.Clear();
        foreach (var (seller, id, amount, cur, due) in _disputeMails.ToList())
            await notifier.NotifyAsync(seller, $"dispute:{id}", "A buyer opened a dispute", "A payment was disputed",
                [$"A buyer disputed a payment of {PaymentNotifier.Money(amount, cur)}. The amount and the dispute fee were debited from your balance." + (due is { } dd ? $" Evidence is due by {dd:yyyy-MM-dd}." : "") + " Respond through your Stripe Express dashboard."]);
        _disputeMails.Clear();
    }

    public async Task AfterAsync() { await AfterSaveAsync(); await FlushDisputeMailsAsync(); }
}
