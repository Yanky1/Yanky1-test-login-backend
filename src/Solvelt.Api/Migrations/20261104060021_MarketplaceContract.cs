using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CatalogEverListed",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CatalogListed",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CatalogStockState",
                table: "products",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogUnlistReason",
                table: "products",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CatalogVersion",
                table: "products",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "product_questions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrderKey",
                table: "outbox_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "catalog_counter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_counter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReporterRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModerationReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_reports", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "catalog_counter",
                columns: new[] { "Id", "Value" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_products_CatalogVersion",
                table: "products",
                column: "CatalogVersion",
                unique: true,
                filter: "\"CatalogVersion\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_product_questions_ExternalId",
                table: "product_questions",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_AggregateId_OrderKey",
                table: "outbox_messages",
                columns: new[] { "AggregateId", "OrderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_content_reports_ModerationReportId",
                table: "content_reports",
                column: "ModerationReportId");

            migrationBuilder.CreateIndex(
                name: "IX_content_reports_ReportId",
                table: "content_reports",
                column: "ReportId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_counter");

            migrationBuilder.DropTable(
                name: "content_reports");

            migrationBuilder.DropIndex(
                name: "IX_products_CatalogVersion",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_product_questions_ExternalId",
                table: "product_questions");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_AggregateId_OrderKey",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "CatalogEverListed",
                table: "products");

            migrationBuilder.DropColumn(
                name: "CatalogListed",
                table: "products");

            migrationBuilder.DropColumn(
                name: "CatalogStockState",
                table: "products");

            migrationBuilder.DropColumn(
                name: "CatalogUnlistReason",
                table: "products");

            migrationBuilder.DropColumn(
                name: "CatalogVersion",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "product_questions");

            migrationBuilder.DropColumn(
                name: "OrderKey",
                table: "outbox_messages");
        }
    }
}
