using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Teams;

public static class TeamModel
{
    public static void ConfigureTeams(this ModelBuilder b)
    {
        b.Entity<AccountMembership>(e =>
        {
            e.ToTable("account_memberships");
            e.HasIndex(x => new { x.SellerId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Brands).WithOne().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MembershipBrand>(e =>
        {
            e.ToTable("membership_brands");
            e.HasKey(x => new { x.MembershipId, x.BrandId });
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Brand>(e =>
        {
            e.ToTable("brands");
            e.HasIndex(x => new { x.SellerId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Slug }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Slug).HasMaxLength(90);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AccountInvitation>(e =>
        {
            e.ToTable("account_invitations");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Email });
            e.HasIndex(x => x.Email);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
