using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Demo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "demo_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "demo_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_demo_batches_SellerId",
                table: "demo_batches",
                column: "SellerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_demo_records_BatchId",
                table: "demo_records",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_demo_records_EntityId",
                table: "demo_records",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_demo_records_SellerId_Kind",
                table: "demo_records",
                columns: new[] { "SellerId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "demo_batches");

            migrationBuilder.DropTable(
                name: "demo_records");
        }
    }
}
