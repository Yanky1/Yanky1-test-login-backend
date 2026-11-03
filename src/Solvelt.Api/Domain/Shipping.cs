namespace Solvelt.Api.Domain;

/// <summary>A place labels ship from. One default per (account, brand scope); the provider validated it.</summary>
public class ShipFromAddress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    /// <summary>Null: account-wide.</summary>
    public Guid? BrandId { get; set; }
    /// <summary>BrandId, or Guid.Empty for account-wide (lets a unique index cover both).</summary>
    public Guid ScopeKey { get; set; }
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string? Region { get; set; }
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "US";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>Equals ScopeKey while this is the live default, otherwise null: a unique index on (SellerId, DefaultKey) = one default per scope.</summary>
    public Guid? DefaultKey { get; set; }
    /// <summary>valid, unverified, invalid</summary>
    public string Validation { get; set; } = "unverified";
    public string? ValidationMessage { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PackagePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>box, envelope, soft_pack</summary>
    public string Kind { get; set; } = "box";
    public int LengthMm { get; set; }
    public int WidthMm { get; set; }
    public int HeightMm { get; set; }
    /// <summary>Weight of the empty package; item weights are added by the client.</summary>
    public int EmptyWeightGrams { get; set; }
    public bool IsDefault { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Account-wide (BrandId null) or per-brand shipping settings. Effective = brand row, else account row.</summary>
public class SellerShippingSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid ScopeKey { get; set; }
    /// <summary>Feeds the order ship-by date when the product has none.</summary>
    public int? HandlingDays { get; set; }
    /// <summary>PDF_4x6, PNG or ZPLII</summary>
    public string? LabelFormat { get; set; }
    /// <summary>Return labels ship to this address (default: the default ship-from address).</summary>
    public Guid? ReturnAddressId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Zones, rates and speeds the marketplace uses to quote shipping. Zones are stored as JSON and validated on write.</summary>
public class ShippingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid? BrandId { get; set; }
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>Order subtotal at or above which shipping is free (null: never).</summary>
    public long? FreeShippingThresholdCents { get; set; }
    public string ZonesJson { get; set; } = "[]";
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum LabelKind { Outbound, Return }
/// <summary>Purchasing -> Active -> Voiding (refund asked) -> Voided (refund confirmed). RefundFailed: the carrier refused the refund. Failed: never bought.</summary>
public enum LabelStatus { Purchasing, Active, Voiding, Voided, RefundFailed, Failed }

public class ShippingLabel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid OrderId { get; set; }
    public LabelKind Kind { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid? ReturnShipmentId { get; set; }
    public Guid? ReturnId { get; set; }
    public LabelStatus Status { get; set; } = LabelStatus.Purchasing;
    public string Provider { get; set; } = "";
    public string? ProviderTransactionId { get; set; }
    public string? ProviderShipmentId { get; set; }
    public string? ProviderRateId { get; set; }
    public Guid? QuoteId { get; set; }
    /// <summary>Unique client/derived key: a retry or double click never buys twice.</summary>
    public string IdempotencyKey { get; set; } = "";
    /// <summary>"quoteId:rateId" while the purchase is live, null for failed ones: one purchase per quoted rate.</summary>
    public string? QuoteRateKey { get; set; }
    /// <summary>ShipmentId while Active/Voiding, else null: unique = one live label per shipment.</summary>
    public Guid? ActiveShipmentKey { get; set; }
    public string Carrier { get; set; } = "";
    public string? Service { get; set; }
    public string? TrackingNumber { get; set; }
    public long CostCents { get; set; }
    public string Currency { get; set; } = "USD";
    public string LabelFormat { get; set; } = "PDF_4x6";
    public string? LabelKey { get; set; }
    public string? LabelContentType { get; set; }
    public DateTime? EstimatedDelivery { get; set; }
    public string? CustomsJson { get; set; }
    public string? AddressOverrideReason { get; set; }
    public Guid? PurchasedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PurchasedAt { get; set; }
    public DateTime? PrintedAt { get; set; }
    // void / refund
    public string? ProviderRefundId { get; set; }
    /// <summary>none, pending, success, failed</summary>
    public string RefundStatus { get; set; } = "none";
    public DateTime? RefundRequestedAt { get; set; }
    public DateTime? RefundedAt { get; set; }
    public int RefundAttempts { get; set; }
    public DateTime? NextRefundCheckAt { get; set; }
    public string? RefundError { get; set; }
    public string? Error { get; set; }
}

/// <summary>Return parcel (buyer to seller). Same tracking fields as <see cref="Shipment"/>; the engine works on both.</summary>
public class ReturnShipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ReturnId { get; set; }
    public string Carrier { get; set; } = "";
    public string? Service { get; set; }
    public string TrackingNumber { get; set; } = "";
    public Guid? LabelId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? TrackingStatus { get; set; }
    public string? TrackingStatusDetail { get; set; }
    public DateTime? TrackingEta { get; set; }
    public DateTime? TrackingStatusAt { get; set; }
    public DateTime? LastTrackingAt { get; set; }
    public DateTime? TrackingRegisteredAt { get; set; }
    public DateTime? TrackingStoppedAt { get; set; }
    public DateTime? PollLockedUntil { get; set; }
}

/// <summary>One entry of a parcel's tracking history (a carrier shipment, a return parcel, or a local delivery).</summary>
public class TrackingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ShipmentId { get; set; }
    public Guid? ReturnShipmentId { get; set; }
    public string Status { get; set; } = "unknown";
    public string? Detail { get; set; }
    public string? Location { get; set; }
    public DateTime OccurredAt { get; set; }
    /// <summary>webhook, poll, manual, local</summary>
    public string Source { get; set; } = "webhook";
    /// <summary>Hash of (time, status, detail): the same history entry delivered twice is stored once.</summary>
    public string DedupeKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Inbound provider webhook log: idempotency (unique per provider event id) and the staff "webhook failures" view.</summary>
public class ShippingWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";
    public string? EventId { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>applied, duplicate, stale, unknown_shipment, bad_signature, bad_payload, error</summary>
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>Rate quotes handed to the seller; a purchase must reference one that has not expired.</summary>
public class ShippingQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid OrderId { get; set; }
    public Guid CreatedBy { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string RatesJson { get; set; } = "[]";
    public string? ProviderShipmentId { get; set; }
    /// <summary>valid, unverified, invalid (buyer address check at quote time)</summary>
    public string AddressStatus { get; set; } = "valid";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
