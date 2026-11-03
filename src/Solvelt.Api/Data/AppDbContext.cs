using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
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
    }

    public static void Seed(AppDbContext db)
    {
        CategorySeed.Run(db);
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
