using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Assistant;

public static class AssistantModelConfig
{
    public static void ConfigureAssistant(this ModelBuilder b)
    {
        b.Entity<AssistantConversation>(e =>
        {
            e.ToTable("assistant_conversations");
            e.HasIndex(x => new { x.SellerId, x.UserId, x.UpdatedAt });
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Surface).HasMaxLength(20);
        });
        b.Entity<AssistantMessage>(e =>
        {
            e.ToTable("assistant_messages");
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.Property(x => x.Role).HasMaxLength(12);
            e.Property(x => x.Status).HasMaxLength(16);
        });
        b.Entity<AssistantPendingAction>(e =>
        {
            e.ToTable("assistant_actions");
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.TargetKind).HasMaxLength(24);
            e.Property(x => x.Risk).HasMaxLength(8);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.Stamp).HasMaxLength(200);
        });
        b.Entity<AssistantUsage>(e =>
        {
            e.ToTable("assistant_usage");
            e.HasIndex(x => new { x.SellerId, x.UserId, x.Day }).IsUnique();
            e.Property(x => x.Day).HasMaxLength(10);
        });
    }
}
