using Microsoft.EntityFrameworkCore;

namespace Solvelt.Api.Demo;

/// <summary>Test-only feature (Demo:Enabled, never in Production): one batch of demo data per seller account. The unique index on SellerId makes parallel fills create at most one batch.</summary>
public class DemoBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>The fill has finished (a crash mid-fill leaves a batch that can still be removed, but POST keeps answering 409 until it is).</summary>
    public bool Completed { get; set; }
}

/// <summary>
/// Marks one record as demo data: the registry (instead of a flag column on every table) lets one migration cover every entity type, and keeps the demo marker out of the
/// hot tables. <see cref="Seq"/> is the creation order inside the batch: removal walks it backwards, so children go before parents.
/// </summary>
public class DemoRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public Guid SellerId { get; set; }
    /// <summary>brand, product, service_product, order, booking, quote, conversation, shipfrom, package, shipping_profile, delivery_setting, payout_account, event_key ...</summary>
    public string Kind { get; set; } = "";
    /// <summary>The id of the record; for keyed rows without a Guid key (idempotency keys, event ids) <see cref="Key"/> is used instead.</summary>
    public Guid EntityId { get; set; }
    public string? Key { get; set; }
    public int Seq { get; set; }
}

public static class DemoModelConfig
{
    public static void ConfigureDemo(this ModelBuilder b)
    {
        b.Entity<DemoBatch>(e =>
        {
            e.ToTable("demo_batches");
            e.HasIndex(x => x.SellerId).IsUnique();
        });
        b.Entity<DemoRecord>(e =>
        {
            e.ToTable("demo_records");
            e.HasIndex(x => new { x.SellerId, x.Kind });
            e.HasIndex(x => x.EntityId);
            e.HasIndex(x => x.BatchId);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Key).HasMaxLength(200);
        });
    }
}
