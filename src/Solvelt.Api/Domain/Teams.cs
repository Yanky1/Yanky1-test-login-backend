namespace Solvelt.Api.Domain;

/// <summary>Driver (-1) is below Viewer on purpose: every `Role >= Viewer` check excludes drivers. Drivers only ever reach /api/v1/driver (see DriverGuard).</summary>
public enum AccountRole { Driver = -1, Viewer = 0, Manager = 1, Admin = 2, Owner = 3 }
public enum MembershipStatus { Active, Suspended }
public enum BrandStatus { Active, Archived }
public enum InviteStatus { Pending, Accepted, Declined, Revoked }

/// <summary>A user's membership of one seller account. One user can have many; one account can have many.</summary>
public class AccountMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid UserId { get; set; }
    public AccountRole Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    /// <summary>True: every brand of the account (always true for Owner/Admin). False: only <see cref="Brands"/>.</summary>
    public bool AllBrands { get; set; } = true;
    public Guid? InvitedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public List<MembershipBrand> Brands { get; set; } = [];
}

public class MembershipBrand
{
    public Guid MembershipId { get; set; }
    public Guid BrandId { get; set; }
}

public class Brand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ContactEmail { get; set; }
    public string? Website { get; set; }
    public string? LogoKey { get; set; }
    public string? LogoContentType { get; set; }
    public BrandStatus Status { get; set; } = BrandStatus.Active;
    /// <summary>The auto-created brand; renamed after the vendor name until somebody edits it.</summary>
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AccountInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Email { get; set; } = "";          // normalized
    public AccountRole Role { get; set; }
    public bool AllBrands { get; set; } = true;
    public string BrandIdsJson { get; set; } = "[]";
    public string TokenHash { get; set; } = "";      // SHA-256 of the random token; the token itself is never stored
    public Guid InvitedBy { get; set; }
    public InviteStatus Status { get; set; } = InviteStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSentAt { get; set; }
    public int SendCount { get; set; } = 1;
    public DateTime? ResolvedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}
