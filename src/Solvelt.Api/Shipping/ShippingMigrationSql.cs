namespace Solvelt.Api.Shipping;

/// <summary>SQL used by migrations (kept here so a test can run exactly what the migration runs).</summary>
public static class ShippingMigrationSql
{
    /// <summary>
    /// Before the per-carrier unique index of LIVE carrier parcels can be created, existing duplicates must go (the old pre-check was not atomic): all but the NEWEST live parcel of a
    /// (carrier, number) get their tracking stopped now. Their shipments and orders are untouched; they just stop receiving carrier updates.
    /// </summary>
    public static string StopOlderLiveDuplicates(bool postgres) => $"""
        UPDATE order_shipments SET "TrackingStoppedAt" = {(postgres ? "now()" : "datetime('now')")}
        WHERE "Method" = 'carrier' AND "DeliveredAt" IS NULL AND "TrackingStoppedAt" IS NULL
          AND EXISTS (SELECT 1 FROM order_shipments n
                      WHERE n."Carrier" = order_shipments."Carrier" AND n."TrackingNumber" = order_shipments."TrackingNumber" AND n."Method" = 'carrier'
                        AND n."DeliveredAt" IS NULL AND n."TrackingStoppedAt" IS NULL
                        AND (n."CreatedAt" > order_shipments."CreatedAt" OR (n."CreatedAt" = order_shipments."CreatedAt" AND n."Id" > order_shipments."Id")))
        """;
}
