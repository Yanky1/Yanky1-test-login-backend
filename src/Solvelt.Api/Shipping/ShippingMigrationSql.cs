namespace Solvelt.Api.Shipping;

/// <summary>SQL used by migrations (kept here so a test can run exactly what the migration runs). Portable: the same text runs on SQLite and PostgreSQL.</summary>
public static class ShippingMigrationSql
{
    /// <summary>
    /// A unique index over LIVE carrier parcels cannot be created while two live parcels share a number (the pre-index check was not atomic). Keeps the NEWEST live parcel of each number
    /// and stops tracking on the older ones. Their shipments and orders are untouched; they just stop receiving carrier updates. The stop time is the parcel's own last activity
    /// (<c>LastTrackingAt</c>, else <c>CreatedAt</c>), never "now": a stop dated at deploy time would look like a delivery-sized event that happened AFTER the surviving parcel was created.
    /// Used by the migration that creates the number-scoped index (Up) and by the per-carrier migration's Down (which recreates it, and where FedEx and USPS rows may share a string).
    /// </summary>
    public const string StopOlderLiveDuplicates = """
        UPDATE order_shipments SET "TrackingStoppedAt" = COALESCE("LastTrackingAt", "CreatedAt")
        WHERE "Method" = 'carrier' AND "DeliveredAt" IS NULL AND "TrackingStoppedAt" IS NULL
          AND EXISTS (SELECT 1 FROM order_shipments n
                      WHERE n."TrackingNumber" = order_shipments."TrackingNumber" AND n."Method" = 'carrier'
                        AND n."DeliveredAt" IS NULL AND n."TrackingStoppedAt" IS NULL
                        AND (n."CreatedAt" > order_shipments."CreatedAt" OR (n."CreatedAt" = order_shipments."CreatedAt" AND n."Id" > order_shipments."Id")))
        """;
}
