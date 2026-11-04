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

/// <summary>
/// Applies Stripe events. Idempotent on the event id (the processed-event row commits with the effects), order independent (every mutable row carries the
/// Stripe event time of the last applied event and older events are ignored), unknown objects/types are acknowledged and ignored.
/// </summary>
public class StripeWebhookService(AppDbContext db, LedgerService ledger, PaymentIngestService ingest, PaymentNotifier notifier, TimeProvider time,
    IOptions<StripeOptions> stripe, IOptions<PaymentOptions> opt, PaymentTestHooks hooks)
{
    public async Task<WebhookResult> HandleAsync(string body, string? signature)
    {
        var secret = stripe.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return new(503, "webhook_not_configured");
        var bad = StripeSignature.Verify(secret, signature, body, time.GetUtcNow().ToUnixTimeSeconds(), opt.Value.WebhookToleranceSeconds);
        if (bad is not null) return new(400, bad);
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; if (root.ValueKind != JsonValueKind.Object) return new(400, "invalid_json"); }
        catch (JsonException) { return new(400, "invalid_json"); }
        var id = Str(root, "id"); var type = Str(root, "type");
        if (id is null or "" || type is null || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj)) return new(400, "invalid_event");
        var created = root.TryGetProperty("created", out var c) && c.TryGetInt64(out var cu) ? DateTimeOffset.FromUnixTimeSeconds(cu).UtcDateTime : time.UtcNow();
        var account = Str(root, "account");
        if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Source == "stripe" && p.EventId == id)) return new(200, "duplicate");

        string outcome;
        var mails = new List<Func<Task>>();
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
        if (!await ledger.SaveAsync())
        {
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
        var existing = await db.PaymentDisputes.FirstOrDefaultAsync(d => d.ExternalId == did);
        var amount = Long(o, "amount") ?? 0; var cur = (Str(o, "currency") ?? "usd").ToUpperInvariant();
        var raw = (Str(o, "status") ?? "needs_response").ToLowerInvariant();
        // Stripe inquiries (warning_*) are early warnings that move no money: kept as "inquiry" without a debit or fee
        var status = raw switch { "warning_needs_response" => "inquiry", "warning_under_review" => "inquiry_review", "charge_refunded" => "warning_closed", _ => raw };
        if (status is not ("needs_response" or "under_review" or "won" or "lost" or "warning_closed" or "inquiry" or "inquiry_review")) status = "needs_response";
        var due = o.TryGetProperty("evidence_details", out var ev) ? Unix(ev, "due_by") : null;
        Guid seller; Guid? orderId = existing?.OrderId, bookingId = existing?.BookingId; string currency = existing?.Currency ?? cur;
        if (existing is not null) seller = existing.SellerId;
        else
        {
            var refs = new[] { Str(o, "charge"), Str(o, "payment_intent") }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            var pr = await db.PaymentReferences.AsNoTracking().Where(r => refs.Contains(r.Ref)).OrderBy(r => r.CapturedAt).FirstOrDefaultAsync();
            Guid? s = null;
            if (pr?.MarketplaceOrderId is { } mo)
            {
                var orders = await db.Orders.AsNoTracking().Where(x => x.MarketplaceOrderId == mo).ToListAsync();
                if (orders.Count == 1) { s = orders[0].SellerId; orderId = orders[0].Id; currency = orders[0].Currency; }
            }
            else if (pr?.MarketplaceBookingId is { } mb)
            {
                var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.MarketplaceBookingId == mb);
                if (b is not null) { s = b.SellerId; bookingId = b.Id; }
            }
            if (s is null)
            {
                db.ReconciliationFlags.Add(new ReconciliationFlag { Kind = "dispute_unattributed", Detail = $"Stripe dispute {did} could not be matched to exactly one seller order/booking (charge/payment intent unknown or shared by several sellers).", CreatedAt = time.UtcNow() });
                return "flagged_unattributed";
            }
            seller = s.Value;
        }
        if (amount <= 0) return "invalid";
        await ingest.ApplyDisputeAsync(existing, did, seller, currency, orderId, bookingId, amount, existing?.FeeCents ?? opt.Value.DefaultDisputeFeeCents, status, Str(o, "reason"), due, created, time.UtcNow());
        return "applied";
    }
}
