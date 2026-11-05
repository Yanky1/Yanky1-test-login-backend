namespace Solvelt.Api.Demo;

public class DemoOptions
{
    public const string Section = "Demo";
    /// <summary>Test-only: exposes the "fill with demo data" / "approve this account" endpoints. Default false. The app refuses to start with this on in Production.</summary>
    public bool Enabled { get; set; }
    /// <summary>TEST SITES ONLY, default false. Together with <see cref="Enabled"/> (both must be true) it maps the endpoints that let a staff member skip two-factor for one session (docs/ADMIN.md). The app refuses to start with this on in Production.</summary>
    public bool AllowStaffMfaSkip { get; set; }
    /// <summary>The staff two-factor skip exists only when BOTH flags are on; checked again on every request of a skipped session.</summary>
    public bool StaffMfaSkipOn => Enabled && AllowStaffMfaSkip;
    /// <summary>Fill / remove / approve calls per account per hour.</summary>
    public int PerHour { get; set; } = 5;
}
