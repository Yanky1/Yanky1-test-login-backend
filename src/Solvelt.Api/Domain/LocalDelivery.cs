namespace Solvelt.Api.Domain;

// Local delivery with the seller's own drivers (workstream L, docs/LOCAL-DELIVERY.md).

/// <summary>Normalized tracking statuses shared by every delivery method (carrier shipments from workstream K and local delivery).</summary>
public static class TrackingStatus
{
    public const string PreTransit = "pre_transit", InTransit = "in_transit", OutForDelivery = "out_for_delivery", Delivered = "delivered",
        AvailableForPickup = "available_for_pickup", ReturnToSender = "return_to_sender", Failure = "failure", Unknown = "unknown";
    public static readonly string[] All = [PreTransit, InTransit, OutForDelivery, Delivered, AvailableForPickup, ReturnToSender, Failure, Unknown];
}

public static class DeliveryMethod { public const string Carrier = "carrier", Local = "local"; }

/// <summary>Lifecycle of one local delivery (one per order). Maps to a normalized tracking status, see <c>LocalTracking.StatusOf</c>.</summary>
public enum LocalStatus { Unassigned, Assigned, InTransit, OutForDelivery, Delivered, Failed, Returned, Cancelled }
public enum RouteStatus { Planned, Active, Completed, Cancelled }

/// <summary>Delivery settings of one account, or of one brand (BrandId set) which then overrides the account-wide settings.</summary>
public class DeliverySetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid? BrandId { get; set; }
    /// <summary>BrandId or Guid.Empty: gives the (account, brand) pair a plain unique index that behaves the same on SQLite and PostgreSQL.</summary>
    public Guid ScopeKey { get; set; }
    public bool Enabled { get; set; }
    public string TimeZone { get; set; } = "UTC";
    /// <summary>Local time of day (minutes after midnight) after which same-day delivery is no longer offered; null = no same-day delivery.</summary>
    public int? SameDayCutoffMinutes { get; set; }
    /// <summary>A window must start at least this many minutes from now to be offered.</summary>
    public int MinLeadMinutes { get; set; } = 60;
    public int MaxDaysAhead { get; set; } = 7;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<DeliveryZone> Zones { get; set; } = [];
    public List<DeliveryWindow> Windows { get; set; } = [];
}

public class DeliveryZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SettingId { get; set; }
    public Guid SellerId { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "US";
    // criteria: a zone matches when ANY configured criterion matches
    public double? OriginLat { get; set; }
    public double? OriginLng { get; set; }
    public double? RadiusKm { get; set; }
    /// <summary>JSON array of postcodes; a trailing * is a prefix match ("112*").</summary>
    public string PostcodesJson { get; set; } = "[]";
    /// <summary>Optional JSON array of [lat,lng] pairs (a drawn polygon).</summary>
    public string? PolygonJson { get; set; }
    public long FeeCents { get; set; }
    public long? FreeOverCents { get; set; }
    public long MinOrderCents { get; set; }
    public int Priority { get; set; }
    public bool Active { get; set; } = true;
}

public class DeliveryWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SettingId { get; set; }
    public Guid SellerId { get; set; }
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
    public int Capacity { get; set; }
    /// <summary>Bit 0 = Sunday ... bit 6 = Saturday.</summary>
    public int DaysMask { get; set; } = 0b0111110;
    public bool Active { get; set; } = true;
}

/// <summary>How many deliveries are booked into one window on one local date. Incremented with a conditional single-statement UPDATE (never over capacity).</summary>
public class DeliveryWindowUsage
{
    public Guid WindowId { get; set; }
    public DateOnly Date { get; set; }
    public int Booked { get; set; }
}

public class LocalDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid? ZoneId { get; set; }
    public Guid? WindowId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>The window in UTC (computed once, when booked, with the account time zone).</summary>
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public long FeeCents { get; set; }
    public double? DestLat { get; set; }
    public double? DestLng { get; set; }
    public LocalStatus Status { get; set; } = LocalStatus.Unassigned;
    public Guid? RouteId { get; set; }
    public int StopSeq { get; set; }
    public Guid? DriverMembershipId { get; set; }
    /// <summary>Public-facing code, also the tracking number of the local shipment.</summary>
    public string Code { get; set; } = "";
    public int Attempts { get; set; }
    public string? FailReason { get; set; }
    /// <summary>The client time of the newest applied driver event: an older event can never overwrite newer state.</summary>
    public DateTime? LastEventClientAt { get; set; }
    public DateTime? EtaUtc { get; set; }
    public DateTime? LastEtaEventAt { get; set; }
    public DateTime? LateNotifiedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public double? DeliveredLat { get; set; }
    public double? DeliveredLng { get; set; }
    public bool Converted { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DeliveryRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public DateOnly Date { get; set; }
    public Guid DriverMembershipId { get; set; }
    public RouteStatus Status { get; set; } = RouteStatus.Planned;
    public string? Name { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? StartLat { get; set; }
    public double? StartLng { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryId { get; set; }
    public Guid SellerId { get; set; }
    public int Number { get; set; }
    /// <summary>delivered | failed</summary>
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
    public string? Note { get; set; }
    public string? RecipientName { get; set; }
    /// <summary>left_at_door | handed_to</summary>
    public string? Placement { get; set; }
    public Guid? DriverMembershipId { get; set; }
    public DateTime ClientAt { get; set; }
    public DateTime ServerAt { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
}

/// <summary>A photo or signature. Bytes live in IFileStorage under StorageKey; EXIF/location metadata is stripped before saving.</summary>
public class DeliveryProof
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryId { get; set; }
    public Guid SellerId { get; set; }
    public Guid? AttemptId { get; set; }
    /// <summary>photo | signature</summary>
    public string Kind { get; set; } = "photo";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Client-generated id: an offline replay of the same upload returns the stored proof.</summary>
    public Guid ClientId { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>Idempotency + audit trail of every driver event: the client event id is the primary key.</summary>
public class DeliveryClientEvent
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public DateTime ClientAt { get; set; }
    public DateTime AppliedAt { get; set; }
    /// <summary>applied | ignored_stale | ignored_state | rejected</summary>
    public string Result { get; set; } = "applied";
}

/// <summary>Driver position. Coordinates are rounded to 3 decimals (about 100 m) BEFORE they are stored; deleted after 24 hours.</summary>
public class DriverLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid RouteId { get; set; }
    public Guid MembershipId { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTime At { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>A driver's consent to share their location while a route is active.</summary>
public class DriverConsent
{
    public Guid MembershipId { get; set; }
    public Guid SellerId { get; set; }
    public bool Consented { get; set; }
    public bool Paused { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Tracking history of a shipment/delivery with the shared normalized statuses (shape of workstream K's tracking history).</summary>
public class TrackingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid? DeliveryId { get; set; }
    /// <summary>carrier | local</summary>
    public string Method { get; set; } = DeliveryMethod.Local;
    public string Status { get; set; } = TrackingStatus.Unknown;
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    /// <summary>Provider/event id for idempotency (unique per order when set).</summary>
    public string? SourceEventId { get; set; }
    // ---- carrier tracking (workstream K), additive ----
    /// <summary>Set for a return parcel's history (ShipmentId is then null).</summary>
    public Guid? ReturnShipmentId { get; set; }
    public string? Location { get; set; }
    /// <summary>webhook | poll (carrier tracking), null for local deliveries.</summary>
    public string? Source { get; set; }
}
