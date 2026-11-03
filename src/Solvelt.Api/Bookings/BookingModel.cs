using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Bookings;

public static class BookingModel
{
    public static void ConfigureBookings(this ModelBuilder b)
    {
        b.Entity<Product>(e => { e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ListingType.Product); e.HasIndex(x => new { x.SellerId, x.Type }); });
        b.Entity<ServiceDetails>(e =>
        {
            e.ToTable("service_details");
            e.HasKey(x => x.ProductId);
            e.Property(x => x.PricingModel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BookingMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AssignmentRule).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OnlineInstructions).HasMaxLength(2000);
            e.Property(x => x.SellerAddress).HasMaxLength(500);
            e.Property(x => x.Requirements).HasMaxLength(2000);
            e.HasOne<Product>().WithOne(p => p.Service).HasForeignKey<ServiceDetails>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ServiceProviderLink>(e =>
        {
            e.ToTable("service_providers");
            e.HasKey(x => new { x.ProductId, x.MembershipId });
            e.HasIndex(x => x.MembershipId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AccountMembership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProviderProfile>(e =>
        {
            e.ToTable("provider_profiles");
            e.HasKey(x => x.MembershipId);
            e.HasIndex(x => x.SellerId);
            e.HasIndex(x => x.IcsTokenHash).IsUnique();
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.IcsTokenHash).HasMaxLength(64);
            e.Property(x => x.CalendarSeq).IsConcurrencyToken(false);
            e.HasOne<AccountMembership>().WithOne().HasForeignKey<ProviderProfile>(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BookingSettings>(e =>
        {
            e.ToTable("booking_settings");
            e.HasKey(x => x.SellerId);
            e.HasIndex(x => x.IcsTokenHash).IsUnique();
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.IcsTokenHash).HasMaxLength(64);
        });
        b.Entity<WorkingHour>(e =>
        {
            e.ToTable("working_hours");
            e.HasIndex(x => new { x.MembershipId, x.Day });
            e.HasOne<AccountMembership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DateOverride>(e =>
        {
            e.ToTable("date_overrides");
            e.HasIndex(x => new { x.MembershipId, x.Date }).IsUnique();
            e.HasOne<AccountMembership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CalendarBlock>(e =>
        {
            e.ToTable("calendar_blocks");
            e.HasIndex(x => new { x.MembershipId, x.StartUtc });
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<AccountMembership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Booking>(e =>
        {
            e.ToTable("bookings");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.MarketplaceBookingId).IsUnique();
            e.HasIndex(x => new { x.ProviderId, x.BlockStartUtc });
            e.HasIndex(x => new { x.SellerId, x.Status, x.StartUtc });
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.QuoteId).IsUnique();   // one booking per accepted quote (NULLs never collide)
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Location).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.PricingModel).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Number).HasMaxLength(24);
            e.Property(x => x.MarketplaceBookingId).HasMaxLength(64);
            e.Property(x => x.BuyerName).HasMaxLength(200);
            e.Property(x => x.BuyerEmail).HasMaxLength(254);
            e.Property(x => x.BuyerPhone).HasMaxLength(40);
            e.Property(x => x.PostalCode).HasMaxLength(16);
            e.Property(x => x.ServiceTitle).HasMaxLength(200);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Property(x => x.DeclineReason).HasMaxLength(500);
            e.Property(x => x.RescheduleRequestId).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BookingEvent>(e =>
        {
            e.ToTable("booking_events");
            e.HasIndex(x => new { x.BookingId, x.At });
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.Actor).HasMaxLength(12);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BookingMessage>(e =>
        {
            e.ToTable("booking_messages");
            e.HasIndex(x => new { x.BookingId, x.CreatedAt });
            e.HasIndex(x => x.MarketplaceMessageId).IsUnique();
            e.Property(x => x.Direction).HasMaxLength(8);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.Property(x => x.MarketplaceMessageId).HasMaxLength(64);
            e.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BookingCounter>(e => { e.ToTable("booking_counters"); e.HasKey(x => x.Year); });
        b.Entity<QuoteRequest>(e =>
        {
            e.ToTable("quote_requests");
            e.HasIndex(x => x.MarketplaceQuoteId).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Status });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Location).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.MarketplaceQuoteId).HasMaxLength(64);
            e.Property(x => x.BuyerName).HasMaxLength(200);
            e.Property(x => x.BuyerEmail).HasMaxLength(254);
            e.Property(x => x.BuyerPhone).HasMaxLength(40);
            e.Property(x => x.PostalCode).HasMaxLength(16);
            e.Property(x => x.Details).HasMaxLength(4000);
            e.Property(x => x.QuoteNote).HasMaxLength(2000);
            e.Property(x => x.ServiceTitle).HasMaxLength(200);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
