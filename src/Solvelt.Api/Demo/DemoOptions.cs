namespace Solvelt.Api.Demo;

public class DemoOptions
{
    public const string Section = "Demo";
    /// <summary>Test-only: exposes the "fill with demo data" / "approve this account" endpoints. Default false. The app refuses to start with this on in Production.</summary>
    public bool Enabled { get; set; }
    /// <summary>Fill / remove / approve calls per account per hour.</summary>
    public int PerHour { get; set; } = 5;
}
