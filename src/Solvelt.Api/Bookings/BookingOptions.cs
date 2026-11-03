namespace Solvelt.Api.Bookings;

public class BookingOptions
{
    public const string Section = "Bookings";
    /// <summary>How long a slot held during checkout stays reserved.</summary>
    public int HoldMinutes { get; set; } = 10;
    /// <summary>Customer address/phone/name are wiped this many days after the booking ended (completed, cancelled, declined, no-show).</summary>
    public int RedactAfterDays { get; set; } = 30;
    /// <summary>Account time zone used when neither the provider nor the account chose one.</summary>
    public string DefaultTimeZone { get; set; } = "UTC";
    public int MaxAvailabilityDays { get; set; } = 62;
    /// <summary>Include the buyer's display name in calendar feeds (default: no, minimal data).</summary>
    public bool IcsIncludeBuyerName { get; set; }
    public bool RunBackgroundJobs { get; set; } = true;
}
