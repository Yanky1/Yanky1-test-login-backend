namespace Solvelt.Api.Domain;

/// <summary>Order lifecycle. The only legal transitions live in <c>Orders/OrderStateMachine.cs</c> (diagram in docs/ORDERS.md).</summary>
public enum OrderStatus
{
    New, Accepted, PartiallyShipped, Shipped, Delivered, Completed, OnHold, Cancelled,
    ReturnRequested, ReturnApproved, ReturnRejected, ReturnReceived, Refunded
}

public enum CancelRequestStatus { Requested, Approved, Rejected }
public enum ReturnStatus { Requested, Approved, Rejected, Received, Refunded }
public enum Party { Seller, Buyer, System }

/// <summary>
/// One order for one (seller account, brand). A marketplace order with items from several accounts/brands becomes several of these.
/// All money is stored in minor units (cents) as <c>long</c>; there is no floating point anywhere in the order math.
/// </summary>
public class SellerOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    /// <summary>Human number, e.g. SO-2026-000123 (per-year counter).</summary>
    public string Number { get; set; } = "";
    public string MarketplaceOrderId { get; set; } = "";
    public DateTime PlacedAt { get; set; }
    public string Currency { get; set; } = "USD";
    public OrderStatus Status { get; set; } = OrderStatus.New;
    /// <summary>Where an On hold order resumes to.</summary>
    public OrderStatus? HeldFrom { get; set; }

    // buyer (only what fulfilment needs)
    public string BuyerName { get; set; } = "";
    public string? BuyerRelayEmail { get; set; }
    public string? BuyerPhone { get; set; }
    public string ShipName { get; set; } = "";
    public string ShipLine1 { get; set; } = "";
    public string? ShipLine2 { get; set; }
    public string ShipCity { get; set; } = "";
    public string? ShipRegion { get; set; }
    public string ShipPostalCode { get; set; } = "";
    public string ShipCountry { get; set; } = "";
    /// <summary>Optional coordinates of the shipping address (sent by the marketplace); used by local delivery zones and routes.</summary>
    public double? ShipLat { get; set; }
    public double? ShipLng { get; set; }
    public string ShippingMethod { get; set; } = "standard";
    public string ShippingSpeed { get; set; } = "standard";
    public string? GiftMessage { get; set; }
    public DateTime? PiiRedactedAt { get; set; }

    // SLA
    public DateTime ShipByAt { get; set; }
    public DateTime DeliverByAt { get; set; }

    // money (cents)
    public long ItemsCents { get; set; }
    public long DiscountCents { get; set; }
    public long ShippingCents { get; set; }
    /// <summary>Tax on the items (sum of the lines' tax).</summary>
    public long TaxCents { get; set; }
    /// <summary>Tax the buyer paid on this group's shipping (C8). In the total, never in the fee base and never in the net (the marketplace remits it).</summary>
    public long ShippingTaxCents { get; set; }
    /// <summary>What the buyer paid: items - discounts + shipping + item tax + shipping tax.</summary>
    public long TotalCents { get; set; }
    public long FeeCents { get; set; }
    /// <summary>What the seller earns before refunds: items - discounts + shipping - marketplace fee (tax is remitted by the marketplace).</summary>
    public long NetCents { get; set; }
    public long RefundedCents { get; set; }

    // flags
    public bool Oversold { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>Set by a tracking exception (failure / return to sender); cleared when tracking recovers or the seller dismisses it.</summary>
    public bool NeedsAttention { get; set; }
    public string? AttentionReason { get; set; }
    public DateTime? AttentionAt { get; set; }

    // timestamps
    public DateTime? AcceptedAt { get; set; }
    public DateTime? FirstShippedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public Party? CancelledBy { get; set; }
    /// <summary>Set when the order was shipped after its ship-by date (feeds the late-shipment rate).</summary>
    public bool ShippedLate { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
    public List<Shipment> Shipments { get; set; } = [];
}

public class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string? MarketplaceLineId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    // snapshot (never changes when the listing is edited later)
    public string Title { get; set; } = "";
    public string? Sku { get; set; }
    public string? OptionsText { get; set; }
    public string? ImageKey { get; set; }
    public string? ImageContentType { get; set; }
    public long UnitPriceCents { get; set; }
    public int Quantity { get; set; }
    public long DiscountCents { get; set; }
    public long TaxCents { get; set; }
    public int ShippedQty { get; set; }
    public int CancelledQty { get; set; }
    public int RefundedQty { get; set; }
    public int ReturnedQty { get; set; }
    /// <summary>Units currently held in stock for this line (0 when the order was oversold or the hold was released/consumed).</summary>
    public int ReservedQty { get; set; }
}

public class Shipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Carrier { get; set; } = "";
    public string? Service { get; set; }
    public string TrackingNumber { get; set; } = "";
    public DateTime ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>carrier | local (shared with workstream K's shipments).</summary>
    public string Method { get; set; } = "carrier";
    /// <summary>Normalized tracking status (see <see cref="TrackingStatus"/>); null for shipments nobody tracks.</summary>
    public string? TrackingStatus { get; set; }
    /// <summary>Provider/event time of the newest status applied (an older update never overwrites a newer one).</summary>
    public DateTime? TrackingStatusAt { get; set; }
    public DateTime? Eta { get; set; }
    // ---- carrier tracking and labels (workstream K) ----
    public string? TrackingStatusDetail { get; set; }
    /// <summary>Last webhook or poll that touched this shipment (the reconciler polls when this is 24 h old).</summary>
    public DateTime? LastTrackingAt { get; set; }
    public DateTime? TrackingRegisteredAt { get; set; }
    /// <summary>Set when tracking stopped (delivered, 60 days, voided).</summary>
    public DateTime? TrackingStoppedAt { get; set; }
    /// <summary>Per-shipment poll lease: only the instance that atomically set this polls the shipment.</summary>
    public DateTime? PollLockedUntil { get; set; }
    public Guid? LabelId { get; set; }
    public List<ShipmentLine> Lines { get; set; } = [];
}

public class ShipmentLine
{
    public Guid ShipmentId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
}

public class OrderCancellation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string? MarketplaceRequestId { get; set; }
    public Party RequestedBy { get; set; }
    public CancelRequestStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public string? DecisionNote { get; set; }
    public bool Restock { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

public class ReturnRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string? MarketplaceReturnId { get; set; }
    public ReturnStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string? BuyerComment { get; set; }
    public string? DecisionReason { get; set; }
    public bool Restock { get; set; }
    public long RefundCents { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public List<ReturnLine> Lines { get; set; } = [];
}

public class ReturnLine
{
    public Guid ReturnId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
}

public class OrderRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid? ReturnId { get; set; }
    /// <summary>Everything handed back: line amounts (incl. line tax) + ShippingCents + ShippingTaxCents.</summary>
    public long AmountCents { get; set; }
    /// <summary>The shipping refunded, net of tax.</summary>
    public long ShippingCents { get; set; }
    /// <summary>The shipping tax refunded (prorated from the order's shipping tax; the group's last shipping refund takes the remainder).</summary>
    public long ShippingTaxCents { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<RefundLine> Lines { get; set; } = [];
}

public class RefundLine
{
    public Guid RefundId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
    /// <summary>Includes the line's tax share.</summary>
    public long AmountCents { get; set; }
    /// <summary>The tax inside <see cref="AmountCents"/> (same proration as the amount: floor, the last refund of the line takes the remainder).</summary>
    public long TaxCents { get; set; }
}

/// <summary>Internal note, visible to the seller team only.</summary>
public class OrderNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid AuthorId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class OrderEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Type { get; set; } = "";
    public Party Actor { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Text { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Per-year order-number counter (incremented with a single atomic UPDATE).</summary>
public class OrderCounter
{
    public int Year { get; set; }
    public long Next { get; set; }
}

// ---- Marketplace integration -------------------------------------------------------------------------------------------

/// <summary>Replay protection for the signed inbound API: every accepted nonce is remembered until its timestamp window has passed.</summary>
public class InboundNonce
{
    public string Nonce { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public enum OutboxStatus { Pending, Delivered, Dead, Discarded }

/// <summary>Transactional outbox: written in the same SaveChanges as the state change it reports, delivered (signed) to the marketplace webhook.</summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = "";
    public Guid? SellerId { get; set; }
    public string AggregateId { get; set; } = "";
    /// <summary>Per-aggregate ordering key (listing events carry the catalog version): a message is never sent while an older one of the same aggregate is still pending.</summary>
    public long? OrderKey { get; set; }
    /// <summary>
    /// Ordered-sequence scope (one seller order, one booking): events of one scope are delivered strictly in <see cref="OrderKey"/> order (never while an older one is pending, leased or in backoff).
    /// Unlike the listing keys (latest wins) every event of a scope is delivered. A Dead predecessor does not block its successors: they carry <c>predecessorDead: true</c>.
    /// </summary>
    public string? SequenceScope { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Dispatcher lease: a message is sent only by the dispatcher that atomically set this (prevents double delivery with several instances).</summary>
    public DateTime? LockedUntil { get; set; }
}

// ---- Messaging --------------------------------------------------------------------------------------------------------

public enum ConversationKind { Order, Product }
public enum MessageDirection { Buyer, Seller }
public enum DeliveryState { Pending, Delivered, Read, Failed }

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public ConversationKind Kind { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? ProductId { get; set; }
    /// <summary>The marketplace's thread id (idempotency + reply routing); null for threads the seller started.</summary>
    public string? MarketplaceConversationId { get; set; }
    public string BuyerName { get; set; } = "";
    public string Subject { get; set; } = "";
    public Guid? AssignedUserId { get; set; }
    public bool NoReplyNeeded { get; set; }
    public bool Flagged { get; set; }
    public string? FlagReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public DateTime? LastBuyerMessageAt { get; set; }
    /// <summary>Number of buyer messages so far; a member's unread state is BuyerMessageCount &gt; their ReadBuyerCount (no clock comparisons).</summary>
    public int BuyerMessageCount { get; set; }
    /// <summary>When the oldest unanswered buyer message arrived (null: nothing waits for a reply).</summary>
    public DateTime? AwaitingReplySince { get; set; }
    public DateTime? OverdueNotifiedAt { get; set; }
    public DateTime? PiiRedactedAt { get; set; }
    public int MessageCount { get; set; }
}

public class OrderMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid SellerId { get; set; }
    public MessageDirection Direction { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string? MarketplaceMessageId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DeliveryState Delivery { get; set; } = DeliveryState.Pending;
    public bool ContactWarning { get; set; }
    public bool Reported { get; set; }
    public string? ReportReason { get; set; }
    /// <summary>For buyer messages: when the seller team answered (response-time metric).</summary>
    public DateTime? RespondedAt { get; set; }
    public List<MessageAttachment> Attachments { get; set; } = [];
}

public class MessageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public Guid ConversationId { get; set; }
    /// <summary>Null until the message that carries it is sent.</summary>
    public Guid? MessageId { get; set; }
    public Guid UploadedBy { get; set; }
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ConversationRead
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTime LastReadAt { get; set; }
    public int ReadBuyerCount { get; set; }
}

public class ConversationNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid AuthorId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class MessageTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Per-user, per-account email preferences. A null flag means "role default" (Owner/Admin on, others off).</summary>
public class NotificationPreference
{
    public Guid UserId { get; set; }
    public Guid SellerId { get; set; }
    public bool? NewOrder { get; set; }
    public bool? CancelRequest { get; set; }
    public bool? ReturnRequest { get; set; }
    public bool? BuyerMessage { get; set; }
    public bool? OverdueReply { get; set; }
    /// <summary>Payment emails (payout failed, dispute, negative balance, onboarding). Owners always get them; null = Admin default off.</summary>
    public bool? PaymentEmails { get; set; }
    /// <summary>Shipping exception emails. Owners always get them; others only when opted in (null = off).</summary>
    public bool? ShippingEmails { get; set; }
}
