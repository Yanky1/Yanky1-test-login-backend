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
    /// <summary>Reconciliation only re-checks transfers created within this many days (older ones were checked many times already).</summary>
    public int ReconcileDays { get; set; } = 35;
    /// <summary>Per-minute limit of the Stripe webhook endpoint (per IP).</summary>
    public int WebhooksPerMinute { get; set; } = 1200;
    public bool AllowTestKeysInProduction { get; set; }
    public bool AllowLiveKeysOutsideProduction { get; set; }
    /// <summary>With a live Stripe key the platform AND the Connect webhook secret are required at startup (Stripe signs each endpoint with its own secret). Set to accept a single secret on purpose (Stripe CLI, one shared secret).</summary>
    public bool AllowSingleWebhookSecret { get; set; }
}

/// <summary>Configuration section "Stripe". Secret key + webhook secret; no key = deterministic fake provider (dev/tests).</summary>
public class StripeOptions
{
    public const string Section = "Stripe";
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Signing secret of the Stripe CONNECT webhook endpoint (events of connected accounts). Stripe issues a different secret per endpoint; empty = the single <see cref="WebhookSecret"/> is used for everything.</summary>
    public string ConnectWebhookSecret { get; set; } = "";
    /// <summary>Two-letter country for new Express accounts when the seller account has none.</summary>
    public string DefaultCountry { get; set; } = "US";
}

/// <summary>Test seams (singleton, empty in production): lets a test commit another change at an exact point of a job.</summary>
public class PaymentTestHooks
{
    public Func<Guid, Task>? AfterReleaseCandidate { get; set; }
    /// <summary>Runs inside RetryAsync after the lease is taken and before its transaction opens.</summary>
    public Func<Task>? BeforeRetryTransaction { get; set; }
    /// <summary>Runs in the Stripe webhook just before its changes are saved (simulates a concurrent writer).</summary>
    public Func<Task>? BeforeWebhookSave { get; set; }
    /// <summary>Runs just before a money-path transaction commits (a test makes the commit fail like PostgreSQL does: after rolling the transaction back, with a 40001).</summary>
    public Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction, Task>? BeforeCommit { get; set; }
    /// <summary>Runs inside the event handlers (refund/dispute/capture) before they do their work.</summary>
    public Func<string, Task>? InHandler { get; set; }
    /// <summary>Runs after a lease renewal succeeded (a test moves the clock or lets another runner try).</summary>
    public Func<Task>? AfterLeaseRenew { get; set; }

    public async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct = default)
    {
        if (BeforeCommit is { } h) await h(tx);
        await tx.CommitAsync(ct);
    }
    /// <summary>Isolation levels of the explicit transactions the payment code opened (tests assert the money paths are Serializable).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<System.Data.IsolationLevel> Transactions { get; } = new();

    /// <summary>The ONE place the money paths open their transaction: always Serializable (and recorded, so a test notices a path that stops using it).</summary>
    public async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginSerializableAsync(Solvelt.Api.Data.AppDbContext db, CancellationToken ct = default)
    {
        const System.Data.IsolationLevel level = System.Data.IsolationLevel.Serializable;
        Transactions.Enqueue(level);
        return await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.BeginTransactionAsync(db.Database, level, ct);
    }
}
