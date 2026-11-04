using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Services;

namespace Solvelt.Api.Orders;

public static class OrderJson
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    public static string Ser(object o) => JsonSerializer.Serialize(o, Opts);
}

/// <summary>HMAC-SHA256 request signing shared by the inbound API (verify) and the outbound webhook (sign).</summary>
public static class Signing
{
    public const string TimestampHeader = "X-Solvelt-Timestamp", NonceHeader = "X-Solvelt-Nonce", SignatureHeader = "X-Solvelt-Signature";
    public const string EventIdHeader = "X-Solvelt-Event-Id", EventTypeHeader = "X-Solvelt-Event-Type";

    public static string BodyHash(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>Canonical string for inbound requests: timestamp, nonce, METHOD, path+query and the SHA-256 of the exact body bytes, newline separated.</summary>
    public static string Canonical(string timestamp, string nonce, string method, string pathAndQuery, byte[] body) =>
        $"{timestamp}\n{nonce}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{BodyHash(body)}";

    public static string Sign(string secret, string canonical) =>
        "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    public static bool Verify(string secret, string canonical, string? signature) =>
        signature is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, canonical)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));

    /// <summary>Outbound webhook signature: HMAC over "{timestamp}.{body}" with the (separate) outbound secret.</summary>
    public static string SignOutbound(string secret, string timestamp, string body) => Sign(secret, timestamp + "." + body);

    /// <summary>Purpose of the separate key that image downloads may be signed with (see <see cref="ImageKey"/>).</summary>
    public const string ImagePurpose = "catalog-image";

    /// <summary>
    /// The key image requests may be signed with: <c>Orders:ImageKey</c> when configured, otherwise a SUBKEY derived from the inbound secret:
    /// lower-case hex of HMAC-SHA256(key = the inbound secret as UTF-8, message = "solvelt.catalog-image.v1"). It is used exactly like the inbound secret (same canonical string).
    /// It can sign image downloads only, so whoever holds it (an edge proxy, a CDN worker) cannot call any other internal endpoint.
    /// </summary>
    public static string ImageKey(OrderOptions o) => !string.IsNullOrEmpty(o.ImageKey) ? o.ImageKey
        : Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(o.InboundSecret), Encoding.UTF8.GetBytes("solvelt.catalog-image.v1"))).ToLowerInvariant();
}

/// <summary>
/// Decides whether a rejected inbound request may write an <c>inbound.rejected</c> audit row: at most N per client IP per minute, counted in memory. Without it every forged request
/// cost one database insert (nothing deleted them), so the per-IP failed-request bucket only turned the 401 into a 429 and bounded no server work.
/// Auditing never decides whether a request is refused: signed requests that share an IP with garbage are not affected.
/// </summary>
public sealed class InboundRejectedSampler(TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Minute, int Count)> _seen = new();

    public bool Allow(string ip, int perMinute)
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        if (_seen.Count > 10_000) foreach (var k in _seen.Where(x => x.Value.Minute < minute).Select(x => x.Key).ToList()) _seen.TryRemove(k, out _);   // bounded memory
        var allowed = false;
        _seen.AddOrUpdate(ip, _ => { allowed = perMinute > 0; return (minute, 1); }, (_, cur) =>
        {
            if (cur.Minute != minute) { allowed = perMinute > 0; return (minute, 1); }
            allowed = cur.Count < perMinute;
            return (minute, cur.Count + 1);
        });
        return allowed;
    }
}

/// <summary>Verifies signed inbound requests: headers present, timestamp in window, signature valid, nonce unused. Returns the raw body on success.</summary>
public class InboundVerifier(IOptions<OrderOptions> opt, AppDbContext db, TimeProvider time, InboundRejectedSampler sampler)
{
    public record Result(byte[]? Body, int Status, string Code, string Message)
    {
        public bool Ok => Body is not null;
    }

    /// <param name="purpose">null: the request must be signed with the inbound secret. <see cref="Signing.ImagePurpose"/>: the image key is accepted as well (and the inbound secret too unless <c>Orders:ImageAcceptInboundKey</c> is false).</param>
    public async Task<Result> VerifyAsync(HttpRequest req, CancellationToken ct, string? purpose = null)
    {
        var res = await VerifyCoreAsync(req, ct, purpose);
        // rejected signatures / stale timestamps / replays are counted for the staff integration-health page (no payload, no secrets)
        if (!res.Ok && res.Code is "bad_signature" or "stale" or "replayed" or "bad_timestamp" or "unsigned"
            && sampler.Allow(req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", opt.Value.InboundRejectedAuditPerIpPerMinute))   // sampled per IP: a flood writes a bounded number of rows
        {
            try
            {
                db.AuditEvents.Add(new AuditEvent { Type = "inbound.rejected", Detail = "code=" + res.Code, IpAddress = req.HttpContext.Connection.RemoteIpAddress?.ToString(), At = time.UtcNow() });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception) { db.ChangeTracker.Clear(); }
        }
        return res;
    }

    private async Task<Result> VerifyCoreAsync(HttpRequest req, CancellationToken ct, string? purpose)
    {
        var o = opt.Value;
        if (string.IsNullOrEmpty(o.InboundSecret)) return new(null, 503, "inbound_not_configured", "The inbound API is not configured.");
        var ts = req.Headers[Signing.TimestampHeader].ToString();
        var nonce = req.Headers[Signing.NonceHeader].ToString();
        var sig = req.Headers[Signing.SignatureHeader].ToString();
        if (ts.Length == 0 || nonce.Length is 0 or > 100 || sig.Length == 0) return new(null, 401, "unsigned", "Missing signature headers.");
        if (!long.TryParse(ts, out var unix)) return new(null, 401, "bad_timestamp", "Invalid timestamp.");
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (unix < 0 || unix > 4_102_444_800L) return new(null, 401, "bad_timestamp", "Invalid timestamp.");   // range first: keeps the arithmetic below overflow-free
        if (Math.Abs(now - unix) > o.TimestampWindowSeconds) return new(null, 401, "stale", "The request timestamp is outside the allowed window.");

        if (req.ContentLength > o.MaxInboundBodyBytes) return new(null, 413, "too_large", "Request body too large.");
        using var ms = new MemoryStream();
        var buf = new byte[8192]; int n;
        var stream = req.Body;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > o.MaxInboundBodyBytes) return new(null, 413, "too_large", "Request body too large.");
        }
        var body = ms.ToArray();
        var canonical = Signing.Canonical(ts, nonce, req.Method, Microsoft.AspNetCore.Http.Extensions.UriHelper.GetEncodedPathAndQuery(req), body);
        var keys = purpose == Signing.ImagePurpose ? (o.ImageAcceptInboundKey ? [Signing.ImageKey(o), o.InboundSecret] : new[] { Signing.ImageKey(o) }) : [o.InboundSecret];
        if (!keys.Any(k => Signing.Verify(k, canonical, sig))) return new(null, 401, "bad_signature", "Signature mismatch.");

        // replay protection: the nonce is only recorded for correctly signed requests, so strangers can't fill the table
        db.InboundNonces.Add(new InboundNonce { Nonce = nonce, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddSeconds(o.TimestampWindowSeconds * 2) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(null, 401, "replayed", "This request (nonce) was already processed."); }
        return new(body, 200, "ok", "");
    }
}

/// <summary>Dev/test destination for outbound events (used when no webhook URL is configured in Development/Testing).</summary>
public class OutboxSink
{
    public record Delivered(Guid Id, string Type, string Body, string Timestamp, string Signature, DateTime At);
    private readonly List<Delivered> _items = [];
    private readonly object _l = new();
    public void Add(Delivered d) { lock (_l) _items.Add(d); }
    public IReadOnlyList<Delivered> All() { lock (_l) return _items.ToList(); }
    public void Clear() { lock (_l) _items.Clear(); }
}

/// <summary>Sequence scopes of the outbox: every event of one seller order, or of one booking, is delivered in the order it was written.</summary>
public static class OutboxScopes
{
    public static string Order(SellerOrder o) => "so:" + o.Id.ToString("N");
    public static string Order(Guid sellerOrderId) => "so:" + sellerOrderId.ToString("N");
    public static string Booking(Guid bookingId) => "bk:" + bookingId.ToString("N");
}

/// <summary>Writes outbound events. Call <see cref="Enqueue"/> BEFORE the SaveChanges that persists the state change: both commit in one transaction.</summary>
public class Outbox(AppDbContext db, TimeProvider time)
{
    /// <param name="orderKey">Optional per-aggregate ordering key: the dispatcher never sends a message while an older one (smaller key) of the same aggregate is still pending.</param>
    /// <summary>The ordered-sequence scope of an event payload: its seller order or its booking, null for everything else (listings, messages, quotes, reports).</summary>
    internal static string? ScopeOf(string payloadJson)
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)?["data"];
        if (Guid.TryParse((string?)data?["order"]?["sellerOrderId"], out var so)) return OutboxScopes.Order(so);
        if (Guid.TryParse((string?)data?["booking"]?["bookingId"], out var bk)) return OutboxScopes.Booking(bk);
        return null;
    }

    /// <param name="scope">Ordered-sequence scope (<see cref="OutboxScopes"/>): the event gets the next sequence number of that scope and is delivered after every earlier event of it. The caller serializes writers of one scope (order version claim, booking lock), so numbers do not tie.</param>
    public OutboxMessage Enqueue(string type, Guid? sellerId, string aggregateId, object data, long? orderKey = null, string? scope = null)
    {
        var id = Guid.NewGuid();
        var now = time.UtcNow();
        var payload = OrderJson.Ser(new { id, type, createdAt = now, data });
        // an event about a seller order (data.order.sellerOrderId) or a booking (data.booking.bookingId) joins that order's / booking's ordered sequence
        if (scope is null && orderKey is null) scope = ScopeOf(payload);
        if (scope is not null && orderKey is null)
        {
            var queued = db.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added && e.Entity.SequenceScope == scope).Select(e => e.Entity.OrderKey ?? 0).DefaultIfEmpty(0).Max();
            var stored = db.Outbox.AsNoTracking().Where(x => x.SequenceScope == scope).Max(x => (long?)x.OrderKey) ?? 0;
            orderKey = Math.Max(queued, stored) + 1;
        }
        var m = new OutboxMessage
        {
            Id = id, EventType = type, SellerId = sellerId, AggregateId = aggregateId, OrderKey = orderKey, SequenceScope = scope, CreatedAt = now, NextAttemptAt = now,
            PayloadJson = payload
        };
        db.Outbox.Add(m);
        return m;
    }
}

/// <summary>Delivers due outbox messages with exponential backoff; after the maximum number of attempts a message becomes Dead (visible to admins).</summary>
public class OutboxDispatcher(AppDbContext db, IOptions<OrderOptions> opt, TimeProvider time, IHttpClientFactory http, OutboxSink sink,
    IHostEnvironment env, ILogger<OutboxDispatcher> log)
{
    /// <summary>An older event of the same seller order / booking is Dead (it never reached the marketplace): the envelope says so, so the consumer knows its view of that order has a gap.</summary>
    internal static string MarkPredecessorDead(string payloadJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)!.AsObject();
        node["predecessorDead"] = true;
        return node.ToJsonString();
    }

    /// <summary>
    /// Delivers what is due. The events of one seller order / booking go out one at a time (each is held back until its predecessor is delivered), so one pass sends only the head of
    /// every sequence: the call repeats passes while any message was settled (delivered or Dead; bounded), otherwise an order with five events would need five worker ticks.
    /// </summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct = default, int batch = 50)
    {
        var delivered = 0;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var (done, settled) = await DispatchPassAsync(ct, batch);
            delivered += done;
            if (settled == 0) break;       // nothing changed state: what is left is in backoff, held back or not due
        }
        return delivered;
    }

    private const int MaxPasses = 25;

    private async Task<(int Delivered, int Settled)> DispatchPassAsync(CancellationToken ct, int batch)
    {
        var o = opt.Value;
        var attempted = 0;
        var now = time.UtcNow();
        var due = await db.Outbox.AsNoTracking().Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now && (m.LockedUntil == null || m.LockedUntil < now))
            .Where(m => m.SequenceScope == null || !db.Outbox.Any(x => x.SequenceScope == m.SequenceScope && x.OrderKey < m.OrderKey && x.Status == OutboxStatus.Pending))   // held back: an older event of its seller order / booking is not delivered yet
            .OrderBy(m => m.CreatedAt).Take(batch).ToListAsync(ct);
        var done = 0;
        foreach (var candidate in due)
        {
            // per-aggregate ordering (listing events): never send version N while an older version of the same listing is still pending (retrying, leased or in backoff)
            if (candidate.OrderKey is { } key)
            {
                if (await db.Outbox.AnyAsync(x => x.AggregateId == candidate.AggregateId && x.OrderKey < key && x.Status == OutboxStatus.Pending, ct)) continue;
                // a newer version of the listing already went out (this one was re-queued after it died): it is obsolete, never send it
                if (await db.Outbox.AnyAsync(x => x.AggregateId == candidate.AggregateId && x.OrderKey > key && x.Status == OutboxStatus.Delivered, ct))
                {
                    var cid = candidate.Id;
                    await db.Outbox.Where(x => x.Id == cid && x.Status == OutboxStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, OutboxStatus.Dead).SetProperty(x => x.LastError, "superseded by a newer version"), ct);
                    continue;
                }
            }
            // ordered sequences (seller order, booking): re-check inside the loop (an earlier candidate of the same batch may just have failed), and note a Dead predecessor
            var predecessorDead = false;
            if (candidate.SequenceScope is { } scope && candidate.OrderKey is { } seq)
            {
                if (await db.Outbox.AnyAsync(x => x.SequenceScope == scope && x.OrderKey < seq && x.Status == OutboxStatus.Pending, ct)) continue;
                predecessorDead = await db.Outbox.AnyAsync(x => x.SequenceScope == scope && x.OrderKey < seq && x.Status == OutboxStatus.Dead, ct);
            }
            // claim atomically: only the dispatcher whose UPDATE touches the row sends it (lease expires if that instance dies mid-send)
            var id = candidate.Id;
            var lease = time.UtcNow().AddMinutes(2);
            var claimed = await db.Outbox.Where(x => x.Id == id && x.Status == OutboxStatus.Pending && (x.LockedUntil == null || x.LockedUntil < now))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedUntil, lease), ct);
            if (claimed != 1) continue;
            var m = await db.Outbox.AsNoTracking().FirstAsync(x => x.Id == id, ct);
            var ok = false; string? err = null;
            var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
            var body = predecessorDead ? MarkPredecessorDead(m.PayloadJson) : m.PayloadJson;   // the marker is part of the signed body; the stored payload is untouched
            var sig = Signing.SignOutbound(o.OutboundSecret, ts, body);
            try
            {
                if (string.IsNullOrEmpty(o.WebhookUrl))
                {
                    if (env.IsDevelopment() || env.IsEnvironment("Testing")) { sink.Add(new(m.Id, m.EventType, body, ts, sig, now)); ok = true; }
                    else err = "No webhook URL configured";
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, o.WebhookUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    req.Headers.Add(Signing.TimestampHeader, ts);
                    req.Headers.Add(Signing.SignatureHeader, sig);
                    req.Headers.Add(Signing.EventIdHeader, m.Id.ToString());
                    req.Headers.Add(Signing.EventTypeHeader, m.EventType);
                    using var res = await http.CreateClient("outbox").SendAsync(req, ct);
                    ok = res.IsSuccessStatusCode;
                    if (!ok) err = $"HTTP {(int)res.StatusCode}";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { err = ex.GetType().Name; }

            var attempts = m.Attempts + 1;
            var status = OutboxStatus.Pending; DateTime? deliveredAt = null; var next = m.NextAttemptAt; string? lastErr = err;
            if (ok) { status = OutboxStatus.Delivered; deliveredAt = time.UtcNow(); lastErr = null; done++; }
            else if (attempts >= o.OutboxMaxAttempts) { status = OutboxStatus.Dead; log.LogWarning("Outbox message {Id} ({Type}) is dead after {N} attempts", m.Id, m.EventType, attempts); }
            else next = time.UtcNow().AddSeconds(Math.Min(3600, o.OutboxBackoffSeconds * Math.Pow(2, attempts - 1)));
            if (status != OutboxStatus.Pending) attempted++;      // settled (delivered or dead): successors of its sequence may be eligible now. A failed attempt goes into backoff and unblocks nothing.
            await db.Outbox.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, attempts)
                .SetProperty(x => x.DeliveredAt, deliveredAt).SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LastError, lastErr).SetProperty(x => x.LockedUntil, (DateTime?)null), CancellationToken.None);
        }
        return (done, attempted);
    }
}

public class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var rec = scope.ServiceProvider.GetRequiredService<Solvelt.Api.Admin.JobRecorder>();
                await rec.RunAsync("outbox-dispatcher", async () => new { sent = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync(stop) });
                await rec.RunAsync("order-maintenance", () => scope.ServiceProvider.GetRequiredService<OrderMaintenance>().RunAsync(stop));
                await rec.RunAsync("catalog-refresh", async () => new { bumped = await scope.ServiceProvider.GetRequiredService<Solvelt.Api.Catalog.CatalogRefreshJob>().RunAsync(stop) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Order background job failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { }
        }
    }
}
