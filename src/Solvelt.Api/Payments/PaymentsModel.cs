using Solvelt.Api.Services;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Payments;

public static class PaymentsModelConfig
{
    public static void ConfigurePayments(this ModelBuilder b)
    {
        b.Entity<LedgerJournal>(e =>
        {
            e.ToTable("ledger_journals");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.CreatedAt });
            e.HasIndex(x => x.OrderId); e.HasIndex(x => x.BookingId);
            e.Property(x => x.Type).HasMaxLength(32); e.Property(x => x.IdempotencyKey).HasMaxLength(200); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Memo).HasMaxLength(300); e.Property(x => x.SourceRef).HasMaxLength(120); e.Property(x => x.Reason).HasMaxLength(500);
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.JournalId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasIndex(x => new { x.SellerId, x.Currency, x.Account });
            e.HasIndex(x => x.OrderId); e.HasIndex(x => x.BookingId);
            e.Property(x => x.Account).HasConversion<string>().HasMaxLength(16); e.Property(x => x.Kind).HasMaxLength(24); e.Property(x => x.Currency).HasMaxLength(3);
        });
        b.Entity<PayoutAccount>(e =>
        {
            e.ToTable("payout_accounts"); e.HasKey(x => x.SellerId); e.HasIndex(x => x.StripeAccountId).IsUnique();
            e.Property(x => x.StripeAccountId).HasMaxLength(64);
        });
        b.Entity<PayoutHold>(e => { e.ToTable("payout_holds"); e.HasIndex(x => new { x.SellerId, x.ReleasedAt }); });
        b.Entity<PaymentTransfer>(e =>
        {
            e.ToTable("payment_transfers");
            e.HasIndex(x => x.IdempotencyKey).IsUnique(); e.HasIndex(x => x.StripeTransferId); e.HasIndex(x => new { x.Status, x.NextAttemptAt }); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12); e.Property(x => x.IdempotencyKey).HasMaxLength(120); e.Property(x => x.Currency).HasMaxLength(3);
        });
        b.Entity<PaymentPayout>(e =>
        {
            e.ToTable("payment_payouts"); e.HasIndex(x => x.StripePayoutId).IsUnique(); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Status).HasMaxLength(16); e.Property(x => x.Currency).HasMaxLength(3);
        });
        b.Entity<PaymentDispute>(e =>
        {
            e.ToTable("payment_disputes"); e.HasIndex(x => x.ExternalId).IsUnique(); e.HasIndex(x => x.SellerId);
            e.Property(x => x.Status).HasMaxLength(16); e.Property(x => x.Currency).HasMaxLength(3);
        });
        b.Entity<PaymentReference>(e =>
        {
            e.ToTable("payment_references"); e.HasIndex(x => x.Ref); e.HasIndex(x => x.MarketplaceOrderId); e.HasIndex(x => x.MarketplaceBookingId);
            e.HasIndex(x => new { x.EventId, x.Ref }).IsUnique();
        });
        b.Entity<ProcessedPaymentEvent>(e =>
        {
            e.ToTable("processed_payment_events"); e.HasKey(x => new { x.Source, x.EventId });
            e.Property(x => x.Source).HasMaxLength(16); e.Property(x => x.EventId).HasMaxLength(120);
        });
        b.Entity<ReconciliationFlag>(e => { e.ToTable("reconciliation_flags"); e.HasIndex(x => x.ResolvedAt); });
        b.Entity<PaymentNotice>(e => { e.ToTable("payment_notices"); e.HasKey(x => new { x.SellerId, x.Key }); e.Property(x => x.Key).HasMaxLength(160); });
        b.Entity<PaymentJobLease>(e => { e.ToTable("payment_job_leases"); e.HasKey(x => x.Name); e.Property(x => x.Name).HasMaxLength(64); });
    }
}
