namespace Solvelt.Api.Domain;

public enum ListingType { Product = 0, Service = 1 }
public enum PricingModel { Hourly, Fixed, Quote }
public enum BookingMode { Instant, Request }
public enum AssignmentRule { BuyerChoice, LeastBusy, RoundRobin }
public enum LocationKind { Online, Customer, Seller }
public enum BlockKind { TimeOff, Busy }

/// <summary>
/// Lifecycle of a booking (see Bookings/BookingStateMachine.cs and docs/SERVICES.md).
/// Rescheduled behaves like Confirmed (a confirmed booking that has been moved at least once).
/// </summary>
public enum BookingStatus
{
    Hold, Requested, Confirmed, Rescheduled, InProgress, Completed,
    Declined, Expired, CancelledByBuyer, CancelledBySeller, NoShow
}

public enum QuoteStatus { Requested, Quoted, Accepted, Declined, Expired, Withdrawn }

/// <summary>Service-specific data of a listing (Product.Type == Service). One row per service; product code never reads it except through the readiness hook.</summary>
public class ServiceDetails
{
    public Guid ProductId { get; set; }
    public Guid SellerId { get; set; }
    public PricingModel PricingModel { get; set; } = PricingModel.Fixed;
    /// <summary>Hourly: rate per hour. Fixed: price per job. Quote: unused (the quote sets the price). Cents.</summary>
    public long? PriceCents { get; set; }
    public int? MinMinutes { get; set; }
    public int? MaxMinutes { get; set; }
    /// <summary>Hourly billing increment in minutes (15, 30 or 60).</summary>
    public int IncrementMinutes { get; set; } = 60;
    public int DefaultDurationMinutes { get; set; } = 60;
    public int BufferBeforeMinutes { get; set; }
    public int BufferAfterMinutes { get; set; }
    public int MinNoticeHours { get; set; } = 24;
    public int MaxAdvanceDays { get; set; } = 60;
    public int SlotGranularityMinutes { get; set; } = 30;
    // where it happens
    public bool Online { get; set; }
    /// <summary>Meeting link / instructions. Revealed only to the confirmed buyer (outbox event) and to the team.</summary>
    public string? OnlineInstructions { get; set; }
    public bool AtCustomer { get; set; }
    /// <summary>JSON: { postalCodes: [], radius: { baseAddress, lat, lng, distance, unit: "km"|"mi" } | null }.</summary>
    public string ServiceAreaJson { get; set; } = "{}";
    public long TravelFeeCents { get; set; }
    public int TravelBufferMinutes { get; set; }
    public bool AtSeller { get; set; }
    public string? SellerAddress { get; set; }
    // booking mode
    public BookingMode BookingMode { get; set; } = BookingMode.Instant;
    public int ApprovalHours { get; set; } = 24;
    /// <summary>JSON array: { id, label, type: text|choice|yesno, options?, required }.</summary>
    public string IntakeJson { get; set; } = "[]";
    public string? Requirements { get; set; }
    /// <summary>JSON array: { id, name, priceCents, extraMinutes }.</summary>
    public string AddOnsJson { get; set; } = "[]";
    // policies
    public int CancelFreeHours { get; set; } = 24;
    public int CancelFeePercent { get; set; }
    public int MaxReschedules { get; set; } = 2;
    public int NoShowFeePercent { get; set; } = 100;
    public AssignmentRule AssignmentRule { get; set; } = AssignmentRule.BuyerChoice;
    /// <summary>Denormalised: eligible providers that have working hours. Refreshed whenever providers or hours change (keeps the readiness check synchronous).</summary>
    public int ReadyProviders { get; set; }
    public int RoundRobinCursor { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ServiceProviderLink
{
    public Guid ProductId { get; set; }
    public Guid MembershipId { get; set; }
}

/// <summary>Calendar settings of a team member who provides services.</summary>
public class ProviderProfile
{
    public Guid MembershipId { get; set; }
    public Guid SellerId { get; set; }
    public bool IsProvider { get; set; }
    public string? TimeZone { get; set; }
    public bool ReminderDayBefore { get; set; } = true;
    public bool ReminderHourBefore { get; set; } = true;
    /// <summary>SHA-256 of the private ICS feed token (the token itself is shown once).</summary>
    public string? IcsTokenHash { get; set; }
    public DateTime? IcsTokenCreatedAt { get; set; }
    /// <summary>Per-provider calendar lock: every booking write first bumps this with an atomic UPDATE, serialising writers of one calendar.</summary>
    public int CalendarSeq { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class BookingSettings
{
    public Guid SellerId { get; set; }
    public string? TimeZone { get; set; }
    public string? IcsTokenHash { get; set; }
    public DateTime? IcsTokenCreatedAt { get; set; }
}

public class WorkingHour
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MembershipId { get; set; }
    public int Day { get; set; }          // 0 = Sunday ... 6 = Saturday (System.DayOfWeek)
    public int StartMinute { get; set; }  // minutes after local midnight
    public int EndMinute { get; set; }    // exclusive, up to 1440
}

/// <summary>Replaces the weekly hours of one local date. Empty interval list = closed that day.</summary>
public class DateOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MembershipId { get; set; }
    public DateOnly Date { get; set; }
    public string IntervalsJson { get; set; } = "[]";
}

/// <summary>Time off / holiday (TimeOff) or a manual "busy" block.</summary>
public class CalendarBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid MembershipId { get; set; }
    public BlockKind Kind { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public bool AllDay { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProviderId { get; set; }          // membership id
    /// <summary>BK-2026-000123 (per-year counter, unique).</summary>
    public string Number { get; set; } = "";
    public string MarketplaceBookingId { get; set; } = "";
    public BookingStatus Status { get; set; }
    public LocationKind Location { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>Calendar footprint including buffers and travel; this is what overlap checks use.</summary>
    public DateTime BlockStartUtc { get; set; }
    public DateTime BlockEndUtc { get; set; }
    public int DurationMinutes { get; set; }
    public DateTime? HoldExpiresAt { get; set; }
    public DateTime? ApprovalDeadline { get; set; }
    // proposals
    public DateTime? ProposedStartUtc { get; set; }
    public DateTime? RescheduleRequestedStartUtc { get; set; }
    public string? RescheduleRequestId { get; set; }
    public int RescheduleCount { get; set; }
    // customer (PII; visible only to the assigned provider and Owners/Admins, from confirmation until RedactAfterDays after the end)
    public string BuyerName { get; set; } = "";
    public string? BuyerEmail { get; set; }
    public string? BuyerPhone { get; set; }
    public string? AddressJson { get; set; }
    public string? PostalCode { get; set; }
    public string IntakeAnswersJson { get; set; } = "[]";
    public string AddOnsJson { get; set; } = "[]";
    public string ServiceTitle { get; set; } = "";
    /// <summary>Policy snapshot taken at booking time (cancellation, reschedule limits, no-show fee, increment) so later edits of the service never change an existing booking.</summary>
    public string PolicyJson { get; set; } = "{}";
    public PricingModel PricingModel { get; set; }
    public long RateCents { get; set; }
    // money (cents). The marketplace moves the money; these are what the seller side recorded.
    public long PriceCents { get; set; }
    public long AddOnsCents { get; set; }
    public long TravelFeeCents { get; set; }
    public long TotalCents { get; set; }
    public long CancellationFeeCents { get; set; }
    public long AdditionalChargeCents { get; set; }
    public int? ActualMinutes { get; set; }
    public string? CancelReason { get; set; }
    public string? DeclineReason { get; set; }
    public Guid? QuoteId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>When the booking first became Confirmed (customer data is only visible from then on).</summary>
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ReminderDaySentAt { get; set; }
    public DateTime? ReminderHourSentAt { get; set; }
    public DateTime? PiiRedactedAt { get; set; }
    /// <summary>Optimistic concurrency token (application-managed).</summary>
    public int Version { get; set; } = 1;
}

public class BookingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public DateTime At { get; set; }
    public string Type { get; set; } = "";
    /// <summary>buyer | seller | system</summary>
    public string Actor { get; set; } = "system";
    public string? Text { get; set; }
}

public class BookingCounter
{
    public int Year { get; set; }
    public long Next { get; set; }
}

public class QuoteRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid ProductId { get; set; }
    public string MarketplaceQuoteId { get; set; } = "";
    public QuoteStatus Status { get; set; } = QuoteStatus.Requested;
    public string BuyerName { get; set; } = "";
    public string? BuyerEmail { get; set; }
    public string? BuyerPhone { get; set; }
    public string Details { get; set; } = "";
    public LocationKind Location { get; set; }
    public string? AddressJson { get; set; }
    public string? PostalCode { get; set; }
    public string PreferredTimesJson { get; set; } = "[]";
    public string ServiceTitle { get; set; } = "";
    // the seller's quote
    public long? QuotePriceCents { get; set; }
    public int? QuoteDurationMinutes { get; set; }
    public string SlotsJson { get; set; } = "[]";
    public Guid? QuoteProviderId { get; set; }
    public DateTime? QuoteExpiresAt { get; set; }
    public string? QuoteNote { get; set; }
    public DateTime? QuotedAt { get; set; }
    public Guid? QuotedBy { get; set; }
    public Guid? BookingId { get; set; }
    public string? DeclineReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PiiRedactedAt { get; set; }
    public int Version { get; set; } = 1;
}

/// <summary>A message in the thread of one booking (buyer via the marketplace, or the team). Own table: the order/product conversations of workstream E stay untouched.</summary>
public class BookingMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Guid SellerId { get; set; }
    /// <summary>buyer | seller</summary>
    public string Direction { get; set; } = "buyer";
    public Guid? AuthorUserId { get; set; }
    public string? MarketplaceMessageId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
