using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Orders;

public static class OrderModel
{
    public static void ConfigureOrders(this ModelBuilder b)
    {
        b.Entity<SellerOrder>(e =>
        {
            e.ToTable("seller_orders");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.MarketplaceOrderId, x.SellerId, x.BrandId }).IsUnique(); // idempotent ingest
            e.HasIndex(x => new { x.SellerId, x.Status });
            e.HasIndex(x => new { x.SellerId, x.BrandId, x.PlacedAt });
            e.HasIndex(x => x.MarketplaceOrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.HeldFrom).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.CancelledBy).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Number).HasMaxLength(24);
            e.Property(x => x.MarketplaceOrderId).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.BuyerName).HasMaxLength(120);
            e.Property(x => x.BuyerRelayEmail).HasMaxLength(254);
            e.Property(x => x.ShippingMethod).HasMaxLength(64);
            e.Property(x => x.ShippingSpeed).HasMaxLength(24);
            e.Property(x => x.GiftMessage).HasMaxLength(500);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Shipments).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.Sku);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.OptionsText).HasMaxLength(300);
            e.Property(x => x.MarketplaceLineId).HasMaxLength(64);
            e.Property(x => x.ImageKey).HasMaxLength(64);
            e.Property(x => x.ImageContentType).HasMaxLength(32);
        });
        b.Entity<Shipment>(e =>
        {
            e.ToTable("order_shipments");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.TrackingNumber);
            e.Property(x => x.Carrier).HasMaxLength(40);
            e.Property(x => x.Service).HasMaxLength(60);
            e.Property(x => x.TrackingNumber).HasMaxLength(60);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ShipmentLine>(e => { e.ToTable("order_shipment_lines"); e.HasKey(x => new { x.ShipmentId, x.OrderLineId }); });
        b.Entity<OrderCancellation>(e =>
        {
            e.ToTable("order_cancellations");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => new { x.OrderId, x.MarketplaceRequestId }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RequestedBy).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Reason).HasMaxLength(80);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.Property(x => x.MarketplaceRequestId).HasMaxLength(64);
            e.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReturnRequest>(e =>
        {
            e.ToTable("order_returns");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => new { x.OrderId, x.MarketplaceReturnId }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(80);
            e.Property(x => x.BuyerComment).HasMaxLength(1000);
            e.Property(x => x.DecisionReason).HasMaxLength(1000);
            e.Property(x => x.MarketplaceReturnId).HasMaxLength(64);
            e.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReturnLine>(e => { e.ToTable("order_return_lines"); e.HasKey(x => new { x.ReturnId, x.OrderLineId }); });
        b.Entity<OrderRefund>(e =>
        {
            e.ToTable("order_refunds");
            e.HasIndex(x => x.OrderId);
            e.Property(x => x.Reason).HasMaxLength(80);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RefundLine>(e => { e.ToTable("order_refund_lines"); e.HasKey(x => new { x.RefundId, x.OrderLineId }); });
        b.Entity<OrderNote>(e =>
        {
            e.ToTable("order_notes");
            e.HasIndex(x => x.OrderId);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrderEvent>(e =>
        {
            e.ToTable("order_events");
            e.HasIndex(x => new { x.OrderId, x.At });
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.Actor).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrderCounter>(e => { e.ToTable("order_counters"); e.HasKey(x => x.Year); e.Property(x => x.Year).ValueGeneratedNever(); });
        b.Entity<InboundNonce>(e => { e.ToTable("inbound_nonces"); e.HasKey(x => x.Nonce); e.Property(x => x.Nonce).HasMaxLength(100); e.HasIndex(x => x.ExpiresAt); });
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.AggregateId, x.OrderKey });
            e.HasIndex(x => new { x.SequenceScope, x.OrderKey });
            e.Property(x => x.SequenceScope).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.AggregateId).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(500);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasIndex(x => new { x.SellerId, x.LastMessageAt });
            e.HasIndex(x => new { x.SellerId, x.MarketplaceConversationId }).IsUnique();
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.MarketplaceConversationId).HasMaxLength(64);
            e.Property(x => x.BuyerName).HasMaxLength(120);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.FlagReason).HasMaxLength(200);
            e.HasOne<SellerAccount>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<OrderMessage>(e =>
        {
            e.ToTable("order_messages");
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => new { x.SellerId, x.MarketplaceMessageId }).IsUnique();
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Delivery).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.MarketplaceMessageId).HasMaxLength(64);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.ReportReason).HasMaxLength(200);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MessageAttachment>(e =>
        {
            e.ToTable("message_attachments");
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.StorageKey).HasMaxLength(64);
            e.Property(x => x.ContentType).HasMaxLength(32);
            e.Property(x => x.FileName).HasMaxLength(120);
        });
        b.Entity<ConversationRead>(e => { e.ToTable("conversation_reads"); e.HasKey(x => new { x.ConversationId, x.UserId }); });
        b.Entity<ConversationNote>(e =>
        {
            e.ToTable("conversation_notes");
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.Text).HasMaxLength(2000);
        });
        b.Entity<MessageTemplate>(e =>
        {
            e.ToTable("message_templates");
            e.HasIndex(x => x.SellerId);
            e.Property(x => x.Title).HasMaxLength(80);
            e.Property(x => x.Body).HasMaxLength(2000);
        });
        b.Entity<NotificationPreference>(e => { e.ToTable("notification_preferences"); e.HasKey(x => new { x.UserId, x.SellerId }); });
    }
}
