using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Api.Domain;
using Solvelt.Api.Orders;

namespace Solvelt.Api.Shipping;

/// <summary>Configuration section "Shipping" (docs/SHIPPING.md). Keys come only from configuration / environment (SHIPPO_API_KEY), never from code.</summary>
public class ShippingOptions
{
    public const string Section = "Shipping";
    public string ShippoApiKey { get; set; } = "";
    /// <summary>Verifies tracking webhooks (HMAC signature header, or ?token= for providers that cannot sign).</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>Signs the short-lived label links the marketplace hands to buyers. Its own secret: the Orders secrets are shared with the marketplace, which must not be able to mint label links.</summary>
    public string LinkKey { get; set; } = "";
    public bool AllowTestKeyInProduction { get; set; }
    public bool AllowLiveKeyOutsideProduction { get; set; }
    public string LabelFormat { get; set; } = "PDF_4x6";
    /// <summary>How long a rate quote can be bought (minutes).</summary>
    public int QuoteMinutes { get; set; } = 15;
    /// <summary>Per account and UTC month; 0 = unlimited.</summary>
    public long SpendLimitCentsPerMonth { get; set; }
    /// <summary>Lifetime of the signed return-label link sent to the marketplace (minutes).</summary>
    public int LabelLinkMinutes { get; set; } = 1440;
    /// <summary>Public base URL of this API (for signed label links).</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost";
    public int TrackingPollAfterHours { get; set; } = 24;
    public int TrackingStopDays { get; set; } = 60;
    public int RefundCheckMinutes { get; set; } = 30;
    public int PollBatch { get; set; } = 50;
    /// <summary>A label still "Purchasing" after this long was lost to a crash: the sweep asks the provider whether it was bought.</summary>
    public int PurchaseStuckMinutes { get; set; } = 10;
    /// <summary>A purchase the provider search could not resolve for this many days stops being retried and is flagged for staff (a Failed label with RefundStatus "review").</summary>
    public int PurchaseAbandonDays { get; set; } = 3;
    /// <summary>A carrier "delivered" may be stamped this long before a MANUALLY entered shipment's entry time (the number is often typed in after drop-off).</summary>
    public int ManualDeliveryGraceHours { get; set; } = 48;
    public int PollLeaseMinutes { get; set; } = 5;
    public int JobIntervalSeconds { get; set; } = 300;
    public int WebhookToleranceSeconds { get; set; } = 300;
    public int WebhooksPerMinute { get; set; } = 600;
    public int MaxBulk { get; set; } = 25;
    public bool RunBackgroundJobs { get; set; } = true;
}

/// <summary>Short-lived signed links to a label file (return labels for the buyer, via the marketplace). HMAC over "label:{id}:{exp}".</summary>
public class LabelLinks(IOptions<ShippingOptions> opt, TimeProvider time)
{
    private byte[] Key => SHA256.HashData(Encoding.UTF8.GetBytes("solvelt-label-link|" + opt.Value.LinkKey));
    private string Sig(Guid id, long exp) => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"label:{id:N}:{exp}"))).ToLowerInvariant();

    public (string Url, DateTime ExpiresAt) Create(Guid labelId)
    {
        var exp = time.GetUtcNow().AddMinutes(opt.Value.LabelLinkMinutes);
        var e = exp.ToUnixTimeSeconds();
        return ($"{opt.Value.PublicBaseUrl.TrimEnd('/')}/api/v1/shipping/public/labels/{labelId:N}?exp={e}&sig={Sig(labelId, e)}", exp.UtcDateTime);
    }

    public bool Valid(Guid id, long exp, string? sig) =>
        exp >= time.GetUtcNow().ToUnixTimeSeconds() && sig is { Length: 64 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sig(id, exp)), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
}

public static class ShippingSupport
{
    public static readonly string[] Speeds = ["standard", "expedited", "overnight"];

    public static ShipAddress ToShip(this ShipFromAddress a) => new(a.Name, a.Company, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.Phone, a.Email);
    public static ShipAddress BuyerAddress(SellerOrder o) => new(o.ShipName, null, o.ShipLine1, o.ShipLine2, o.ShipCity, o.ShipRegion, o.ShipPostalCode, o.ShipCountry, o.BuyerPhone, null);

    /// <summary>Provider carrier name mapped to the carrier list the order flow knows ("Other" otherwise).</summary>
    public static string OrderCarrier(string carrier) => Tracking.Carrier(carrier) ?? "Other";

    /// <summary>Masked address for logs: country and postal prefix only.</summary>
    public static string MaskAddress(ShipAddress a) => $"{a.Country}/{(a.PostalCode.Length > 2 ? a.PostalCode[..2] + "***" : "***")}";

    public static string StorageKey() => Guid.NewGuid().ToString("N");

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
