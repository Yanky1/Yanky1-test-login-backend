using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Admin;
using Solvelt.Api.Demo;
using Solvelt.Api.Domain;
using Solvelt.Api.Delivery;
using Solvelt.Api.Assistant;
using Solvelt.Api.Bookings;
using Solvelt.Api.Orders;
using Solvelt.Api.Payments;
using Solvelt.Api.Products;
using Solvelt.Api.Shipping;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider? clock = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<SellerAccount> Sellers => Set<SellerAccount>();
    public DbSet<SellerFieldConfig> SellerFieldConfigs => Set<SellerFieldConfig>();
    public DbSet<SellerFile> SellerFiles => Set<SellerFile>();
    public DbSet<OneTimeCode> OneTimeCodes => Set<OneTimeCode>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();
    public DbSet<ExternalExchange> ExternalExchanges => Set<ExternalExchange>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    // phone/SMS channel (see Domain/PhoneEntities.cs)
    public DbSet<PhoneCredential> PhoneCredentials => Set<PhoneCredential>();
    public DbSet<PhoneState> PhoneStates => Set<PhoneState>();
    public DbSet<PhoneOptOut> PhoneOptOuts => Set<PhoneOptOut>();
    public DbSet<PhoneLink> PhoneLinks => Set<PhoneLink>();
    public DbSet<PhoneSeen> PhoneSeen => Set<PhoneSeen>();
    public DbSet<AccountMembership> Memberships => Set<AccountMembership>();
    public DbSet<MembershipBrand> MembershipBrands => Set<MembershipBrand>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<AccountInvitation> Invitations => Set<AccountInvitation>();
    // products (see Products/)
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryAttribute> CategoryAttributes => Set<CategoryAttribute>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductOption> ProductOptions => Set<ProductOption>();
    public DbSet<ProductVariant> Variants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();
    public DbSet<ProductFaq> ProductFaqs => Set<ProductFaq>();
    public DbSet<ProductQuestion> Questions => Set<ProductQuestion>();
    public DbSet<SkuClaim> SkuClaims => Set<SkuClaim>();
    public DbSet<CatalogCounter> CatalogCounters => Set<CatalogCounter>();
    public DbSet<CatalogRefreshRequest> CatalogRefreshRequests => Set<CatalogRefreshRequest>();
    public DbSet<CatalogTombstone> CatalogTombstones => Set<CatalogTombstone>();
    public DbSet<ContentReport> ContentReports => Set<ContentReport>();
    // orders + messaging (see Orders/)
    public DbSet<SellerOrder> Orders => Set<SellerOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<DeliverySetting> DeliverySettings => Set<DeliverySetting>();
    public DbSet<DeliveryZone> DeliveryZones => Set<DeliveryZone>();
    public DbSet<DeliveryWindow> DeliveryWindows => Set<DeliveryWindow>();
    public DbSet<DeliveryWindowUsage> DeliveryWindowUsage => Set<DeliveryWindowUsage>();
    public DbSet<LocalDelivery> LocalDeliveries => Set<LocalDelivery>();
    public DbSet<DeliveryRoute> DeliveryRoutes => Set<DeliveryRoute>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<DeliveryProof> DeliveryProofs => Set<DeliveryProof>();
    public DbSet<DeliveryClientEvent> DeliveryClientEvents => Set<DeliveryClientEvent>();
    public DbSet<DriverLocation> DriverLocations => Set<DriverLocation>();
    public DbSet<DriverConsent> DriverConsents => Set<DriverConsent>();
    public DbSet<TrackingEvent> TrackingEvents => Set<TrackingEvent>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<OrderCancellation> Cancellations => Set<OrderCancellation>();
    public DbSet<ReturnRequest> Returns => Set<ReturnRequest>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<OrderRefund> Refunds => Set<OrderRefund>();
    public DbSet<RefundLine> RefundLines => Set<RefundLine>();
    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<OrderCounter> OrderCounters => Set<OrderCounter>();
    public DbSet<InboundNonce> InboundNonces => Set<InboundNonce>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<OrderMessage> Messages => Set<OrderMessage>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<ConversationRead> ConversationReads => Set<ConversationRead>();
    public DbSet<ConversationNote> ConversationNotes => Set<ConversationNote>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    // services, calendars, bookings (see Bookings/)
    public DbSet<ServiceDetails> ServiceDetails => Set<ServiceDetails>();
    public DbSet<ServiceProviderLink> ServiceProviders => Set<ServiceProviderLink>();
    public DbSet<ProviderProfile> ProviderProfiles => Set<ProviderProfile>();
    public DbSet<BookingSettings> BookingSettings => Set<BookingSettings>();
    public DbSet<WorkingHour> WorkingHours => Set<WorkingHour>();
    public DbSet<DateOverride> DateOverrides => Set<DateOverride>();
    public DbSet<CalendarBlock> CalendarBlocks => Set<CalendarBlock>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingEvent> BookingEvents => Set<BookingEvent>();
    public DbSet<BookingCounter> BookingCounters => Set<BookingCounter>();
    public DbSet<QuoteRequest> QuoteRequests => Set<QuoteRequest>();
    public DbSet<BookingMessage> BookingMessages => Set<BookingMessage>();
    // AI seller assistant (see Assistant/)
    public DbSet<AssistantConversation> AssistantConversations => Set<AssistantConversation>();
    public DbSet<AssistantMessage> AssistantMessages => Set<AssistantMessage>();
    public DbSet<AssistantPendingAction> AssistantActions => Set<AssistantPendingAction>();
    public DbSet<AssistantUsage> AssistantUsages => Set<AssistantUsage>();

    // staff back-office (see Admin/)
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();
    public DbSet<AdminNote> AdminNotes => Set<AdminNote>();
    public DbSet<ApplicationAssignment> ApplicationAssignments => Set<ApplicationAssignment>();
    public DbSet<ModerationReport> ModerationReports => Set<ModerationReport>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    // seller payments & payouts (see Payments/)
    public DbSet<LedgerJournal> LedgerJournals => Set<LedgerJournal>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PayoutAccount> PayoutAccounts => Set<PayoutAccount>();
    public DbSet<PayoutHold> PayoutHolds => Set<PayoutHold>();
    public DbSet<PaymentTransfer> PaymentTransfers => Set<PaymentTransfer>();
    public DbSet<PaymentPayout> PaymentPayouts => Set<PaymentPayout>();
    public DbSet<PaymentDispute> PaymentDisputes => Set<PaymentDispute>();
    public DbSet<UnallocatedDispute> UnallocatedDisputes => Set<UnallocatedDispute>();
    public DbSet<PaymentReference> PaymentReferences => Set<PaymentReference>();
    public DbSet<ProcessedPaymentEvent> ProcessedPaymentEvents => Set<ProcessedPaymentEvent>();
    public DbSet<ReconciliationFlag> ReconciliationFlags => Set<ReconciliationFlag>();
    public DbSet<PaymentNotice> PaymentNotices => Set<PaymentNotice>();
    public DbSet<PaymentJobLease> PaymentJobLeases => Set<PaymentJobLease>();

    // shipping & tracking (see Shipping/)
    public DbSet<ShipFromAddress> ShipFromAddresses => Set<ShipFromAddress>();
    public DbSet<PackagePreset> PackagePresets => Set<PackagePreset>();
    public DbSet<SellerShippingSettings> SellerShippingSettings => Set<SellerShippingSettings>();
    public DbSet<ShippingProfile> ShippingProfiles => Set<ShippingProfile>();
    public DbSet<ShippingLabel> ShippingLabels => Set<ShippingLabel>();
    public DbSet<ReturnShipment> ReturnShipments => Set<ReturnShipment>();
    public DbSet<ShippingWebhookEvent> ShippingWebhookEvents => Set<ShippingWebhookEvent>();
    public DbSet<ShippingQuote> ShippingQuotes => Set<ShippingQuote>();

    // test-only demo data (see Demo/)
    public DbSet<Solvelt.Api.Demo.DemoBatch> DemoBatches => Set<Solvelt.Api.Demo.DemoBatch>();
    public DbSet<Solvelt.Api.Demo.DemoRecord> DemoRecords => Set<Solvelt.Api.Demo.DemoRecord>();

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>A second context on the SAME connection and transaction: lets set-based code (ExecuteUpdate callers) write rows through EF without flushing the caller's pending changes.</summary>
    internal AppDbContext Sibling(TimeProvider time)
    {
        var s = new AppDbContext(options, time);
        s.Database.SetDbConnection(Database.GetDbConnection());
        if (Database.CurrentTransaction is { } t) s.Database.UseTransaction(t.GetDbTransaction());
        return s;
    }

    /// <summary>The ledger is append-only: an update or delete of a journal or entry through the change tracker is a programming error. Catalog versions + events are added here (see CatalogTracking).</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardLedger();
        if (Catalog.CatalogTracking.Scan(this).IsEmpty) return base.SaveChanges(acceptAllChangesOnSuccess);
        return SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardLedger();
        var touch = Catalog.CatalogTracking.Scan(this);
        if (touch.IsEmpty) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        // the counter lock, the new versions, the events and the change itself must commit together
        var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var undo = new Catalog.CatalogTracking.Undo();
        try
        {
            await Catalog.CatalogTracking.ApplyAsync(this, Clock, touch, undo, cancellationToken);
            var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (own is not null) await own.CommitAsync(cancellationToken);
            return n;
        }
        catch { undo.Revert(); throw; }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    private void GuardLedger()
    {
        foreach (var e in ChangeTracker.Entries())
            if (e.Entity is LedgerJournal or LedgerEntry && e.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("The ledger is append-only: journals and entries are never edited or deleted (post a correcting journal).");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<SellerAccount>(e =>
        {
            e.ToTable("seller_accounts");
            e.HasIndex(x => x.UserId); // no longer unique: a user may own several accounts
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.VendorName).HasMaxLength(200);
        });
        b.Entity<SellerFieldConfig>(e =>
        {
            e.ToTable("seller_field_config");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
        });
        b.Entity<SellerFile>().HasIndex(x => x.SellerId);
        b.Entity<OneTimeCode>().HasIndex(x => new { x.UserId, x.Purpose });
        b.Entity<UserSession>().HasIndex(x => x.UserId);
        b.Entity<RefreshToken>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.FamilyId); });
        b.Entity<LoginThrottle>().HasKey(x => x.KeyHash);
        b.Entity<ExternalExchange>().HasIndex(x => x.CodeHash).IsUnique();
        b.Entity<AuditEvent>().HasIndex(x => x.At);
        b.ConfigurePhone();
        b.ConfigureTeams();
        b.ConfigureProducts();
        b.ConfigureOrders();
        b.ConfigureBookings();
        b.ConfigureAssistant();
        b.ConfigureAdmin();
        b.ConfigurePayments();
        b.ConfigureLocalDelivery();
        b.ConfigureShipping();
        b.ConfigureDemo();
    }

    public static void Seed(AppDbContext db)
    {
        CategorySeed.Run(db);
        ServiceCategories.Ensure(db);
        if (db.SellerFieldConfigs.Any()) return;
        static string Opts(params string[] o) => JsonSerializer.Serialize(o);
        db.SellerFieldConfigs.AddRange(
            new SellerFieldConfig { Key = "sells_food", Label = "Do you sell food?", Type = "yesno", SortOrder = 1 },
            new SellerFieldConfig { Key = "food_types", Label = "What kind of food do you sell?", Type = "select",
                OptionsJson = Opts("Bakery", "Meat", "Dairy", "Produce", "Packaged goods", "Other"),
                ParentKey = "sells_food", ParentValue = "yes", SortOrder = 2 },
            new SellerFieldConfig { Key = "is_kosher", Label = "Is your food kosher?", Type = "yesno",
                ParentKey = "sells_food", ParentValue = "yes", SortOrder = 3 },
            new SellerFieldConfig { Key = "kosher_agency", Label = "Kosher certifying agency", Type = "select",
                OptionsJson = Opts("OU", "OK", "Star-K", "CRC", "Kof-K", "Local rabbinate", "Other"),
                ParentKey = "is_kosher", ParentValue = "yes", SortOrder = 4 },
            new SellerFieldConfig { Key = "kosher_certificate", Label = "Kosher certificate (PDF, PNG or JPG, max 5 MB)", Type = "file",
                ParentKey = "is_kosher", ParentValue = "yes", SortOrder = 5 });
        db.SaveChanges();
    }
}
