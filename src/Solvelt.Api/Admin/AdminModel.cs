using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Admin;

/// <summary>Staff roles of the Solvelt back-office. The legacy Identity role "Admin" maps to <see cref="SuperAdmin"/>.</summary>
public enum StaffRole { ReadOnly = 0, Support = 1, Moderator = 2, SuperAdmin = 3 }

/// <summary>A Solvelt staff member (a normal Identity user with a back-office role). Seller memberships are independent of this.</summary>
public class StaffMember
{
    public Guid UserId { get; set; }
    public StaffRole Role { get; set; }
    public bool Active { get; set; } = true;
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>Staff invitation: the token is random, only its SHA-256 is stored, single use, expiring (same pattern as seller team invitations).</summary>
public class StaffInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public StaffRole Role { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid InvitedBy { get; set; }
    public InviteStatus Status { get; set; } = InviteStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}

/// <summary>Internal staff note on a seller application/account or a user. Never shown to sellers.</summary>
public class AdminNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SellerId { get; set; }
    public Guid? UserId { get; set; }
    public string Text { get; set; } = "";
    public Guid AuthorId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Reviewer assignment of a seller application.</summary>
public class ApplicationAssignment
{
    public Guid SellerId { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTime AssignedAt { get; set; }
}

public enum ReportStatus { Open, Dismissed, Actioned }

/// <summary>A reported / flagged item in the moderation queue. Kind: message | booking_message | listing | contact_warning.</summary>
public class ModerationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public Guid TargetId { get; set; }
    public Guid SellerId { get; set; }
    public string Reason { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>buyer | marketplace | system | staff</summary>
    public string ReporterKind { get; set; } = "buyer";
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
}

public class PlatformSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Last run of a background job (reconcilers, redaction, dispatchers) for the integration-health page.</summary>
public class JobRun
{
    public string Name { get; set; } = "";
    public DateTime LastStartedAt { get; set; }
    public DateTime? LastFinishedAt { get; set; }
    public bool LastSucceeded { get; set; }
    public string? LastResult { get; set; }
    public long Runs { get; set; }
    public long Failures { get; set; }
}

public static class AdminModelConfig
{
    public static void ConfigureAdmin(this ModelBuilder b)
    {
        b.Entity<StaffMember>(e => { e.ToTable("staff_members"); e.HasKey(x => x.UserId); e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16); });
        b.Entity<StaffInvitation>(e =>
        {
            e.ToTable("staff_invitations"); e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.Email);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16); e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
        b.Entity<AdminNote>(e => { e.ToTable("admin_notes"); e.HasIndex(x => x.SellerId); e.HasIndex(x => x.UserId); });
        b.Entity<ApplicationAssignment>(e => { e.ToTable("application_assignments"); e.HasKey(x => x.SellerId); });
        b.Entity<ModerationReport>(e =>
        {
            e.ToTable("moderation_reports"); e.HasIndex(x => new { x.Kind, x.TargetId }).IsUnique(); e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
        b.Entity<PlatformSetting>(e => { e.ToTable("platform_settings"); e.HasKey(x => x.Key); e.Property(x => x.Key).HasMaxLength(64); });
        b.Entity<JobRun>(e => { e.ToTable("job_runs"); e.HasKey(x => x.Name); e.Property(x => x.Name).HasMaxLength(64); });
        b.Entity<AuditEvent>(e => { e.HasIndex(x => x.Type); e.HasIndex(x => new { x.TargetType, x.TargetId }); });
    }
}

/// <summary>The central staff permission matrix. Every admin endpoint declares exactly one permission; roles map to permissions here and nowhere else (docs/ADMIN.md mirrors this table).</summary>
public enum StaffPermission
{
    DashboardView, ApplicationsView, ApplicationsDecide, ApplicationsAssign,
    SellersView, SellersSuspend, SellersClose, SellersResendVerification, SellersTransferOwnership,
    UsersView, UsersAct, UsersDisable2fa,
    ModerationView, ModerationAct,
    LookupsView, LookupsReveal,
    IntegrationsView, IntegrationsAct, IntegrationsDiscard,
    AuditView, AuditExport,
    SettingsView, SettingsEdit,
    StaffView, StaffManage,
    PaymentsView, PaymentsHold, PaymentsRetry, PaymentsAdjust
}

public static class StaffPermissions
{
    private static readonly StaffPermission[] ReadOnly =
    [
        StaffPermission.DashboardView, StaffPermission.ApplicationsView, StaffPermission.SellersView, StaffPermission.UsersView,
        StaffPermission.ModerationView, StaffPermission.LookupsView, StaffPermission.IntegrationsView, StaffPermission.AuditView, StaffPermission.SettingsView,
        StaffPermission.PaymentsView
    ];

    public static readonly IReadOnlyDictionary<StaffRole, HashSet<StaffPermission>> Matrix = new Dictionary<StaffRole, HashSet<StaffPermission>>
    {
        [StaffRole.SuperAdmin] = Enum.GetValues<StaffPermission>().ToHashSet(),
        [StaffRole.ReadOnly] = ReadOnly.ToHashSet(),
        // Support: look up and help sellers/users with non-destructive account actions, incl. break-glass PII reveal and retrying (not discarding) dead letters.
        [StaffRole.Support] = ReadOnly.Concat([StaffPermission.UsersAct, StaffPermission.SellersResendVerification,
            StaffPermission.LookupsReveal, StaffPermission.IntegrationsAct, StaffPermission.PaymentsHold, StaffPermission.PaymentsRetry]).ToHashSet(),
        // Moderator: reported content, listings, messages, seller applications (and escalating to suspension).
        [StaffRole.Moderator] = ReadOnly.Concat([StaffPermission.ModerationAct, StaffPermission.ApplicationsDecide,
            StaffPermission.ApplicationsAssign, StaffPermission.SellersSuspend]).ToHashSet()
    };

    public static bool Has(StaffRole r, StaffPermission p) => Matrix.TryGetValue(r, out var s) && s.Contains(p);

    /// <summary>Permissions that additionally demand a recent step-up re-authentication.</summary>
    public static readonly HashSet<StaffPermission> StepUp =
    [
        StaffPermission.SellersClose, StaffPermission.SellersTransferOwnership, StaffPermission.UsersDisable2fa,
        StaffPermission.IntegrationsDiscard, StaffPermission.AuditExport, StaffPermission.SettingsEdit, StaffPermission.StaffManage,
        StaffPermission.PaymentsAdjust
    ];
}
