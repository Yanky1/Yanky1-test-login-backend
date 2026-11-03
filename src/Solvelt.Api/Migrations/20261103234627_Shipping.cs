using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Shipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AttentionAt",
                table: "seller_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttentionReason",
                table: "seller_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsAttention",
                table: "seller_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultPackageId",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LabelId",
                table: "order_shipments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTrackingAt",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "order_shipments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "carrier");

            migrationBuilder.AddColumn<DateTime>(
                name: "PollLockedUntil",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingEta",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingRegisteredAt",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrackingStatus",
                table: "order_shipments",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingStatusAt",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrackingStatusDetail",
                table: "order_shipments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingStoppedAt",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShippingEmails",
                table: "notification_preferences",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "package_presets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    LengthMm = table.Column<int>(type: "integer", nullable: false),
                    WidthMm = table.Column<int>(type: "integer", nullable: false),
                    HeightMm = table.Column<int>(type: "integer", nullable: false),
                    EmptyWeightGrams = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_presets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "return_shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    Carrier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Service = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    LabelId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrackingStatus = table.Column<string>(type: "text", nullable: true),
                    TrackingStatusDetail = table.Column<string>(type: "text", nullable: true),
                    TrackingEta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrackingStatusAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTrackingAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrackingRegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrackingStoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PollLockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_shipments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ship_from_addresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Company = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultKey = table.Column<Guid>(type: "uuid", nullable: true),
                    Validation = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ValidationMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValidatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ship_from_addresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shipping_labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReturnShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderShipmentId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderRateId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    QuoteRateKey = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    ActiveShipmentKey = table.Column<Guid>(type: "uuid", nullable: true),
                    Carrier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Service = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CostCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    LabelFormat = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    LabelKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LabelContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EstimatedDelivery = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CustomsJson = table.Column<string>(type: "text", nullable: true),
                    AddressOverrideReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PurchasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurchasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PrintedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProviderRefundId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RefundStatus = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RefundRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextRefundCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_labels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shipping_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FreeShippingThresholdCents = table.Column<long>(type: "bigint", nullable: true),
                    ZonesJson = table.Column<string>(type: "text", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shipping_quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    RatesJson = table.Column<string>(type: "text", nullable: false),
                    ProviderShipmentId = table.Column<string>(type: "text", nullable: true),
                    AddressStatus = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_quotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shipping_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    HandlingDays = table.Column<int>(type: "integer", nullable: true),
                    LabelFormat = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ReturnAddressId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shipping_webhook_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_webhook_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tracking_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReturnShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracking_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracking_events_order_shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "order_shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracking_events_return_shipments_ReturnShipmentId",
                        column: x => x.ReturnShipmentId,
                        principalTable: "return_shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_TrackingStoppedAt_LastTrackingAt",
                table: "order_shipments",
                columns: new[] { "TrackingStoppedAt", "LastTrackingAt" });

            migrationBuilder.CreateIndex(
                name: "IX_package_presets_SellerId",
                table: "package_presets",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_return_shipments_OrderId",
                table: "return_shipments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_return_shipments_ReturnId",
                table: "return_shipments",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_return_shipments_TrackingNumber",
                table: "return_shipments",
                column: "TrackingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ship_from_addresses_SellerId_DefaultKey",
                table: "ship_from_addresses",
                columns: new[] { "SellerId", "DefaultKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ship_from_addresses_SellerId_ScopeKey",
                table: "ship_from_addresses",
                columns: new[] { "SellerId", "ScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_ActiveShipmentKey",
                table: "shipping_labels",
                column: "ActiveShipmentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_IdempotencyKey",
                table: "shipping_labels",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_OrderId",
                table: "shipping_labels",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_ProviderTransactionId",
                table: "shipping_labels",
                column: "ProviderTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_QuoteRateKey",
                table: "shipping_labels",
                column: "QuoteRateKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_RefundStatus_NextRefundCheckAt",
                table: "shipping_labels",
                columns: new[] { "RefundStatus", "NextRefundCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shipping_labels_SellerId_CreatedAt",
                table: "shipping_labels",
                columns: new[] { "SellerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shipping_profiles_SellerId",
                table: "shipping_profiles",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_quotes_ExpiresAt",
                table: "shipping_quotes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_quotes_OrderId",
                table: "shipping_quotes",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_settings_SellerId_ScopeKey",
                table: "shipping_settings",
                columns: new[] { "SellerId", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipping_webhook_events_Provider_EventId",
                table: "shipping_webhook_events",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipping_webhook_events_ReceivedAt",
                table: "shipping_webhook_events",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_tracking_events_ReturnShipmentId_DedupeKey",
                table: "tracking_events",
                columns: new[] { "ReturnShipmentId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracking_events_ShipmentId_DedupeKey",
                table: "tracking_events",
                columns: new[] { "ShipmentId", "DedupeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "package_presets");

            migrationBuilder.DropTable(
                name: "ship_from_addresses");

            migrationBuilder.DropTable(
                name: "shipping_labels");

            migrationBuilder.DropTable(
                name: "shipping_profiles");

            migrationBuilder.DropTable(
                name: "shipping_quotes");

            migrationBuilder.DropTable(
                name: "shipping_settings");

            migrationBuilder.DropTable(
                name: "shipping_webhook_events");

            migrationBuilder.DropTable(
                name: "tracking_events");

            migrationBuilder.DropTable(
                name: "return_shipments");

            migrationBuilder.DropIndex(
                name: "IX_order_shipments_TrackingStoppedAt_LastTrackingAt",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "AttentionAt",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "AttentionReason",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "NeedsAttention",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "DefaultPackageId",
                table: "products");

            migrationBuilder.DropColumn(
                name: "LabelId",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "LastTrackingAt",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "PollLockedUntil",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingEta",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingRegisteredAt",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStatus",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStatusAt",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStatusDetail",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStoppedAt",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "ShippingEmails",
                table: "notification_preferences");
        }
    }
}
