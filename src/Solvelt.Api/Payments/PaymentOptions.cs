using Solvelt.Api.Services;
namespace Solvelt.Api.Payments;

/// <summary>Configuration section "Payments" (docs/PAYMENTS.md). Stripe keys come only from configuration / environment, never from code.</summary>
public class PaymentOptions
{
    public const string Section = "Payments";
    /// <summary>Days after delivery / completion before funds become available.</summary>
    public int ReleaseDays { get; set; } = 7;
    /// <summary>When true (default, per the brief) an order is only released once its return window (Orders:ReturnWindowDays) has also passed.</summary>
    public bool RequireReturnWindowClosed { get; set; } = true;
    /// <summary>Reserve withheld from every release of an account that is younger than <see cref="NewSellerDays"/> (percent 0-100; 0 = off).</summary>
    public int NewSellerReservePercent { get; set; } = 10;
    public int NewSellerDays { get; set; } = 90;
    /// <summary>Days after which a reserve is released to the available balance.</summary>
    public int ReserveReleaseDays { get; set; } = 30;
    /// <summary>A transfer batch below this amount waits for the next day.</summary>
    public long MinTransferCents { get; set; } = 500;
    public int TransferMaxAttempts { get; set; } = 5;
    public int TransferBackoffSeconds { get; set; } = 60;
    /// <summary>Dispute fee debited when the event does not carry one.</summary>
    public long DefaultDisputeFeeCents { get; set; } = 1500;
    /// <summary>Stripe webhook timestamp tolerance (seconds).</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;
    public bool RunBackgroundJobs { get; set; } = true;
    public int JobIntervalSeconds { get; set; } = 300;
    public int LeaseSeconds { get; set; } = 300;
    /// <summary>Per-minute limit of the Stripe webhook endpoint (per IP).</summary>
    public int WebhooksPerMinute { get; set; } = 1200;
    public bool AllowTestKeysInProduction { get; set; }
    public bool AllowLiveKeysOutsideProduction { get; set; }
}

/// <summary>Configuration section "Stripe". Secret key + webhook secret; no key = deterministic fake provider (dev/tests).</summary>
public class StripeOptions
{
    public const string Section = "Stripe";
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Two-letter country for new Express accounts when the seller account has none.</summary>
    public string DefaultCountry { get; set; } = "US";
}

/// <summary>Test seams (singleton, empty in production): lets a test commit another change at an exact point of a job.</summary>
public class PaymentTestHooks
{
    public Func<Guid, Task>? AfterReleaseCandidate { get; set; }
}
