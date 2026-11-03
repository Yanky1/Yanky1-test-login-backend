using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_reads",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadBuyerCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_reads", x => new { x.ConversationId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    MarketplaceConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BuyerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    NoReplyNeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Flagged = table.Column<bool>(type: "boolean", nullable: false),
                    FlagReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastBuyerMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BuyerMessageCount = table.Column<int>(type: "integer", nullable: false),
                    AwaitingReplySince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OverdueNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PiiRedactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversations_brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conversations_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "inbound_nonces",
                columns: table => new
                {
                    Nonce = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbound_nonces", x => x.Nonce);
                });

            migrationBuilder.CreateTable(
                name: "message_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewOrder = table.Column<bool>(type: "boolean", nullable: true),
                    CancelRequest = table.Column<bool>(type: "boolean", nullable: true),
                    ReturnRequest = table.Column<bool>(type: "boolean", nullable: true),
                    BuyerMessage = table.Column<bool>(type: "boolean", nullable: true),
                    OverdueReply = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => new { x.UserId, x.SellerId });
                });

            migrationBuilder.CreateTable(
                name: "order_counters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Next = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_counters", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: true),
                    AggregateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "seller_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MarketplaceOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlacedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    HeldFrom = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    BuyerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BuyerRelayEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    BuyerPhone = table.Column<string>(type: "text", nullable: true),
                    ShipName = table.Column<string>(type: "text", nullable: false),
                    ShipLine1 = table.Column<string>(type: "text", nullable: false),
                    ShipLine2 = table.Column<string>(type: "text", nullable: true),
                    ShipCity = table.Column<string>(type: "text", nullable: false),
                    ShipRegion = table.Column<string>(type: "text", nullable: true),
                    ShipPostalCode = table.Column<string>(type: "text", nullable: false),
                    ShipCountry = table.Column<string>(type: "text", nullable: false),
                    ShippingMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShippingSpeed = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    GiftMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PiiRedactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShipByAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliverByAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ItemsCents = table.Column<long>(type: "bigint", nullable: false),
                    DiscountCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    FeeCents = table.Column<long>(type: "bigint", nullable: false),
                    NetCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedCents = table.Column<long>(type: "bigint", nullable: false),
                    Oversold = table.Column<bool>(type: "boolean", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstShippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    ShippedLate = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_seller_orders_brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_orders_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MarketplaceMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delivery = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ContactWarning = table.Column<bool>(type: "boolean", nullable: false),
                    Reported = table.Column<bool>(type: "boolean", nullable: false),
                    ReportReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_cancellations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceRequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Restock = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_cancellations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_cancellations_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Actor = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_events_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceLineId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OptionsText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ImageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UnitPriceCents = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    DiscountCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippedQty = table.Column<int>(type: "integer", nullable: false),
                    CancelledQty = table.Column<int>(type: "integer", nullable: false),
                    RefundedQty = table.Column<int>(type: "integer", nullable: false),
                    ReturnedQty = table.Column<int>(type: "integer", nullable: false),
                    ReservedQty = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_lines_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_notes_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_refunds_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_returns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceReturnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BuyerComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Restock = table.Column<bool>(type: "boolean", nullable: false),
                    RefundCents = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_returns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_returns_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Carrier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Service = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ShippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_shipments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_shipments_seller_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "seller_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_message_attachments_order_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "order_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_refund_lines",
                columns: table => new
                {
                    RefundId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_refund_lines", x => new { x.RefundId, x.OrderLineId });
                    table.ForeignKey(
                        name: "FK_order_refund_lines_order_refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "order_refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_return_lines",
                columns: table => new
                {
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_return_lines", x => new { x.ReturnId, x.OrderLineId });
                    table.ForeignKey(
                        name: "FK_order_return_lines_order_returns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "order_returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_shipment_lines",
                columns: table => new
                {
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_shipment_lines", x => new { x.ShipmentId, x.OrderLineId });
                    table.ForeignKey(
                        name: "FK_order_shipment_lines_order_shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "order_shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_notes_ConversationId",
                table: "conversation_notes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_BrandId",
                table: "conversations",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_OrderId",
                table: "conversations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_ProductId",
                table: "conversations",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_SellerId_LastMessageAt",
                table: "conversations",
                columns: new[] { "SellerId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_SellerId_MarketplaceConversationId",
                table: "conversations",
                columns: new[] { "SellerId", "MarketplaceConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbound_nonces_ExpiresAt",
                table: "inbound_nonces",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_ConversationId",
                table: "message_attachments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_MessageId",
                table: "message_attachments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_message_templates_SellerId",
                table: "message_templates",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_order_cancellations_OrderId",
                table: "order_cancellations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_cancellations_OrderId_MarketplaceRequestId",
                table: "order_cancellations",
                columns: new[] { "OrderId", "MarketplaceRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_events_OrderId_At",
                table: "order_events",
                columns: new[] { "OrderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_OrderId",
                table: "order_lines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_Sku",
                table: "order_lines",
                column: "Sku");

            migrationBuilder.CreateIndex(
                name: "IX_order_messages_ConversationId_CreatedAt",
                table: "order_messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_order_messages_SellerId_MarketplaceMessageId",
                table: "order_messages",
                columns: new[] { "SellerId", "MarketplaceMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_notes_OrderId",
                table: "order_notes",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_OrderId",
                table: "order_refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_OrderId",
                table: "order_returns",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_OrderId_MarketplaceReturnId",
                table: "order_returns",
                columns: new[] { "OrderId", "MarketplaceReturnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_OrderId",
                table: "order_shipments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_TrackingNumber",
                table: "order_shipments",
                column: "TrackingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_NextAttemptAt",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_BrandId",
                table: "seller_orders",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_MarketplaceOrderId",
                table: "seller_orders",
                column: "MarketplaceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_MarketplaceOrderId_SellerId_BrandId",
                table: "seller_orders",
                columns: new[] { "MarketplaceOrderId", "SellerId", "BrandId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_Number",
                table: "seller_orders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_SellerId_BrandId_PlacedAt",
                table: "seller_orders",
                columns: new[] { "SellerId", "BrandId", "PlacedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_orders_SellerId_Status",
                table: "seller_orders",
                columns: new[] { "SellerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_notes");

            migrationBuilder.DropTable(
                name: "conversation_reads");

            migrationBuilder.DropTable(
                name: "inbound_nonces");

            migrationBuilder.DropTable(
                name: "message_attachments");

            migrationBuilder.DropTable(
                name: "message_templates");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "order_cancellations");

            migrationBuilder.DropTable(
                name: "order_counters");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "order_notes");

            migrationBuilder.DropTable(
                name: "order_refund_lines");

            migrationBuilder.DropTable(
                name: "order_return_lines");

            migrationBuilder.DropTable(
                name: "order_shipment_lines");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "order_messages");

            migrationBuilder.DropTable(
                name: "order_refunds");

            migrationBuilder.DropTable(
                name: "order_returns");

            migrationBuilder.DropTable(
                name: "order_shipments");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "seller_orders");
        }
    }
}
