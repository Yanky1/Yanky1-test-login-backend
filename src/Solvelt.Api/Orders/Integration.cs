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
}

/// <summary>Verifies signed inbound requests: headers present, timestamp in window, signature valid, nonce unused. Returns the raw body on success.</summary>
public class InboundVerifier(IOptions<OrderOptions> opt, AppDbContext db, TimeProvider time)
{
    public record Result(byte[]? Body, int Status, string Code, string Message)
    {
        public bool Ok => Body is not null;
    }

    public async Task<Result> VerifyAsync(HttpRequest req, CancellationToken ct)
    {
        var res = await VerifyCoreAsync(req, ct);
        // rejected signatures / stale timestamps / replays are counted for the staff integration-health page (no payload, no secrets)
        if (!res.Ok && res.Code is "bad_signature" or "stale" or "replayed" or "bad_timestamp" or "unsigned")
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

    private async Task<Result> VerifyCoreAsync(HttpRequest req, CancellationToken ct)
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
        if (!Signing.Verify(o.InboundSecret, canonical, sig)) return new(null, 401, "bad_signature", "Signature mismatch.");

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

/// <summary>Writes outbound events. Call <see cref="Enqueue"/> BEFORE the SaveChanges that persists the state change: both commit in one transaction.</summary>
public class Outbox(AppDbContext db, TimeProvider time)
{
    /// <param name="orderKey">Optional per-aggregate ordering key: the dispatcher never sends a message while an older one (smaller key) of the same aggregate is still pending.</param>
    public OutboxMessage Enqueue(string type, Guid? sellerId, string aggregateId, object data, long? orderKey = null)
    {
        var id = Guid.NewGuid();
        var now = time.UtcNow();
        var m = new OutboxMessage
        {
            Id = id, EventType = type, SellerId = sellerId, AggregateId = aggregateId, OrderKey = orderKey, CreatedAt = now, NextAttemptAt = now,
            PayloadJson = OrderJson.Ser(new { id, type, createdAt = now, data })
        };
        db.Outbox.Add(m);
        return m;
    }
}

/// <summary>Delivers due outbox messages with exponential backoff; after the maximum number of attempts a message becomes Dead (visible to admins).</summary>
public class OutboxDispatcher(AppDbContext db, IOptions<OrderOptions> opt, TimeProvider time, IHttpClientFactory http, OutboxSink sink,
    IHostEnvironment env, ILogger<OutboxDispatcher> log)
{
    public async Task<int> DispatchDueAsync(CancellationToken ct = default, int batch = 50)
    {
        var o = opt.Value;
        var now = time.UtcNow();
        var due = await db.Outbox.AsNoTracking().Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now && (m.LockedUntil == null || m.LockedUntil < now))
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
            // claim atomically: only the dispatcher whose UPDATE touches the row sends it (lease expires if that instance dies mid-send)
            var id = candidate.Id;
            var lease = time.UtcNow().AddMinutes(2);
            var claimed = await db.Outbox.Where(x => x.Id == id && x.Status == OutboxStatus.Pending && (x.LockedUntil == null || x.LockedUntil < now))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedUntil, lease), ct);
            if (claimed != 1) continue;
            var m = await db.Outbox.AsNoTracking().FirstAsync(x => x.Id == id, ct);
            var ok = false; string? err = null;
            var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
            var sig = Signing.SignOutbound(o.OutboundSecret, ts, m.PayloadJson);
            try
            {
                if (string.IsNullOrEmpty(o.WebhookUrl))
                {
                    if (env.IsDevelopment() || env.IsEnvironment("Testing")) { sink.Add(new(m.Id, m.EventType, m.PayloadJson, ts, sig, now)); ok = true; }
                    else err = "No webhook URL configured";
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, o.WebhookUrl) { Content = new StringContent(m.PayloadJson, Encoding.UTF8, "application/json") };
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
            await db.Outbox.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, attempts)
                .SetProperty(x => x.DeliveredAt, deliveredAt).SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LastError, lastErr).SetProperty(x => x.LockedUntil, (DateTime?)null), CancellationToken.None);
        }
        return done;
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
