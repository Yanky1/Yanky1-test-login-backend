using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrdersReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "outbox_messages");
        }
    }
}
