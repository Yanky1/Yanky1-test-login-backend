using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvelt.Api.Orders;
using Solvelt.Api.Services;

namespace Solvelt.Api.Shipping;

public record ShipAddress(string Name, string? Company, string Line1, string? Line2, string City, string? Region, string PostalCode, string Country, string? Phone, string? Email);
public record ParcelSpec(int LengthMm, int WidthMm, int HeightMm, int WeightGrams);
public record CustomsItem(string Description, int Quantity, long ValueCents, string Currency, int WeightGrams, string? HsCode, string OriginCountry);
public record CustomsSpec(string ContentsType, string NonDeliveryOption, List<CustomsItem> Items);
public record RateRequest(ShipAddress From, ShipAddress To, ParcelSpec Parcel, CustomsSpec? Customs, bool IsReturn);
public record RateOffer(string RateId, string Carrier, string Service, long AmountCents, string Currency, int? EstimatedDays, DateTime? EstimatedDelivery);
public record RateSet(string ProviderShipmentId, List<RateOffer> Rates);
public record AddressCheck(string Status, string? Message, ShipAddress? Suggested);
public record PurchaseRequest(string RateId, string ProviderShipmentId, string LabelFormat, string IdempotencyKey);
public record PurchasedLabel(string TransactionId, string Carrier, string? Service, string TrackingNumber, byte[] Bytes, string ContentType, DateTime? EstimatedDelivery);
/// <summary>Status: pending, success or error.</summary>
public record RefundResult(string RefundId, string Status, string? Error);
/// <summary><c>AtEstimated</c>: the carrier sent no date, so <c>At</c> is only "when we heard it" (history dedupes on a stable key instead of that time).</summary>
public record TrackingEntry(string Status, string? Detail, string? Location, DateTime At, bool AtEstimated = false);
public record TrackingSnapshot(string Carrier, string TrackingNumber, string Status, string? Detail, DateTime StatusAt, DateTime? Eta, string? Location, List<TrackingEntry> History, bool StatusAtEstimated = false);

/// <summary>Provider failure with a stable code (rate_expired, invalid_address, provider_error, not_configured); Retryable means "try again later".</summary>
public class ShippingProviderException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Everything the app needs from a carrier aggregator. Implementations: Shippo (test mode in this iteration), the deterministic fake, and "unconfigured".</summary>
public interface IShippingProvider
{
    string Name { get; }
    Task<AddressCheck> ValidateAddressAsync(ShipAddress a, CancellationToken ct = default);
    Task<RateSet> GetRatesAsync(RateRequest r, CancellationToken ct = default);
    Task<PurchasedLabel> PurchaseAsync(PurchaseRequest r, CancellationToken ct = default);
    /// <summary>Looks up a purchase this server may have started and lost track of (crash mid-purchase) by the idempotency key sent as its metadata. Null when the provider has none.</summary>
    Task<PurchasedLabel?> FindPurchaseAsync(string idempotencyKey, string labelFormat, DateTime since, CancellationToken ct = default);
    Task<RefundResult> RequestRefundAsync(string transactionId, CancellationToken ct = default);
    Task<RefundResult> GetRefundAsync(string refundId, CancellationToken ct = default);
    /// <summary>Asks the provider to send webhooks for this parcel. False: the carrier is not supported (the reconciler still polls where it can).</summary>
    Task<bool> RegisterTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default);
    Task<TrackingSnapshot?> GetTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default);
}

/// <summary>Money from provider decimals to integer minor units. Rule (documented in docs/SHIPPING.md): round half away from zero at the currency's last digit.</summary>
public static class ShippingMoney
{
    private static readonly HashSet<string> ZeroDecimal = ["JPY", "KRW", "VND", "CLP"];
    public static long ToMinor(decimal amount, string currency) =>
        (long)Math.Round(amount * (ZeroDecimal.Contains(currency.ToUpperInvariant()) ? 1m : 100m), MidpointRounding.AwayFromZero);
    public static long ToMinor(string? amount, string currency) =>
        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? ToMinor(d, currency) : throw new ShippingProviderException("provider_error", "The provider returned an invalid amount.");
}

public static class LabelFormats
{
    public static readonly string[] All = ["PDF_4x6", "PNG", "ZPLII"];
    public static string ContentType(string f) => f switch { "PNG" => "image/png", "ZPLII" => "application/zpl", _ => "application/pdf" };
    public static string Extension(string f) => f switch { "PNG" => "png", "ZPLII" => "zpl", _ => "pdf" };
}

public class UnconfiguredShippingProvider : IShippingProvider
{
    public string Name => "none";
    private static ShippingProviderException Off() => new("not_configured", "Shipping labels are not configured on this server.");
    public Task<AddressCheck> ValidateAddressAsync(ShipAddress a, CancellationToken ct = default) => throw Off();
    public Task<RateSet> GetRatesAsync(RateRequest r, CancellationToken ct = default) => throw Off();
    public Task<PurchasedLabel> PurchaseAsync(PurchaseRequest r, CancellationToken ct = default) => throw Off();
    public Task<PurchasedLabel?> FindPurchaseAsync(string idempotencyKey, string labelFormat, DateTime since, CancellationToken ct = default) => throw Off();
    public Task<RefundResult> RequestRefundAsync(string transactionId, CancellationToken ct = default) => throw Off();
    public Task<RefundResult> GetRefundAsync(string refundId, CancellationToken ct = default) => throw Off();
    public Task<bool> RegisterTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default) => Task.FromResult(false);
    public Task<TrackingSnapshot?> GetTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default) => Task.FromResult<TrackingSnapshot?>(null);
}

/// <summary>
/// Deterministic fake used when no Shippo key is configured (dev, tests, E2E; CI never calls Shippo). Rates are a pure function of weight and
/// destination, tracking numbers come from a counter, and tests steer it through the public knobs.
/// Address rules: "INVALID" in line 1 is invalid, "UNVERIFIED" in line 1 or postal code 00000 is unverified, anything else is valid.
/// </summary>
public class FakeShippingProvider(TimeProvider time) : IShippingProvider
{
    public string Name => "fake";
    private long _seq = 1000;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Carrier, string Service, long Cents, string Cur, int Days)> _rates = [];
    private readonly Dictionary<string, TrackingSnapshot> _tracking = [];
    private readonly Dictionary<string, string> _refundStatus = [];
    private readonly Dictionary<string, PurchasedLabel> _byKey = [];

    // ---- knobs and counters for tests ----
    public bool ExpireRates { get; set; }
    /// <summary>The currency the fake quotes rates in (a carrier account billed in another currency than the order).</summary>
    public string RateCurrency { get; set; } = "USD";
    public string? FailPurchaseWith { get; set; }
    public int PurchaseDelayMs { get; set; }
    /// <summary>Makes a tracking poll take this long (a slow carrier): lets a test keep one instance mid-poll while another tries to claim the same shipment.</summary>
    public int PollDelayMs { get; set; }
    /// <summary>pending_then_success (default: pending on request, success when polled), success, hold (stays pending), error.</summary>
    public string RefundMode { get; set; } = "pending_then_success";
    public bool FailRegistration { get; set; }
    /// <summary>The stuck-purchase lookup cannot finish (provider down / search incomplete).</summary>
    public bool FailLookup { get; set; }
    /// <summary>A poll of this tracking number blows up with an unexpected (non-provider) exception: proves one bad parcel never stops the reconciler.</summary>
    public string? ThrowForTracking { get; set; }
    public bool UnsupportedCarriers { get; set; }
    public int Purchases, RateCalls, RefundRequests, Registrations, TrackingPolls;
    public List<string> Registered { get; } = [];

    private long Next() => Interlocked.Increment(ref _seq);

    public Task<AddressCheck> ValidateAddressAsync(ShipAddress a, CancellationToken ct = default)
    {
        var l = a.Line1.ToUpperInvariant();
        if (l.Contains("INVALID")) return Task.FromResult(new AddressCheck("invalid", "We couldn't find this address.", null));
        if (l.Contains("UNVERIFIED") || a.PostalCode == "00000") return Task.FromResult(new AddressCheck("unverified", "The address could not be fully verified.", null));
        return Task.FromResult(new AddressCheck("valid", null, a with { Line1 = a.Line1.Trim(), City = a.City.Trim() }));
    }

    public Task<RateSet> GetRatesAsync(RateRequest r, CancellationToken ct = default)
    {
        Interlocked.Increment(ref RateCalls);
        var w = Math.Max(r.Parcel.WeightGrams, 1);
        var intl = !string.Equals(r.From.Country, r.To.Country, StringComparison.OrdinalIgnoreCase);
        var shp = "fake_shp_" + Next();
        (string C, string S, long Cents, int Days)[] plan = intl
            ? [("USPS", "First-Class Package International", 1500 + w / 6, 10), ("DHL", "Express Worldwide", 4500 + w / 4, 4)]
            : [("USPS", "Ground Advantage", 400 + w / 10, 5), ("USPS", "Priority Mail", 800 + w / 8, 3), ("UPS", "Ground", 950 + w / 9, 4), ("FedEx", "Overnight", 3000 + w / 5, 1)];
        var today = time.GetUtcNow().UtcDateTime.Date;
        var list = new List<RateOffer>();
        lock (_gate)
            foreach (var p in plan)
            {
                var id = "fake_rate_" + Next();
                _rates[id] = (p.C, p.S, p.Cents, RateCurrency, p.Days);
                list.Add(new RateOffer(id, p.C, p.S, p.Cents, RateCurrency, p.Days, today.AddDays(p.Days)));
            }
        return Task.FromResult(new RateSet(shp, list));
    }

    public async Task<PurchasedLabel> PurchaseAsync(PurchaseRequest r, CancellationToken ct = default)
    {
        if (PurchaseDelayMs > 0) await Task.Delay(PurchaseDelayMs, ct);
        if (FailPurchaseWith is { } f) throw new ShippingProviderException(f, "The provider could not buy this label.", f == "provider_error");
        (string Carrier, string Service, long Cents, string Cur, int Days) rate;
        lock (_gate)
        {
            if (ExpireRates || !_rates.TryGetValue(r.RateId, out rate)) throw new ShippingProviderException("rate_expired", "This rate is no longer available.");
            Interlocked.Increment(ref Purchases);
        }
        var n = Next();
        var tn = rate.Carrier switch
        {
            "UPS" => "1Z999AA1" + (10000000 + n % 90000000).ToString(CultureInfo.InvariantCulture),
            "FedEx" => (100000000000L + n).ToString(CultureInfo.InvariantCulture),
            "DHL" => (1000000000L + n).ToString(CultureInfo.InvariantCulture),
            _ => "9400111899223" + (100000000L + n).ToString(CultureInfo.InvariantCulture)
        };
        var bytes = r.LabelFormat switch
        {
            "PNG" => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="),
            "ZPLII" => Encoding.ASCII.GetBytes($"^XA^FO50,50^FDFAKE LABEL {tn}^FS^XZ"),
            _ => Encoding.ASCII.GetBytes($"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 288 432]>>endobj\ntrailer<</Root 1 0 R>>\n%% FAKE LABEL {tn}\n%%EOF")
        };
        var tx = "fake_tx_" + n;
        lock (_gate) _tracking[tn] = new TrackingSnapshot(rate.Carrier, tn, TrackingStatuses.PreTransit, "Label created", time.GetUtcNow().UtcDateTime, null, null, []);
        var bought = new PurchasedLabel(tx, rate.Carrier, rate.Service, tn, bytes, LabelFormats.ContentType(r.LabelFormat), time.GetUtcNow().UtcDateTime.Date.AddDays(rate.Days));
        lock (_gate) _byKey[r.IdempotencyKey] = bought;
        return bought;
    }

    public Task<PurchasedLabel?> FindPurchaseAsync(string idempotencyKey, string labelFormat, DateTime since, CancellationToken ct = default)
    {
        if (FailLookup) throw new ShippingProviderException("provider_error", "The purchase search could not finish.", true);
        lock (_gate) return Task.FromResult(_byKey.TryGetValue(idempotencyKey, out var p) ? p : null);
    }

    public Task<RefundResult> RequestRefundAsync(string transactionId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref RefundRequests);
        var id = "fake_refund_" + transactionId;
        lock (_gate) _refundStatus[id] = RefundMode == "success" ? "success" : RefundMode == "error" ? "error" : "pending";
        return Task.FromResult(new RefundResult(id, _refundStatus[id], _refundStatus[id] == "error" ? "The carrier refused the refund." : null));
    }

    public Task<RefundResult> GetRefundAsync(string refundId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var cur = _refundStatus.GetValueOrDefault(refundId, "pending");
            if (cur == "pending" && RefundMode == "pending_then_success") cur = _refundStatus[refundId] = "success";
            if (cur == "pending" && RefundMode == "error") cur = _refundStatus[refundId] = "error";
            return Task.FromResult(new RefundResult(refundId, cur, cur == "error" ? "The carrier refused the refund." : null));
        }
    }

    public Task<bool> RegisterTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default)
    {
        if (FailRegistration) throw new ShippingProviderException("provider_error", "Registration failed.", true);
        if (UnsupportedCarriers || carrier == "Other") return Task.FromResult(false);
        Interlocked.Increment(ref Registrations);
        lock (_gate) Registered.Add(trackingNumber);
        return Task.FromResult(true);
    }

    public async Task<TrackingSnapshot?> GetTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default)
    {
        Interlocked.Increment(ref TrackingPolls);
        if (PollDelayMs > 0) await Task.Delay(PollDelayMs, ct);
        if (ThrowForTracking == trackingNumber) throw new InvalidOperationException("simulated parcel failure");
        lock (_gate) return _tracking.TryGetValue(trackingNumber, out var s) ? s : null;
    }

    /// <summary>Test helper: what a poll of this tracking number returns.</summary>
    public void Script(string carrier, string tracking, string status, DateTime at, string? detail = null)
    {
        lock (_gate)
        {
            var hist = _tracking.TryGetValue(tracking, out var old) ? old.History : [];
            hist = [.. hist, new TrackingEntry(status, detail, null, at)];
            _tracking[tracking] = new TrackingSnapshot(carrier, tracking, status, detail, at, null, null, hist);
        }
    }
}

/// <summary>Shippo REST client (https://docs.goshippo.com). Test mode only in this iteration: keys must be shippo_test_... unless explicitly allowed.</summary>
public class ShippoShippingProvider(HttpClient http, IOptions<ShippingOptions> opt, ILogger<ShippoShippingProvider> log, TimeProvider? clock = null) : IShippingProvider
{
    public string Name => "shippo";
    private static readonly Dictionary<string, string> Tokens = new() { ["UPS"] = "ups", ["USPS"] = "usps", ["FedEx"] = "fedex", ["DHL"] = "dhl_express" };
    private static readonly Dictionary<string, string> Names = new() { ["ups"] = "UPS", ["usps"] = "USPS", ["fedex"] = "FedEx", ["dhl_express"] = "DHL" };
    public static string CarrierName(string? token) => token is not null && Names.TryGetValue(token.ToLowerInvariant(), out var n) ? n : "Other";

    private async Task<JsonNode> Send(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, "https://api.goshippo.com" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("ShippoToken", opt.Value.ShippoApiKey);   // the key is never logged
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new ShippingProviderException("provider_error", "The shipping provider could not be reached.", true); }
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Shippo {Method} {Path} -> {Status}", m, path, (int)res.StatusCode);
            throw new ShippingProviderException("provider_error", "The shipping provider rejected the request.", (int)res.StatusCode >= 500 || (int)res.StatusCode == 429);
        }
        return JsonNode.Parse(text) ?? throw new ShippingProviderException("provider_error", "Empty provider response.", true);
    }

    private static JsonObject Addr(ShipAddress a, bool validate = false) => new()
    {
        ["name"] = a.Name, ["company"] = a.Company, ["street1"] = a.Line1, ["street2"] = a.Line2, ["city"] = a.City, ["state"] = a.Region, ["zip"] = a.PostalCode,
        ["country"] = a.Country, ["phone"] = a.Phone, ["email"] = a.Email, ["validate"] = validate
    };

    public async Task<AddressCheck> ValidateAddressAsync(ShipAddress a, CancellationToken ct = default)
    {
        var r = await Send(HttpMethod.Post, "/addresses/", Addr(a, true), ct);
        var v = r["validation_results"];
        var ok = v?["is_valid"]?.GetValue<bool>() ?? false;
        var msg = v?["messages"]?.AsArray().Select(x => x?["text"]?.GetValue<string>()).FirstOrDefault(x => x != null);
        return new AddressCheck(ok ? "valid" : (msg is null ? "unverified" : "invalid"), ok ? null : msg ?? "The address could not be verified.", null);
    }

    public async Task<RateSet> GetRatesAsync(RateRequest r, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["address_from"] = Addr(r.From), ["address_to"] = Addr(r.To), ["async"] = false,
            ["parcels"] = new JsonArray(new JsonObject
            {
                ["length"] = r.Parcel.LengthMm.ToString(CultureInfo.InvariantCulture), ["width"] = r.Parcel.WidthMm.ToString(CultureInfo.InvariantCulture),
                ["height"] = r.Parcel.HeightMm.ToString(CultureInfo.InvariantCulture), ["distance_unit"] = "mm",
                ["weight"] = r.Parcel.WeightGrams.ToString(CultureInfo.InvariantCulture), ["mass_unit"] = "g"
            })
        };
        if (r.IsReturn) body["extra"] = new JsonObject { ["is_return"] = true };
        if (r.Customs is { } c)
            body["customs_declaration"] = new JsonObject
            {
                ["contents_type"] = c.ContentsType.ToUpperInvariant(), ["non_delivery_option"] = c.NonDeliveryOption.ToUpperInvariant(), ["certify"] = true, ["certify_signer"] = r.From.Name,
                ["items"] = new JsonArray(c.Items.Select(i => (JsonNode)new JsonObject
                {
                    ["description"] = i.Description, ["quantity"] = i.Quantity, ["net_weight"] = i.WeightGrams.ToString(CultureInfo.InvariantCulture), ["mass_unit"] = "g",
                    ["value_amount"] = (i.ValueCents / 100m).ToString("0.00", CultureInfo.InvariantCulture), ["value_currency"] = i.Currency, ["origin_country"] = i.OriginCountry, ["tariff_number"] = i.HsCode
                }).ToArray())
            };
        var res = await Send(HttpMethod.Post, "/shipments/", body, ct);
        var rates = (res["rates"]?.AsArray() ?? []).Select(x =>
        {
            var cur = x!["currency"]!.GetValue<string>();
            var days = x["estimated_days"]?.GetValue<int>();
            return new RateOffer(x["object_id"]!.GetValue<string>(), CarrierName(x["provider"]?.GetValue<string>()?.ToLowerInvariant().Replace(" ", "_")),
                x["servicelevel"]?["name"]?.GetValue<string>() ?? "", ShippingMoney.ToMinor(x["amount"]!.GetValue<string>(), cur), cur, days,
                days is { } d ? (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.Date.AddDays(d) : null);
        }).ToList();
        return new RateSet(res["object_id"]!.GetValue<string>(), rates);
    }

    public async Task<PurchasedLabel> PurchaseAsync(PurchaseRequest r, CancellationToken ct = default)
    {
        var tx = await Send(HttpMethod.Post, "/transactions/", new JsonObject
        {
            ["rate"] = r.RateId, ["label_file_type"] = r.LabelFormat, ["async"] = false, ["metadata"] = r.IdempotencyKey.Length > 100 ? r.IdempotencyKey[..100] : r.IdempotencyKey
        }, ct);
        var status = tx["status"]?.GetValue<string>();
        if (status != "SUCCESS")
        {
            var text = string.Join(" ", (tx["messages"]?.AsArray() ?? []).Select(m => m?["text"]?.GetValue<string>()));
            throw new ShippingProviderException(text.Contains("expire", StringComparison.OrdinalIgnoreCase) ? "rate_expired" : "provider_error", "The carrier did not issue the label.", status is "QUEUED" or "WAITING");
        }
        var url = tx["label_url"]!.GetValue<string>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new ShippingProviderException("provider_error", "The label address is not https.");
        var bytes = await http.GetByteArrayAsync(u, ct);
        var rate = await Send(HttpMethod.Get, "/rates/" + r.RateId, null, ct);
        var days = rate["estimated_days"]?.GetValue<int>();
        return new PurchasedLabel(tx["object_id"]!.GetValue<string>(), CarrierName(rate["provider"]?.GetValue<string>()?.ToLowerInvariant().Replace(" ", "_")),
            rate["servicelevel"]?["name"]?.GetValue<string>(), tx["tracking_number"]!.GetValue<string>(), bytes, LabelFormats.ContentType(r.LabelFormat),
            days is { } d ? (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.Date.AddDays(d) : null);
    }

    /// <summary>Pages through the transactions (newest first) until it passes <paramref name="since"/> minus a margin: an older stuck purchase must still be found, and only a COMPLETE search may conclude "never bought".</summary>
    public async Task<PurchasedLabel?> FindPurchaseAsync(string idempotencyKey, string labelFormat, DateTime since, CancellationToken ct = default)
    {
        var key = idempotencyKey.Length > 100 ? idempotencyKey[..100] : idempotencyKey;
        var oldest = since.AddHours(-1);
        var path = "/transactions/?results=100"; JsonNode? tx = null; var searched = false;
        for (var page = 0; page < 50 && path is not null; page++)
        {
            var list = await Send(HttpMethod.Get, path, null, ct);
            var rows = list["results"]?.AsArray() ?? [];
            tx = rows.FirstOrDefault(x => x?["metadata"]?.GetValue<string>() == key && x["status"]?.GetValue<string>() == "SUCCESS");
            if (tx is not null) break;
            var next = list["next"]?.GetValue<string>();
            DateTime? last = rows.Count > 0 && rows[^1]?["object_created"]?.GetValue<string>() is { } oc && DateTime.TryParse(oc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            if (string.IsNullOrEmpty(next) || rows.Count == 0 || last is { } l && l < oldest) { searched = true; break; }
            path = Uri.TryCreate(next, UriKind.Absolute, out var u) && u.Host == "api.goshippo.com" ? u.PathAndQuery : null;
        }
        if (tx is null)
        {
            if (!searched) throw new ShippingProviderException("provider_error", "The purchase could not be searched completely.", true);   // never conclude "not bought" from a partial search
            return null;
        }
        var url = tx["label_url"]?.GetValue<string>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var lu) || lu.Scheme != Uri.UriSchemeHttps) throw new ShippingProviderException("provider_error", "The label address is not https.");
        var bytes = await http.GetByteArrayAsync(lu, ct);
        var rate = await Send(HttpMethod.Get, "/rates/" + tx["rate"]!.GetValue<string>(), null, ct);
        var days = rate["estimated_days"]?.GetValue<int>();
        return new PurchasedLabel(tx["object_id"]!.GetValue<string>(), CarrierName(rate["provider"]?.GetValue<string>()?.ToLowerInvariant().Replace(" ", "_")),
            rate["servicelevel"]?["name"]?.GetValue<string>(), tx["tracking_number"]!.GetValue<string>(), bytes, LabelFormats.ContentType(labelFormat),
            days is { } d ? (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.Date.AddDays(d) : null);
    }

    private static RefundResult Refund(JsonNode n) => new(n["object_id"]!.GetValue<string>(), (n["status"]?.GetValue<string>() ?? "").ToUpperInvariant() switch
    { "SUCCESS" => "success", "ERROR" => "error", _ => "pending" }, null);

    public async Task<RefundResult> RequestRefundAsync(string transactionId, CancellationToken ct = default) =>
        Refund(await Send(HttpMethod.Post, "/refunds/", new JsonObject { ["transaction"] = transactionId, ["async"] = false }, ct));

    public async Task<RefundResult> GetRefundAsync(string refundId, CancellationToken ct = default) =>
        Refund(await Send(HttpMethod.Get, "/refunds/" + Uri.EscapeDataString(refundId), null, ct));

    public async Task<bool> RegisterTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default)
    {
        if (!Tokens.TryGetValue(carrier, out var token)) return false;
        await Send(HttpMethod.Post, "/tracks/", new JsonObject { ["carrier"] = token, ["tracking_number"] = trackingNumber }, ct);
        return true;
    }

    public async Task<TrackingSnapshot?> GetTrackingAsync(string carrier, string trackingNumber, CancellationToken ct = default)
    {
        if (!Tokens.TryGetValue(carrier, out var token)) return null;
        var n = await Send(HttpMethod.Get, $"/tracks/{token}/{Uri.EscapeDataString(trackingNumber)}", null, ct);
        return ShippoPayload.Snapshot(n, carrier, trackingNumber, heardAt: (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }
}

/// <summary>Parses a Shippo tracking object (the webhook's data and the GET /tracks response share this shape).</summary>
public static class ShippoPayload
{
    private static DateTime? Date(JsonNode? n) => n?.GetValue<string>() is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static TrackingEntry? Entry(JsonNode? e, DateTime? heardAt = null)
    {
        if (e is null) return null;
        // no carrier time: the CURRENT status counts as heard now (so a real delivery without a date still delivers); a history row without a date is the oldest fact
        var given = Date(e["status_date"]); var at = given ?? heardAt ?? DateTime.UnixEpoch;
        var loc = e["location"] is { } l ? string.Join(", ", new[] { l["city"], l["state"], l["country"] }.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))) : null;
        return new TrackingEntry(TrackingStatuses.FromShippo(e["status"]?.GetValue<string>(), e["substatus"]?["code"]?.GetValue<string>()), e["status_details"]?.GetValue<string>(), loc, at, given is null);
    }

    public static TrackingSnapshot? Snapshot(JsonNode data, string? carrier = null, string? tracking = null, DateTime? heardAt = null)
    {
        var cur = Entry(data["tracking_status"], heardAt);
        if (cur is null) return null;
        var hist = (data["tracking_history"]?.AsArray() ?? []).Select(h => Entry(h)).Where(x => x != null).Select(x => x!).ToList();
        if (!hist.Any(h => h.At == cur.At && h.Status == cur.Status)) hist.Add(cur);
        return new TrackingSnapshot(carrier ?? ShippoShippingProvider.CarrierName(data["carrier"]?.GetValue<string>()), tracking ?? data["tracking_number"]?.GetValue<string>() ?? "",
            cur.Status, cur.Detail, cur.At, Date(data["eta"]), cur.Location, hist, cur.AtEstimated);
    }
}
