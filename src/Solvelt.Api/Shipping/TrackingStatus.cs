namespace Solvelt.Api.Shipping;

/// <summary>
/// The normalized tracking vocabulary shared by carrier shipments, return parcels and local deliveries (workstream L).
/// Provider statuses are mapped here once; nothing else in the app sees provider-specific strings.
/// </summary>
public static class TrackingStatuses
{
    // the vocabulary itself is shared with local delivery: Domain.TrackingStatus (L). This class adds the carrier-side logic (ordering, mapping, labels).
    public const string PreTransit = Domain.TrackingStatus.PreTransit, InTransit = Domain.TrackingStatus.InTransit, OutForDelivery = Domain.TrackingStatus.OutForDelivery,
        Delivered = Domain.TrackingStatus.Delivered, AvailablePickup = Domain.TrackingStatus.AvailableForPickup, ReturnToSender = Domain.TrackingStatus.ReturnToSender,
        Failure = Domain.TrackingStatus.Failure, Unknown = Domain.TrackingStatus.Unknown;

    public static readonly string[] All = Domain.TrackingStatus.All;

    /// <summary>Order of progress; used to break ties when two updates carry the same provider timestamp (the later stage wins).</summary>
    public static int Rank(string? s) => s switch
    {
        PreTransit => 1, InTransit => 2, OutForDelivery => 3, AvailablePickup => 4, Failure => 4, ReturnToSender => 5, Delivered => 6, _ => 0
    };

    public static bool IsException(string? s) => s is Failure or ReturnToSender;
    public static bool IsTerminal(string? s) => s is Delivered;
    public static string Normalize(string? s) => s != null && All.Contains(s) ? s : Unknown;

    /// <summary>Shippo: PRE_TRANSIT, TRANSIT, DELIVERED, RETURNED, FAILURE, UNKNOWN, plus a substatus code (out_for_delivery, available_for_pickup, ...).</summary>
    public static string FromShippo(string? status, string? substatus)
    {
        var sub = (substatus ?? "").ToLowerInvariant();
        switch ((status ?? "").ToUpperInvariant())
        {
            case "PRE_TRANSIT": return PreTransit;
            case "TRANSIT":
                if (sub.Contains("out_for_delivery")) return OutForDelivery;
                if (sub.Contains("pickup")) return AvailablePickup;
                return InTransit;
            case "DELIVERED": return sub.Contains("pickup") && !sub.Contains("picked") ? AvailablePickup : Delivered;
            case "RETURNED": return ReturnToSender;
            case "FAILURE": return Failure;
            default: return Normalize(status?.ToLowerInvariant());
        }
    }

    public static string Label(string? s) => s switch
    {
        PreTransit => "Label created", InTransit => "In transit", OutForDelivery => "Out for delivery", Delivered => "Delivered",
        AvailablePickup => "Ready for pickup", ReturnToSender => "Returning to sender", Failure => "Delivery problem", _ => "Unknown"
    };
}
