using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    /// <remarks>C18 quantity offers (workstream W): offers + immutable offer versions, offer/gift fields on order lines, the giftUnavailable flag on seller orders. Additive only.</remarks>
    public partial class QuantityOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GiftUnavailable",
                table: "seller_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "GiftOfOfferId",
                table: "order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsGift",
                table: "order_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OfferId",
                table: "order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfferVersion",
                table: "order_lines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    FeedVersion = table.Column<long>(type: "bigint", nullable: false),
                    EverListed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "offer_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ScopeIdsJson = table.Column<string>(type: "text", nullable: false),
                    TiersJson = table.Column<string>(type: "text", nullable: false),
                    GiftsJson = table.Column<string>(type: "text", nullable: false),
                    ActiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offer_versions_offers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_offer_versions_OfferId_Version",
                table: "offer_versions",
                columns: new[] { "OfferId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offers_FeedVersion",
                table: "offers",
                column: "FeedVersion");

            migrationBuilder.CreateIndex(
                name: "IX_offers_SellerId_BrandId_Status",
                table: "offers",
                columns: new[] { "SellerId", "BrandId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offer_versions");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropColumn(
                name: "GiftUnavailable",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "GiftOfOfferId",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "IsGift",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "OfferId",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "OfferVersion",
                table: "order_lines");
        }
    }
}
