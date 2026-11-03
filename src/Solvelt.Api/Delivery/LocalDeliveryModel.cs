using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Delivery;

public static class LocalDeliveryModel
{
    public static void ConfigureLocalDelivery(this ModelBuilder b)
    {
        b.Entity<DeliverySetting>(e =>
        {
            e.ToTable("delivery_settings");
            e.HasIndex(x => new { x.SellerId, x.ScopeKey }).IsUnique();
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Zones).WithOne().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Windows).WithOne().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DeliveryZone>(e =>
        {
            e.ToTable("delivery_zones"); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Name).HasMaxLength(80); e.Property(x => x.Country).HasMaxLength(2);
        });
        b.Entity<DeliveryWindow>(e => { e.ToTable("delivery_windows"); e.HasIndex(x => x.SellerId); });
        b.Entity<DeliveryWindowUsage>(e => { e.ToTable("delivery_window_usage"); e.HasKey(x => new { x.WindowId, x.Date }); });
        b.Entity<LocalDelivery>(e =>
        {
            e.ToTable("local_deliveries");
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Date });
            e.HasIndex(x => x.RouteId);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Code).HasMaxLength(24);
            e.Property(x => x.FailReason).HasMaxLength(40);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<DeliveryRoute>(e =>
        {
            e.ToTable("delivery_routes");
            e.HasIndex(x => new { x.SellerId, x.Date });
            e.HasIndex(x => x.DriverMembershipId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("delivery_attempts"); e.HasIndex(x => x.DeliveryId);
            e.Property(x => x.Outcome).HasMaxLength(16); e.Property(x => x.Reason).HasMaxLength(40);
            e.Property(x => x.Note).HasMaxLength(500); e.Property(x => x.RecipientName).HasMaxLength(120); e.Property(x => x.Placement).HasMaxLength(20);
        });
        b.Entity<DeliveryProof>(e =>
        {
            e.ToTable("delivery_proofs");
            e.HasIndex(x => x.DeliveryId);
            e.HasIndex(x => new { x.DeliveryId, x.ClientId }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(16); e.Property(x => x.StorageKey).HasMaxLength(64); e.Property(x => x.ContentType).HasMaxLength(64);
        });
        b.Entity<DeliveryClientEvent>(e =>
        {
            e.ToTable("delivery_client_events"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.DeliveryId); e.Property(x => x.Type).HasMaxLength(24); e.Property(x => x.Result).HasMaxLength(20);
        });
        b.Entity<DriverLocation>(e => { e.ToTable("driver_locations"); e.HasIndex(x => x.ExpiresAt); e.HasIndex(x => new { x.RouteId, x.At }); });
        b.Entity<DriverConsent>(e => { e.ToTable("driver_consents"); e.HasKey(x => x.MembershipId); e.Property(x => x.MembershipId).ValueGeneratedNever(); });
        b.Entity<TrackingEvent>(e =>
        {
            e.ToTable("tracking_events");
            e.HasIndex(x => new { x.OrderId, x.OccurredAt });
            e.HasIndex(x => new { x.OrderId, x.SourceEventId }).IsUnique();
            e.Property(x => x.Method).HasMaxLength(12); e.Property(x => x.Status).HasMaxLength(24); e.Property(x => x.Detail).HasMaxLength(300);
            e.Property(x => x.SourceEventId).HasMaxLength(100);
        });
    }
}
