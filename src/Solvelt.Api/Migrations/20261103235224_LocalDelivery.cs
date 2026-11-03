using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class LocalDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ShipLat",
                table: "seller_orders",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ShipLng",
                table: "seller_orders",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Eta",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "order_shipments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackingStatus",
                table: "order_shipments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingStatusAt",
                table: "order_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecipientName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Placement = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DriverMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ServerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: true),
                    Lng = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_client_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ClientAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_client_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_proofs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_proofs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    DriverMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartLat = table.Column<double>(type: "double precision", nullable: true),
                    StartLng = table.Column<double>(type: "double precision", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SameDayCutoffMinutes = table.Column<int>(type: "integer", nullable: true),
                    MinLeadMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxDaysAhead = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_window_usage",
                columns: table => new
                {
                    WindowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Booked = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_window_usage", x => new { x.WindowId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "driver_consents",
                columns: table => new
                {
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Consented = table.Column<bool>(type: "boolean", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_driver_consents", x => x.MembershipId);
                });

            migrationBuilder.CreateTable(
                name: "driver_locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lng = table.Column<double>(type: "double precision", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_driver_locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    WindowId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeeCents = table.Column<long>(type: "bigint", nullable: false),
                    DestLat = table.Column<double>(type: "double precision", nullable: true),
                    DestLng = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RouteId = table.Column<Guid>(type: "uuid", nullable: true),
                    StopSeq = table.Column<int>(type: "integer", nullable: false),
                    DriverMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastEventClientAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EtaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastEtaEventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LateNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredLat = table.Column<double>(type: "double precision", nullable: true),
                    DeliveredLng = table.Column<double>(type: "double precision", nullable: true),
                    Converted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tracking_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracking_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_windows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartMinutes = table.Column<int>(type: "integer", nullable: false),
                    EndMinutes = table.Column<int>(type: "integer", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    DaysMask = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_windows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_windows_delivery_settings_SettingId",
                        column: x => x.SettingId,
                        principalTable: "delivery_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    OriginLat = table.Column<double>(type: "double precision", nullable: true),
                    OriginLng = table.Column<double>(type: "double precision", nullable: true),
                    RadiusKm = table.Column<double>(type: "double precision", nullable: true),
                    PostcodesJson = table.Column<string>(type: "text", nullable: false),
                    PolygonJson = table.Column<string>(type: "text", nullable: true),
                    FeeCents = table.Column<long>(type: "bigint", nullable: false),
                    FreeOverCents = table.Column<long>(type: "bigint", nullable: true),
                    MinOrderCents = table.Column<long>(type: "bigint", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_zones_delivery_settings_SettingId",
                        column: x => x.SettingId,
                        principalTable: "delivery_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryId",
                table: "delivery_attempts",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_client_events_DeliveryId",
                table: "delivery_client_events",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_DeliveryId",
                table: "delivery_proofs",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_DeliveryId_ClientId",
                table: "delivery_proofs",
                columns: new[] { "DeliveryId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_routes_DriverMembershipId",
                table: "delivery_routes",
                column: "DriverMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_routes_SellerId_Date",
                table: "delivery_routes",
                columns: new[] { "SellerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_settings_SellerId_ScopeKey",
                table: "delivery_settings",
                columns: new[] { "SellerId", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_windows_SellerId",
                table: "delivery_windows",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_windows_SettingId",
                table: "delivery_windows",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_zones_SellerId",
                table: "delivery_zones",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_zones_SettingId",
                table: "delivery_zones",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_driver_locations_ExpiresAt",
                table: "driver_locations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_driver_locations_RouteId_At",
                table: "driver_locations",
                columns: new[] { "RouteId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_local_deliveries_Code",
                table: "local_deliveries",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_deliveries_OrderId",
                table: "local_deliveries",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_deliveries_RouteId",
                table: "local_deliveries",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_local_deliveries_SellerId_Date",
                table: "local_deliveries",
                columns: new[] { "SellerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_tracking_events_OrderId_OccurredAt",
                table: "tracking_events",
                columns: new[] { "OrderId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tracking_events_OrderId_SourceEventId",
                table: "tracking_events",
                columns: new[] { "OrderId", "SourceEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "delivery_client_events");

            migrationBuilder.DropTable(
                name: "delivery_proofs");

            migrationBuilder.DropTable(
                name: "delivery_routes");

            migrationBuilder.DropTable(
                name: "delivery_window_usage");

            migrationBuilder.DropTable(
                name: "delivery_windows");

            migrationBuilder.DropTable(
                name: "delivery_zones");

            migrationBuilder.DropTable(
                name: "driver_consents");

            migrationBuilder.DropTable(
                name: "driver_locations");

            migrationBuilder.DropTable(
                name: "local_deliveries");

            migrationBuilder.DropTable(
                name: "tracking_events");

            migrationBuilder.DropTable(
                name: "delivery_settings");

            migrationBuilder.DropColumn(
                name: "ShipLat",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "ShipLng",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "Eta",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStatus",
                table: "order_shipments");

            migrationBuilder.DropColumn(
                name: "TrackingStatusAt",
                table: "order_shipments");
        }
    }
}
