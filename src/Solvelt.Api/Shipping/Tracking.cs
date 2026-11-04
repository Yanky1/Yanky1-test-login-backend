using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Email;
using Solvelt.Api.Options;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Services;

namespace Solvelt.Api.Shipping;

/// <summary>Registers a tracking number with the provider (webhooks) for manual, CSV and label shipments. Failure is never fatal: the reconciler retries and polls.</summary>
public class ShipmentTracker(AppDbContext db, IShippingProvider provider, TimeProvider time, ILogger<ShipmentTracker> log)
{
    private async Task<bool> TryAsync(string carrier, string number)
    {
        try { return await provider.RegisterTrackingAsync(carrier, number); }
        catch (ShippingProviderException ex) { log.LogInformation("Tracking registration deferred ({Code})", ex.Code); return false; }
    }

    public async Task RegisterAsync(Shipment s)
    {
        if (s.Method != "carrier" || s.TrackingRegisteredAt is not null || s.TrackingStoppedAt is not null || s.DeliveredAt is not null) return;
        if (!await TryAsync(s.Carrier, s.TrackingNumber)) return;
        var now = time.UtcNow(); s.TrackingRegisteredAt = now;
        await db.Shipments.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TrackingRegisteredAt, now));
    }

    public async Task RegisterAsync(ReturnShipment s)
    {
        if (s.TrackingRegisteredAt is not null || s.TrackingStoppedAt is not null || s.DeliveredAt is not null) return;
        if (!await TryAsync(s.Carrier, s.TrackingNumber)) return;
        var now = time.UtcNow(); s.TrackingRegisteredAt = now;
        await db.ReturnShipments.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TrackingRegisteredAt, now));
    }
}

/// <summary>In-app signal + email when a shipment needs attention or a return parcel arrived. Owners always get the email; other members only when opted in (ShippingEmails).</summary>
public class ShippingNotifier(AppDbContext db, Mailer mailer, EventHub hub, IOptions<AppOptions> app, ILogger<ShippingNotifier> log)
{
    public async Task<List<string>> RecipientsAsync(Guid sellerId, Guid brandId)
    {
        var members = await db.Memberships.AsNoTracking().Where(m => m.SellerId == sellerId && m.Status == MembershipStatus.Active).ToListAsync();
        var scoped = await db.MembershipBrands.AsNoTracking().Where(b => b.BrandId == brandId).Select(b => b.MembershipId).ToListAsync();
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => p.SellerId == sellerId).ToListAsync();
        var ids = members.Where(m => m.Role == AccountRole.Owner
            || ((m.AllBrands || m.Role >= AccountRole.Admin || scoped.Contains(m.Id)) && prefs.FirstOrDefault(p => p.UserId == m.UserId)?.ShippingEmails == true)).Select(m => m.UserId).ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.Email != null).Select(u => u.Email!).ToListAsync();
    }

    private async Task Mail(SellerOrder o, string kind, string subject, string heading, string line, string button)
    {
        try
        {
            var url = app.Value.FrontendBaseUrl.TrimEnd('/') + $"/orders/{o.Id}";
            foreach (var to in await RecipientsAsync(o.SellerId, o.BrandId))
                await mailer.SendAsync(Templates.Notification(to, kind, subject, heading, [line], button, url));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Shipping notification {Kind} failed", kind); }
    }

    public async Task ExceptionAsync(SellerOrder o, string detail)
    {
        hub.Publish(new LiveEvent("order.attention", o.SellerId, o.BrandId, new { orderId = o.Id }));
        await Mail(o, "shipping-exception", $"Delivery problem with {o.Number}", "A shipment needs your attention", $"The carrier reported a problem with a shipment of order {o.Number}: {detail}.", "View order");
    }

    public async Task ReturnArrivedAsync(SellerOrder o)
    {
        hub.Publish(new LiveEvent("order.return_arrived", o.SellerId, o.BrandId, new { orderId = o.Id }));
        await Mail(o, "return-arrived", $"Return arrived for {o.Number}", "A return parcel arrived", $"The return parcel for order {o.Number} was delivered. Inspect the items and mark the return received.", "View return");
    }
}

/// <summary>Adapter so one engine handles carrier shipments and return parcels.</summary>
internal sealed class Parcel
{
    public Shipment? Ship; public ReturnShipment? Ret;
    public Guid Id => Ship?.Id ?? Ret!.Id;
    public Guid OrderId => Ship?.OrderId ?? Ret!.OrderId;
    public string Carrier => Ship?.Carrier ?? Ret!.Carrier;
    public string TrackingNumber => Ship?.TrackingNumber ?? Ret!.TrackingNumber;
    public string? Status { get => Ship?.TrackingStatus ?? Ret?.TrackingStatus; set { if (Ship != null) Ship.TrackingStatus = value; else Ret!.TrackingStatus = value; } }
    public string? Detail { set { if (Ship != null) Ship.TrackingStatusDetail = value; else Ret!.TrackingStatusDetail = value; } }
    public DateTime? Eta { set { if (Ship != null) Ship.TrackingEta = value; else Ret!.TrackingEta = value; } }
    public DateTime? StatusAt { get => Ship?.TrackingStatusAt ?? Ret?.TrackingStatusAt; set { if (Ship != null) Ship.TrackingStatusAt = value; else Ret!.TrackingStatusAt = value; } }
    public DateTime? LastAt { set { if (Ship != null) Ship.LastTrackingAt = value; else Ret!.LastTrackingAt = value; } }
    public DateTime? DeliveredAt => Ship?.DeliveredAt ?? Ret?.DeliveredAt;
    public DateTime? Stopped { get => Ship?.TrackingStoppedAt ?? Ret?.TrackingStoppedAt; set { if (Ship != null) Ship.TrackingStoppedAt = value; else Ret!.TrackingStoppedAt = value; } }
}

public record TrackingApplied(string Outcome, string? Status = null);

/// <summary>
/// Applies provider tracking (webhook or poll) to a parcel. Idempotent (history entries are de-duplicated by a hash of time+status+detail) and order-independent
/// (a status only replaces the current one when it is newer; ties go to the later stage), so replays and out-of-order deliveries are harmless.
/// </summary>
public class TrackingService(AppDbContext db, OrderActions actions, Outbox outbox, ShippingNotifier notifier, TimeProvider time)
{
    public static string Key(TrackingEntry e) => ShippingSupport.Hash($"{e.At.ToUniversalTime():O}|{e.Status}|{e.Detail}")[..32];

    public async Task<(Shipment? Ship, ReturnShipment? Ret)> FindAsync(string number)
    {
        var tn = Orders.Tracking.Normalize(number);
        var s = await db.Shipments.Where(x => x.TrackingNumber == tn && x.Method == "carrier").OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (s is not null) return (s, null);
        var r = await db.ReturnShipments.Where(x => x.TrackingNumber == tn).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        return (null, r);
    }

    public async Task<TrackingApplied> ApplyAsync(string number, TrackingSnapshot snap, string source)
    {
        var (s, r) = await FindAsync(number);
        if (s is null && r is null) return new("unknown_shipment");
        for (var attempt = 0; ; attempt++)
        {
            try { return await ApplyToAsync(new Parcel { Ship = s, Ret = r }, snap, source); }
            catch (DbUpdateException ex) when (attempt == 0 && DbErrors.IsUnique(ex))
            {
                // a concurrent webhook/poll stored the same history entry first: reload and run once more (the duplicates are skipped now)
                db.ChangeTracker.Clear();
                (s, r) = await FindAsync(number);
                if (s is null && r is null) return new("unknown_shipment");
            }
        }
    }

    private async Task<TrackingApplied> ApplyToAsync(Parcel p, TrackingSnapshot snap, string source)
    {
        var now = time.UtcNow();
        var order = await actions.Full(db.Orders).FirstAsync(o => o.Id == p.OrderId);
        var have = (await db.TrackingEvents.Where(e => p.Ship != null ? e.ShipmentId == p.Id : e.ReturnShipmentId == p.Id).Select(e => e.DedupeKey).ToListAsync()).ToHashSet();
        var fresh = new List<TrackingEntry>();
        foreach (var e in snap.History.Append(new TrackingEntry(snap.Status, snap.Detail, snap.Location, snap.StatusAt)).OrderBy(x => x.At))
        {
            if (!have.Add(Key(e))) continue;
            fresh.Add(e);
            db.TrackingEvents.Add(new TrackingEvent
            {
                ShipmentId = p.Ship?.Id, ReturnShipmentId = p.Ret?.Id, Status = TrackingStatuses.Normalize(e.Status), Detail = Clip(e.Detail, 500), Location = Clip(e.Location, 200),
                OccurredAt = e.At.ToUniversalTime(), Source = source, DedupeKey = Key(e), CreatedAt = now
            });
        }
        p.LastAt = now;
        var status = TrackingStatuses.Normalize(snap.Status);
        var at = snap.StatusAt.ToUniversalTime();
        var newer = p.DeliveredAt is null && (p.StatusAt is null || at > p.StatusAt || (at == p.StatusAt && TrackingStatuses.Rank(status) > TrackingStatuses.Rank(p.Status)));
        if (!newer)
        {
            await db.SaveChangesAsync();
            return new(fresh.Count == 0 ? "duplicate" : "stale", p.Status);
        }
        p.Status = status; p.Detail = Clip(snap.Detail, 500); p.Eta = snap.Eta?.ToUniversalTime(); p.StatusAt = at;
        var wasFlagged = order.NeedsAttention;
        var exception = TrackingStatuses.IsException(status);
        if (exception && !wasFlagged)
        {
            order.NeedsAttention = true; order.AttentionAt = now;
            order.AttentionReason = "tracking:" + (snap.Detail ?? TrackingStatuses.Label(status));
            db.AddEvent(order.Id, "shipment.exception", Party.System, null, $"{p.Carrier} {p.TrackingNumber}: {snap.Detail ?? TrackingStatuses.Label(status)}", now);
        }
        else if (!exception && wasFlagged && order.AttentionReason?.StartsWith("tracking:") == true)
        {
            order.NeedsAttention = false; order.AttentionReason = null; order.AttentionAt = null;   // the carrier recovered
        }
        var evs = fresh.TakeLast(20).Select(e => new { status = e.Status, detail = e.Detail, location = e.Location, at = e.At });
        outbox.Enqueue("order.tracking_updated", order.SellerId, order.MarketplaceOrderId, new
        {
            order = new { sellerOrderId = order.Id, sellerOrderNumber = order.Number, marketplaceOrderId = order.MarketplaceOrderId },
            kind = p.Ship != null ? "shipment" : "return", shipmentId = p.Id, returnId = p.Ret?.ReturnId, carrier = p.Carrier, trackingNumber = p.TrackingNumber,
            trackingUrl = Orders.Tracking.Url(p.Carrier, p.TrackingNumber), status, statusDetail = snap.Detail, eta = snap.Eta, events = evs
        });
        order.UpdatedAt = now;
        if (status == TrackingStatuses.Delivered)
        {
            if (p.Ship is not null)
            {
                var res = await actions.ShipmentDeliveredAsync(order, p.Ship, at);
                if (!res.Ok) { db.ChangeTracker.Clear(); throw new InvalidOperationException("Could not record the delivery: " + res.Code); }
            }
            else
            {
                p.Ret!.DeliveredAt = at; p.Stopped = now;
                db.AddEvent(order.Id, "return.arrived", Party.System, null, "Return arrived. Inspect the items and mark the return received.", now);
                await db.SaveChangesAsync();
                await notifier.ReturnArrivedAsync(order);
            }
            return new("applied", status);
        }
        await db.SaveChangesAsync();
        actions.LiveChanged(order);
        if (exception && !wasFlagged) await notifier.ExceptionAsync(order, snap.Detail ?? TrackingStatuses.Label(status));
        return new("applied", status);
    }

    private static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;
}

/// <summary>Provider tracking webhooks: authenticated, idempotent on the event id, logged for the staff "webhook failures" view.</summary>
public class ShippingWebhookService(AppDbContext db, IOptions<ShippingOptions> opt, TrackingService tracking, TimeProvider time)
{
    public const string SignatureHeader = "X-Shipping-Signature";

    public static string Sign(string secret, long t, string body) => $"t={t},v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();

    /// <summary>Valid when the HMAC header verifies (and is fresh), or the ?token= equals the secret (providers that cannot sign).</summary>
    public bool Authentic(string body, string? header, string? token)
    {
        var secret = opt.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return false;
        if (!string.IsNullOrEmpty(token)) return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(secret));
        if (string.IsNullOrEmpty(header)) return false;
        long t = 0; string? v1 = null;
        foreach (var part in header.Split(',')) { var kv = part.Split('=', 2); if (kv.Length == 2 && kv[0].Trim() == "t") long.TryParse(kv[1], out t); else if (kv.Length == 2 && kv[0].Trim() == "v1") v1 = kv[1].Trim(); }
        if (t == 0 || v1 is null || Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - t) > opt.Value.WebhookToleranceSeconds) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, t, body)), Encoding.ASCII.GetBytes($"t={t},v1={v1.ToLowerInvariant()}"));
    }

    public async Task<(int Status, string Outcome)> HandleAsync(string body, string? header, string? token, string provider = "shippo")
    {
        var now = time.UtcNow();
        if (!Authentic(body, header, token))
        {
            await LogAsync(new ShippingWebhookEvent { Provider = provider, Outcome = "bad_signature", ReceivedAt = now });
            return (401, "bad_signature");
        }
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { root = null; }
        var data = root?["data"];
        if (root is null || data is null) { await LogAsync(new ShippingWebhookEvent { Provider = provider, Outcome = "bad_payload", ReceivedAt = now }); return (400, "bad_payload"); }
        if (root["event"]?.GetValue<string>() is { } ev && ev != "track_updated") return (200, "ignored");
        var tn = data["tracking_number"]?.GetValue<string>();
        var eventId = root["event_id"]?.GetValue<string>() ?? data["tracking_status"]?["object_id"]?.GetValue<string>() ?? ShippingSupport.Hash(body)[..40];
        if (tn is null) { await LogAsync(new ShippingWebhookEvent { Provider = provider, EventId = eventId, Outcome = "bad_payload", ReceivedAt = now }); return (400, "bad_payload"); }

        var row = await db.ShippingWebhookEvents.FirstOrDefaultAsync(w => w.Provider == provider && w.EventId == eventId);
        if (row is not null && row.Outcome != "error") return (200, "duplicate");
        if (row is null)
        {
            row = new ShippingWebhookEvent { Provider = provider, EventId = eventId, TrackingNumber = Orders.Tracking.Normalize(tn), Outcome = "received", ReceivedAt = now };
            db.ShippingWebhookEvents.Add(row);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (DbErrors.IsUnique(ex)) { db.ChangeTracker.Clear(); return (200, "duplicate"); }   // a parallel delivery of the same event won
        }
        try
        {
            var snap = ShippoPayload.Snapshot(data);
            if (snap is null) { row.Outcome = "bad_payload"; await db.SaveChangesAsync(); return (400, "bad_payload"); }
            var r = await tracking.ApplyAsync(tn, snap, "webhook");
            await SetOutcome(row.Id, r.Outcome, null);
            return (200, r.Outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            await SetOutcome(row.Id, "error", ex.GetType().Name);
            return (503, "error");   // the provider retries
        }
    }

    private async Task SetOutcome(Guid id, string outcome, string? err) =>
        await db.ShippingWebhookEvents.Where(w => w.Id == id).ExecuteUpdateAsync(u => u.SetProperty(w => w.Outcome, outcome).SetProperty(w => w.Error, err));

    private async Task LogAsync(ShippingWebhookEvent e) { db.ShippingWebhookEvents.Add(e); try { await db.SaveChangesAsync(); } catch (DbUpdateException) { db.ChangeTracker.Clear(); } }
}

public static class ShippingWebhookEndpoints
{
    public static void MapShippingWebhooks(this WebApplication app, RouteGroupBuilder v1)
    {
        v1.MapPost("/shipping/webhooks/shippo", async (HttpContext ctx, ShippingWebhookService svc, IOptions<ShippingOptions> opt) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > 512 * 1024) return Results.StatusCode(413);
            var (status, outcome) = await svc.HandleAsync(Encoding.UTF8.GetString(ms.ToArray()), ctx.Request.Headers[ShippingWebhookService.SignatureHeader], ctx.Request.Query["token"]);
            return Results.Json(new { outcome }, statusCode: status);
        }).AllowAnonymous().RequireRateLimiting("shipping-webhooks");
    }
}
