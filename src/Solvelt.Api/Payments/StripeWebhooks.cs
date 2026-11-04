using Solvelt.Api.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Payments;

/// <summary>Stripe webhook signature scheme: header <c>t=unix,v1=hex[,v1=hex]</c>, signed payload <c>{t}.{raw body}</c>, HMAC-SHA256 with the endpoint secret.</summary>
public static class StripeSignature
{
    public static string Sign(string secret, long timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    public static string Header(string secret, long timestamp, string body) => $"t={timestamp},v1={Sign(secret, timestamp, body)}";

    /// <summary>Returns null when valid, otherwise "missing" | "bad_signature" | "stale".</summary>
    public static string? Verify(string secret, string? header, string body, long now, int toleranceSeconds)
    {
        if (string.IsNullOrWhiteSpace(header)) return "missing";
        long? t = null; var sigs = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t" && long.TryParse(kv[1], out var ts)) t = ts;
            else if (kv[0] == "v1") sigs.Add(kv[1]);
        }
        if (t is null || sigs.Count == 0) return "bad_signature";
        var expected = Encoding.ASCII.GetBytes(Sign(secret, t.Value, body));
        var ok = sigs.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s.ToLowerInvariant())));
        if (!ok) return "bad_signature";
        return Math.Abs(now - t.Value) > toleranceSeconds ? "stale" : null;
    }
}

public record WebhookResult(int Status, string Code, string Outcome = "");

/// <summary>Which webhook URL a delivery arrived on: the shared one (platform and Connect events) or the dedicated Connect one.</summary>
public enum StripeEndpoint { Any, Connect }

/// <summary>
/// Applies Stripe events. Idempotent on the event id (the processed-event row commits with the effects), order independent (every mutable row carries the
/// Stripe event time of the last applied event and older events are ignored), unknown objects/types are acknowledged and ignored.
/// </summary>
public class StripeWebhookService(AppDbContext db, LedgerService ledger, PaymentIngestService ingest, PaymentNotifier notifier, TimeProvider time,
    IOptions<StripeOptions> stripe, IOptions<PaymentOptions> opt, PaymentTestHooks hooks)
{
    public async Task<WebhookResult> HandleAsync(string body, string? signature, StripeEndpoint endpoint = StripeEndpoint.Any)
    {
        // Stripe signs the platform endpoint and the Connect endpoint with DIFFERENT secrets. Verify against the secret(s) that endpoint may use, remember which one matched, and
        // (below, once the event is parsed) require the Connect secret for Connect events and the platform secret for platform events when both are configured.
        var platform = stripe.Value.WebhookSecret ?? ""; var connect = stripe.Value.ConnectWebhookSecret ?? "";
        if (platform.Length == 0 && connect.Length == 0) return new(503, "webhook_not_configured");
        var both = platform.Length > 0 && connect.Length > 0 && platform != connect;      // one secret (or two equal ones) = the single-secret behaviour (Stripe CLI)
        var candidates = new List<(string Kind, string Secret)>();
        if (endpoint == StripeEndpoint.Connect) candidates.Add(connect.Length > 0 ? ("connect", connect) : ("platform", platform));
        else
        {
            if (platform.Length > 0) candidates.Add(("platform", platform));
            if (connect.Length > 0 && connect != platform) candidates.Add(("connect", connect));
        }
        string? matched = null, bad = null; var nowUnix = time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var (kind, secret) in candidates)
        {
            var err = StripeSignature.Verify(secret, signature, body, nowUnix, opt.Value.WebhookToleranceSeconds);
            if (err is null) { matched = kind; break; }
            bad = bad is null or "bad_signature" ? err : bad;
        }
        if (matched is null) return new(400, bad ?? "bad_signature");
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; if (root.ValueKind != JsonValueKind.Object) return new(400, "invalid_json"); }
        catch (JsonException) { return new(400, "invalid_json"); }
        var id = Str(root, "id"); var type = Str(root, "type");
        if (id is null or "" || type is null || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj)) return new(400, "invalid_event");
        var created = root.TryGetProperty("created", out var c) && c.TryGetInt64(out var cu) ? DateTimeOffset.FromUnixTimeSeconds(cu).UtcDateTime : time.UtcNow();
        var account = Str(root, "account");
        // both secrets configured: a Connect event (it carries `account`) must be signed with the Connect secret, a platform event with the platform secret
        if (both && endpoint == StripeEndpoint.Any && matched != (account is not null ? "connect" : "platform")) return new(400, "wrong_endpoint_secret");
        if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Source == "stripe" && p.EventId == id)) return new(200, "duplicate");

        string outcome;
        var mails = new List<Func<Task>>();
        // a dispute event reads the allocated rows and writes status / an unallocated item against them while the marketplace's own (serializable) events write the rows:
        // it runs in a serializable transaction too, so on PostgreSQL one of the two fails with 40001 and Stripe's retry (409) sees the other's result
        var serializable = type is "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed";
        await using var tx = serializable ? await hooks.BeginSerializableAsync(db) : null;
        bool saved;
        try
        {
            try
            {
                outcome = type switch
                {
                    "account.updated" => await AccountUpdated(obj, created, mails),
                    "transfer.created" or "transfer.updated" or "transfer.reversed" or "transfer.paid" => await TransferEvent(type, obj, created),
                    "payout.created" or "payout.updated" or "payout.paid" or "payout.failed" or "payout.canceled" => await PayoutEvent(type, obj, account, created, mails),
                    "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed" => await DisputeEvent(obj, created),
                    _ => "ignored_type"
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { db.ChangeTracker.Clear(); return new(400, "invalid_event"); }
            db.ProcessedPaymentEvents.Add(new ProcessedPaymentEvent { Source = "stripe", EventId = id, Type = type, ReceivedAt = time.UtcNow(), Outcome = outcome });
            if (hooks.BeforeWebhookSave is { } bws) await bws();
            saved = await ledger.SaveAsync();
            if (saved && tx is not null) await hooks.CommitAsync(tx);   // a PostgreSQL serialization failure can surface at COMMIT
        }
        catch (Exception ex) when (ex is DbUpdateException || DbErrors.IsSerialization(ex))
        {
            // nothing was applied and the event is NOT acknowledged: Stripe retries. No rollback after a failed COMMIT (the transaction is already completed).
            db.ChangeTracker.Clear();
            return new(409, "concurrent_update");
        }
        if (!saved)
        {
            if (tx is not null) { try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* already completed */ } }
            if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Source == "stripe" && p.EventId == id)) return new(200, "duplicate");
            return new(409, "concurrent_update");   // not acknowledged: Stripe retries the delivery
        }
        await ingest.AfterAsync();
        foreach (var m in mails) await m();
        return new(200, "ok", outcome);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
    private static string[] Arr(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
    private static DateTime? Unix(JsonElement e, string name) => Long(e, name) is { } l ? DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime : null;

    // ---- account.updated -------------------------------------------------------------------------------------------------------
    private async Task<string> AccountUpdated(JsonElement o, DateTime created, List<Func<Task>> mails)
    {
        var sid = Str(o, "id");
        var pa = sid is null ? null : await db.PayoutAccounts.FirstOrDefaultAsync(a => a.StripeAccountId == sid);
        if (pa is null) return "ignored_unknown_account";
        if (pa.LastEventAt is { } last && created < last) return "stale_ignored";
        Apply(pa, o, created);
        pa.UpdatedAt = time.UtcNow();
        var due = Arr(o.TryGetProperty("requirements", out var rq) ? rq : default, "currently_due");
        var past = Arr(rq, "past_due");
        if (due.Length > 0 || past.Length > 0 || pa.DisabledReason is not null)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', due.Concat(past).Order()) + pa.DisabledReason)))[..16];
            var seller = pa.SellerId;
            mails.Add(async () => await notifier.NotifyAsync(seller, $"onboarding:{hash}", "Stripe needs more information to pay you", "Action needed to receive payouts",
                ["Stripe needs more information about your business before it can send you payouts." + (past.Length > 0 ? " Some of it is already overdue." : ""), "Open Payments and continue the setup. It only takes a few minutes."]));
        }
        return "applied";
    }

    private static void Apply(PayoutAccount pa, JsonElement o, DateTime created)
    {
        pa.DetailsSubmitted = Bool(o, "details_submitted"); pa.ChargesEnabled = Bool(o, "charges_enabled"); pa.PayoutsEnabled = Bool(o, "payouts_enabled");
        JsonElement rq = o.TryGetProperty("requirements", out var r0) ? r0 : default;
        pa.RequirementsDueJson = JsonSerializer.Serialize(Arr(rq, "currently_due")); pa.RequirementsPastDueJson = JsonSerializer.Serialize(Arr(rq, "past_due"));
        pa.DisabledReason = rq.ValueKind == JsonValueKind.Object ? Str(rq, "disabled_reason") : null;
        pa.RequirementsDeadline = rq.ValueKind == JsonValueKind.Object ? Unix(rq, "current_deadline") : null;
        if (o.TryGetProperty("external_accounts", out var ea) && ea.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        { pa.BankLast4 = Str(arr[0], "last4"); pa.BankName = Str(arr[0], "bank_name"); }
        else { pa.BankLast4 = null; pa.BankName = null; }
        if (o.TryGetProperty("settings", out var st) && st.TryGetProperty("payouts", out var po) && po.TryGetProperty("schedule", out var sc))
        { pa.PayoutInterval = Str(sc, "interval"); pa.PayoutDelayDays = (int?)Long(sc, "delay_days"); }
        pa.LastEventAt = created;
    }

    /// <summary>Applies a provider snapshot (used by the manual sync and the reconciliation) with the same newest-wins rule.</summary>
    public static void Apply(PayoutAccount pa, AccountSnapshot s, DateTime at)
    {
        pa.DetailsSubmitted = s.DetailsSubmitted; pa.ChargesEnabled = s.ChargesEnabled; pa.PayoutsEnabled = s.PayoutsEnabled;
        pa.RequirementsDueJson = JsonSerializer.Serialize(s.CurrentlyDue); pa.RequirementsPastDueJson = JsonSerializer.Serialize(s.PastDue);
        pa.DisabledReason = s.DisabledReason; pa.RequirementsDeadline = s.Deadline; pa.PayoutInterval = s.PayoutInterval; pa.PayoutDelayDays = s.PayoutDelayDays;
        pa.BankLast4 = s.BankLast4; pa.BankName = s.BankName; pa.LastEventAt = at;
    }

    // ---- transfer.* ------------------------------------------------------------------------------------------------------------
    private async Task<string> TransferEvent(string type, JsonElement o, DateTime created)
    {
        var sid = Str(o, "id");
        Guid.TryParse(o.TryGetProperty("metadata", out var md) ? Str(md, "transferId") : null, out var mid);
        var t = await db.PaymentTransfers.FirstOrDefaultAsync(x => (sid != null && x.StripeTransferId == sid) || (mid != Guid.Empty && x.Id == mid));
        if (t is null) return "ignored_unknown_transfer";
        if (t.EventAt is { } last && created < last) return "stale_ignored";
        t.EventAt = created; t.UpdatedAt = time.UtcNow();
        t.StripeTransferId ??= sid;
        // Stripe sends transfer.reversed for PARTIAL reversals too (reversed=false, amount_reversed < amount): credit only the not-yet-credited part of amount_reversed
        // (running total, idempotent per total) and close the transfer only when it is fully reversed.
        var fully = Bool(o, "reversed");
        var reversedTotal = Long(o, "amount_reversed") ?? (fully || type == "transfer.reversed" && !o.TryGetProperty("amount_reversed", out _) ? t.AmountCents : 0);
        reversedTotal = Math.Clamp(reversedTotal, 0, t.AmountCents);
        if (reversedTotal >= t.AmountCents) fully = true;
        if (t.Status is TransferStatus.Created or TransferStatus.Pending && reversedTotal > 0)
        {
            var credited = await db.LedgerJournals.Where(j => j.Type == "transfer_reversed" && j.SourceRef == t.Id.ToString()).SelectMany(j => j.Entries).Where(e => e.Account == LedgerAccount.Available).SumAsync(e => (long?)e.AmountCents) ?? 0;
            var delta = reversedTotal - credited;
            if (delta > 0)
                await ledger.PostAsync(t.SellerId, t.Currency, "transfer_reversed", $"transfer-reversed:{t.Id}:{reversedTotal}", "Transfer reversed",
                    [new(LedgerAccount.Settled, -delta, "transfer"), new(LedgerAccount.Available, delta, "transfer")], sourceRef: t.Id.ToString());
        }
        if (fully && t.Status != TransferStatus.Reversed && t.Status != TransferStatus.Failed) t.Status = TransferStatus.Reversed;
        else if (!fully && t.Status == TransferStatus.Pending) { t.Status = TransferStatus.Created; t.LastError = null; }
        return "applied";
    }

    // ---- payout.* (connected account events) ------------------------------------------------------------------------------------
    private async Task<string> PayoutEvent(string type, JsonElement o, string? account, DateTime created, List<Func<Task>> mails)
    {
        var pa = account is null ? null : await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.StripeAccountId == account);
        var pid = Str(o, "id");
        if (pa is null || pid is null) return "ignored_unknown_account";
        var amount = Long(o, "amount") ?? 0; var cur = (Str(o, "currency") ?? "usd").ToUpperInvariant();
        var status = type switch { "payout.paid" => "paid", "payout.failed" => "failed", "payout.canceled" => "canceled", _ => Str(o, "status") ?? "pending" };
        var row = await db.PaymentPayouts.FirstOrDefaultAsync(p => p.StripePayoutId == pid);
        if (row is not null && created < row.EventAt) return "stale_ignored";
        var now = time.UtcNow();
        if (row is null) { row = new PaymentPayout { SellerId = pa.SellerId, StripePayoutId = pid, Currency = cur, AmountCents = amount, CreatedAt = now }; db.PaymentPayouts.Add(row); }
        row.Status = status; row.EventAt = created; row.UpdatedAt = now; row.ArrivalDate = Unix(o, "arrival_date") ?? row.ArrivalDate;
        row.FailureCode = status is "failed" or "canceled" ? Str(o, "failure_code") : null;
        if (status == "paid")
            await ledger.PostAsync(pa.SellerId, cur, "payout", $"payout-paid:{pid}", "Payout paid out", [new(LedgerAccount.Settled, -amount, "payout"), new(LedgerAccount.PaidOut, amount, "payout")], sourceRef: pid);
        else if (status is "failed" or "canceled" && await ledger.ExistsAsync($"payout-paid:{pid}"))
            await ledger.PostAsync(pa.SellerId, cur, "payout_reversed", $"payout-reversed:{pid}", "Payout returned", [new(LedgerAccount.Settled, amount, "payout"), new(LedgerAccount.PaidOut, -amount, "payout")], sourceRef: pid);
        if (status == "failed")
        {
            var seller = pa.SellerId;
            mails.Add(async () => await notifier.NotifyAsync(seller, $"payout-failed:{pid}", "A payout to your bank failed", "A payout failed",
                [$"Stripe could not pay out {PaymentNotifier.Money(amount, cur)} to your bank account.", "Check your bank details in your Stripe dashboard; Stripe will retry once they are fixed."]));
        }
        return "applied";
    }

    // ---- charge.dispute.* -------------------------------------------------------------------------------------------------------
    private async Task<string> DisputeEvent(JsonElement o, DateTime created)
    {
        var did = Str(o, "id"); if (did is null) return "invalid";
        var rows = await db.PaymentDisputes.Where(d => d.ExternalId == did).ToListAsync();   // one row per seller order the marketplace allocated a share to
        var amount = Long(o, "amount") ?? 0; var cur = (Str(o, "currency") ?? "usd").ToUpperInvariant();
        var raw = (Str(o, "status") ?? "needs_response").ToLowerInvariant();
        // Stripe inquiries (warning_*) are early warnings that move no money: kept as "inquiry" without a debit or fee
        var status = raw switch { "warning_needs_response" => "inquiry", "warning_under_review" => "inquiry_review", "charge_refunded" => "warning_closed", _ => raw };
        if (status is not ("needs_response" or "under_review" or "won" or "lost" or "warning_closed" or "inquiry" or "inquiry_review")) status = "needs_response";
        var due = o.TryGetProperty("evidence_details", out var ev) ? Unix(ev, "due_by") : null;
        var charge = Str(o, "charge") ?? Str(o, "payment_intent");
        var refs = new[] { Str(o, "charge"), Str(o, "payment_intent") }.Where(x => !string.IsNullOrEmpty(x)).ToList();
        var pr = await db.PaymentReferences.AsNoTracking().Where(r => refs.Contains(r.Ref)).OrderBy(r => r.CapturedAt).FirstOrDefaultAsync();
        List<SellerOrder> orders = []; Booking? booking = null;
        if (pr?.MarketplaceOrderId is { } mo) orders = await db.Orders.AsNoTracking().Where(x => x.MarketplaceOrderId == mo).ToListAsync();
        else if (pr?.MarketplaceBookingId is { } mb) booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == mb);
        var now = time.UtcNow();
        // One charge, several seller orders: Stripe cannot say how the dispute splits, so it never debits anybody. It only records status / evidence date on the rows the marketplace
        // already allocated, and keeps a staff-visible item open while the allocated shares are below the Stripe amount. The money comes from the marketplace's per-seller-order events.
        if (orders.Count > 1 || rows.Count > 1)
        {
            if (amount <= 0) return "invalid";
            var shared = rows.FirstOrDefault()?.Currency ?? orders.FirstOrDefault()?.Currency ?? cur;
            if (shared != cur)
            {
                await ingest.FlagOnceAsync("dispute_currency_mismatch", did, $"Stripe dispute {did} is in {cur} but the seller orders it covers are in {shared}: it was not recorded and does not count toward the allocation.", now);
                return "flagged_currency_mismatch";
            }
            return await ingest.RecordStripeMultiSellerAsync(did, rows, pr?.MarketplaceOrderId, charge, shared, amount, status, Str(o, "reason"), due, created, now);
        }
        PaymentDispute? existing = null; Guid seller; Guid? orderId = null, bookingId = null; var currency = cur;
        if (orders.Count == 1) { existing = rows.FirstOrDefault(r => r.OrderId == orders[0].Id); seller = orders[0].SellerId; orderId = orders[0].Id; currency = orders[0].Currency; }
        else if (booking is not null) { existing = rows.FirstOrDefault(r => r.BookingId == booking.Id); seller = booking.SellerId; bookingId = booking.Id; }
        else if (rows.Count == 1) { existing = rows[0]; seller = existing.SellerId; orderId = existing.OrderId; bookingId = existing.BookingId; currency = existing.Currency; }
        else
        {
            await ingest.FlagOnceAsync("dispute_unattributed", did, $"Stripe dispute {did} could not be matched to a seller order/booking (charge/payment intent unknown).", now);
            return "flagged_unattributed";
        }
        // the dispute is already allocated (by the marketplace) to a different seller order / booking than this Stripe charge maps to: a Stripe event may only update rows it can match
        // unambiguously, so nobody is debited and staff are told (a second seller must never be debited for a dispute that is already someone else's)
        if (existing is null && rows.Count > 0)
        {
            await ingest.FlagOnceAsync("dispute_unattributed", did, $"Stripe dispute {did} is already allocated to another seller order/booking than the one its charge maps to: nothing was recorded or debited.", now, seller);
            return "flagged_unattributed";
        }
        if (existing is not null) { seller = existing.SellerId; currency = existing.Currency; orderId = existing.OrderId; bookingId = existing.BookingId; }
        if (currency != cur)
        {
            await ingest.FlagOnceAsync("dispute_currency_mismatch", did, $"Stripe dispute {did} is in {cur} but the seller order it maps to is in {currency}: it was not recorded.", now, seller);
            return "flagged_currency_mismatch";
        }
        if (amount <= 0) return "invalid";
        await ingest.ApplyDisputeAsync(existing, did, seller, currency, orderId, bookingId, amount, existing?.FeeCents ?? opt.Value.DefaultDisputeFeeCents, status, Str(o, "reason"), due, created, now);
        return "applied";
    }
}
