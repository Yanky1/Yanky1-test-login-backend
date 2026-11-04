using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Shipping;

public static class ShippingModelConfig
{
    public static void ConfigureShipping(this ModelBuilder b)
    {
        b.Entity<ShipFromAddress>(e =>
        {
            e.ToTable("ship_from_addresses");
            e.HasIndex(x => new { x.SellerId, x.ScopeKey });
            e.HasIndex(x => new { x.SellerId, x.DefaultKey }).IsUnique();   // one default per scope (NULLs are distinct)
            e.Property(x => x.Label).HasMaxLength(80); e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Company).HasMaxLength(120);
            e.Property(x => x.Line1).HasMaxLength(200); e.Property(x => x.Line2).HasMaxLength(200); e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Region).HasMaxLength(100); e.Property(x => x.PostalCode).HasMaxLength(20); e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.Phone).HasMaxLength(40); e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Validation).HasMaxLength(12); e.Property(x => x.ValidationMessage).HasMaxLength(500);
        });
        b.Entity<PackagePreset>(e =>
        {
            e.ToTable("package_presets"); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Name).HasMaxLength(80); e.Property(x => x.Kind).HasMaxLength(12);
        });
        b.Entity<SellerShippingSettings>(e =>
        {
            e.ToTable("shipping_settings");
            e.HasIndex(x => new { x.SellerId, x.ScopeKey }).IsUnique();
            e.Property(x => x.LabelFormat).HasMaxLength(8);
        });
        b.Entity<ShippingProfile>(e =>
        {
            e.ToTable("shipping_profiles"); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Name).HasMaxLength(80); e.Property(x => x.Currency).HasMaxLength(3);
        });
        b.Entity<ShippingLabel>(e =>
        {
            e.ToTable("shipping_labels");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.QuoteRateKey).IsUnique();
            e.HasIndex(x => x.ActiveShipmentKey).IsUnique();               // one live label per shipment
            e.HasIndex(x => x.ProviderTransactionId);
            e.HasIndex(x => new { x.SellerId, x.CreatedAt });
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => new { x.RefundStatus, x.NextRefundCheckAt });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(14);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Provider).HasMaxLength(16); e.Property(x => x.ProviderTransactionId).HasMaxLength(80);
            e.Property(x => x.ProviderShipmentId).HasMaxLength(80); e.Property(x => x.ProviderRateId).HasMaxLength(80);
            e.Property(x => x.ProviderRefundId).HasMaxLength(80);
            e.Property(x => x.IdempotencyKey).HasMaxLength(160); e.Property(x => x.QuoteRateKey).HasMaxLength(140);
            e.Property(x => x.Carrier).HasMaxLength(40); e.Property(x => x.Service).HasMaxLength(80); e.Property(x => x.TrackingNumber).HasMaxLength(60);
            e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.LabelFormat).HasMaxLength(8); e.Property(x => x.LabelKey).HasMaxLength(64);
            e.Property(x => x.LabelContentType).HasMaxLength(32); e.Property(x => x.RefundStatus).HasMaxLength(8);
            e.Property(x => x.AddressOverrideReason).HasMaxLength(300); e.Property(x => x.RefundError).HasMaxLength(300); e.Property(x => x.Error).HasMaxLength(300);
        });
        b.Entity<ReturnShipment>(e =>
        {
            e.ToTable("return_shipments");
            e.HasIndex(x => x.OrderId); e.HasIndex(x => x.ReturnId); e.HasIndex(x => x.TrackingNumber);
            e.Property(x => x.Carrier).HasMaxLength(40); e.Property(x => x.Service).HasMaxLength(80); e.Property(x => x.TrackingNumber).HasMaxLength(60);
        });
        b.Entity<TrackingEvent>(e =>   // the table is shared with local delivery (L): only the carrier-tracking columns are configured here
        {
            e.HasIndex(x => x.ReturnShipmentId); e.HasIndex(x => x.ShipmentId);
            e.Property(x => x.Location).HasMaxLength(200); e.Property(x => x.Source).HasMaxLength(10);
        });
        b.Entity<ShippingWebhookEvent>(e =>
        {
            e.ToTable("shipping_webhook_events");
            e.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
            e.HasIndex(x => x.ReceivedAt);
            e.Property(x => x.Provider).HasMaxLength(16); e.Property(x => x.EventId).HasMaxLength(160); e.Property(x => x.TrackingNumber).HasMaxLength(60);
            e.Property(x => x.Outcome).HasMaxLength(20); e.Property(x => x.Error).HasMaxLength(300);
        });
        b.Entity<ShippingQuote>(e => { e.ToTable("shipping_quotes"); e.HasIndex(x => x.OrderId); e.HasIndex(x => x.ExpiresAt); });
        b.Entity<Shipment>(e =>
        {
            e.Property(x => x.TrackingStatusDetail).HasMaxLength(300);
            e.HasIndex(x => new { x.TrackingStoppedAt, x.LastTrackingAt });
            // two LIVE carrier parcels of one carrier can never share a number (the check in the ship flow is check-then-insert, this makes it race-proof); a delivered or stopped parcel frees the number. Numbers are only unique per carrier, so another carrier's identical string is legitimate
            e.HasIndex(x => new { x.Carrier, x.TrackingNumber }).IsUnique().HasDatabaseName("UX_order_shipments_live_tracking").HasFilter("\"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL");
        });
    }
}
