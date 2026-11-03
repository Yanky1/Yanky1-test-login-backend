using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Services : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "products",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Product");

            migrationBuilder.CreateTable(
                name: "booking_counters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Next = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_counters", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "booking_settings",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IcsTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IcsTokenCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_settings", x => x.SellerId);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MarketplaceBookingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Location = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BlockStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BlockEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    HoldExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalDeadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProposedStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RescheduleRequestedStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RescheduleRequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RescheduleCount = table.Column<int>(type: "integer", nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BuyerEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    BuyerPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AddressJson = table.Column<string>(type: "text", nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IntakeAnswersJson = table.Column<string>(type: "text", nullable: false),
                    AddOnsJson = table.Column<string>(type: "text", nullable: false),
                    ServiceTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    PricingModel = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    RateCents = table.Column<long>(type: "bigint", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    AddOnsCents = table.Column<long>(type: "bigint", nullable: false),
                    TravelFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    CancellationFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    AdditionalChargeCents = table.Column<long>(type: "bigint", nullable: false),
                    ActualMinutes = table.Column<int>(type: "integer", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeclineReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderDaySentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderHourSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PiiRedactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bookings_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bookings_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_blocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllDay = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_blocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_blocks_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "date_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    IntervalsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_date_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_date_overrides_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_profiles",
                columns: table => new
                {
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsProvider = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReminderDayBefore = table.Column<bool>(type: "boolean", nullable: false),
                    ReminderHourBefore = table.Column<bool>(type: "boolean", nullable: false),
                    IcsTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IcsTokenCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CalendarSeq = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_profiles", x => x.MembershipId);
                    table.ForeignKey(
                        name: "FK_provider_profiles_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceQuoteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BuyerEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    BuyerPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Location = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    AddressJson = table.Column<string>(type: "text", nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PreferredTimesJson = table.Column<string>(type: "text", nullable: false),
                    ServiceTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QuotePriceCents = table.Column<long>(type: "bigint", nullable: true),
                    QuoteDurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    SlotsJson = table.Column<string>(type: "text", nullable: false),
                    QuoteProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuoteExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QuoteNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QuotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QuotedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeclineReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PiiRedactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quote_requests_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_details",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PricingModel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: true),
                    MinMinutes = table.Column<int>(type: "integer", nullable: true),
                    MaxMinutes = table.Column<int>(type: "integer", nullable: true),
                    IncrementMinutes = table.Column<int>(type: "integer", nullable: false),
                    DefaultDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    BufferBeforeMinutes = table.Column<int>(type: "integer", nullable: false),
                    BufferAfterMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinNoticeHours = table.Column<int>(type: "integer", nullable: false),
                    MaxAdvanceDays = table.Column<int>(type: "integer", nullable: false),
                    SlotGranularityMinutes = table.Column<int>(type: "integer", nullable: false),
                    Online = table.Column<bool>(type: "boolean", nullable: false),
                    OnlineInstructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AtCustomer = table.Column<bool>(type: "boolean", nullable: false),
                    ServiceAreaJson = table.Column<string>(type: "text", nullable: false),
                    TravelFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    TravelBufferMinutes = table.Column<int>(type: "integer", nullable: false),
                    AtSeller = table.Column<bool>(type: "boolean", nullable: false),
                    SellerAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BookingMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovalHours = table.Column<int>(type: "integer", nullable: false),
                    IntakeJson = table.Column<string>(type: "text", nullable: false),
                    Requirements = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AddOnsJson = table.Column<string>(type: "text", nullable: false),
                    CancelFreeHours = table.Column<int>(type: "integer", nullable: false),
                    CancelFeePercent = table.Column<int>(type: "integer", nullable: false),
                    MaxReschedules = table.Column<int>(type: "integer", nullable: false),
                    NoShowFeePercent = table.Column<int>(type: "integer", nullable: false),
                    AssignmentRule = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReadyProviders = table.Column<int>(type: "integer", nullable: false),
                    RoundRobinCursor = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_details", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_service_details_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_providers",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_providers", x => new { x.ProductId, x.MembershipId });
                    table.ForeignKey(
                        name: "FK_service_providers_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_providers_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "working_hours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndMinute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_working_hours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_working_hours_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "booking_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Actor = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_booking_events_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "booking_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MarketplaceMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_booking_messages_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Type",
                table: "products",
                columns: new[] { "SellerId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_events_BookingId_At",
                table: "booking_events",
                columns: new[] { "BookingId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_messages_BookingId_CreatedAt",
                table: "booking_messages",
                columns: new[] { "BookingId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_messages_MarketplaceMessageId",
                table: "booking_messages",
                column: "MarketplaceMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_settings_IcsTokenHash",
                table: "booking_settings",
                column: "IcsTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_MarketplaceBookingId",
                table: "bookings",
                column: "MarketplaceBookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_Number",
                table: "bookings",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_ProductId",
                table: "bookings",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_ProviderId_BlockStartUtc",
                table: "bookings",
                columns: new[] { "ProviderId", "BlockStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_SellerId_Status_StartUtc",
                table: "bookings",
                columns: new[] { "SellerId", "Status", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_blocks_MembershipId_StartUtc",
                table: "calendar_blocks",
                columns: new[] { "MembershipId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_date_overrides_MembershipId_Date",
                table: "date_overrides",
                columns: new[] { "MembershipId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_IcsTokenHash",
                table: "provider_profiles",
                column: "IcsTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_SellerId",
                table: "provider_profiles",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_quote_requests_MarketplaceQuoteId",
                table: "quote_requests",
                column: "MarketplaceQuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quote_requests_ProductId",
                table: "quote_requests",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_quote_requests_SellerId_Status",
                table: "quote_requests",
                columns: new[] { "SellerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_service_providers_MembershipId",
                table: "service_providers",
                column: "MembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_working_hours_MembershipId_Day",
                table: "working_hours",
                columns: new[] { "MembershipId", "Day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_counters");

            migrationBuilder.DropTable(
                name: "booking_events");

            migrationBuilder.DropTable(
                name: "booking_messages");

            migrationBuilder.DropTable(
                name: "booking_settings");

            migrationBuilder.DropTable(
                name: "calendar_blocks");

            migrationBuilder.DropTable(
                name: "date_overrides");

            migrationBuilder.DropTable(
                name: "provider_profiles");

            migrationBuilder.DropTable(
                name: "quote_requests");

            migrationBuilder.DropTable(
                name: "service_details");

            migrationBuilder.DropTable(
                name: "service_providers");

            migrationBuilder.DropTable(
                name: "working_hours");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_products_SellerId_Type",
                table: "products");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "products");
        }
    }
}
