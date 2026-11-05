using Microsoft.EntityFrameworkCore;

namespace Solvelt.Api.Reviews;

public enum ReviewState { Visible, Hidden, Removed }

/// <summary>
/// A buyer review of one of the seller's listings, pushed by the marketplace (C17, docs/REVIEWS.md). The only thing stored about the buyer is the display name the marketplace
/// sent: no buyer id, no photos, no email. <see cref="Version"/> is the claim counter that serializes the writers of one review (and with them its outbox sequence).
/// </summary>
public class Review
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The marketplace's reviewId (idempotency key, at most 50 characters so that the outbox scope <c>review:{reviewId}</c> fits its column).</summary>
    public string ExternalId { get; set; } = "";
    public Guid SellerId { get; set; }
    public Guid ProductId { get; set; }
    public int Rating { get; set; }
    public string? Text { get; set; }
    public string? BuyerDisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public ReviewState State { get; set; } = ReviewState.Visible;
    public string? StateReason { get; set; }
    public DateTime? StateChangedAt { get; set; }
    public int Version { get; set; }
}

/// <summary>The seller's reply: at most one active reply per review, <see cref="Id"/> stays the same across edits. A removed reply keeps its row (no text) so the history is auditable.</summary>
public class ReviewReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewId { get; set; }
    public Guid SellerId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? EditedBy { get; set; }
    public DateTime? RemovedAt { get; set; }
    /// <summary>seller_deleted | review_removed | staff_removed</summary>
    public string? RemovedReason { get; set; }
}

public static class ReviewModelConfig
{
    public static void ConfigureReviews(this ModelBuilder b)
    {
        b.Entity<Review>(e =>
        {
            e.ToTable("reviews");
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.State, x.CreatedAt });
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.ExternalId).HasMaxLength(50);
            e.Property(x => x.Text).HasMaxLength(5000);
            e.Property(x => x.BuyerDisplayName).HasMaxLength(60);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.StateReason).HasMaxLength(300);
        });
        b.Entity<ReviewReply>(e =>
        {
            e.ToTable("review_replies");
            e.HasOne<Review>().WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ReviewId).IsUnique().HasFilter("\"RemovedAt\" IS NULL");   // one active reply per review, whatever the race
            e.HasIndex(x => x.SellerId);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.Property(x => x.RemovedReason).HasMaxLength(20);
        });
    }
}

public class ReviewOptions
{
    public const string Section = "Reviews";
    /// <summary>Replies (create or edit) one seller account may post per hour.</summary>
    public int RepliesPerHour { get; set; } = 30;
}
