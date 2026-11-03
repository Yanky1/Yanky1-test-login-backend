using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Teams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_seller_accounts_UserId",
                table: "seller_accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveAccountId",
                table: "Sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastAccountId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AllBrands = table.Column<bool>(type: "boolean", nullable: false),
                    BrandIdsJson = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SendCount = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_invitations_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AllBrands = table.Column<bool>(type: "boolean", nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_memberships_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "brands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Slug = table.Column<string>(type: "character varying(90)", maxLength: 90, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContactEmail = table.Column<string>(type: "text", nullable: true),
                    Website = table.Column<string>(type: "text", nullable: true),
                    LogoKey = table.Column<string>(type: "text", nullable: true),
                    LogoContentType = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_brands_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "membership_brands",
                columns: table => new
                {
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_brands", x => new { x.MembershipId, x.BrandId });
                    table.ForeignKey(
                        name: "FK_membership_brands_account_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "account_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_membership_brands_brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_seller_accounts_UserId",
                table: "seller_accounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_account_invitations_Email",
                table: "account_invitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_account_invitations_SellerId_Email",
                table: "account_invitations",
                columns: new[] { "SellerId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_account_invitations_TokenHash",
                table: "account_invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_memberships_SellerId_UserId",
                table: "account_memberships",
                columns: new[] { "SellerId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_memberships_UserId",
                table: "account_memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_brands_SellerId_Name",
                table: "brands",
                columns: new[] { "SellerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brands_SellerId_Slug",
                table: "brands",
                columns: new[] { "SellerId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_brands_BrandId",
                table: "membership_brands",
                column: "BrandId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_invitations");

            migrationBuilder.DropTable(
                name: "membership_brands");

            migrationBuilder.DropTable(
                name: "account_memberships");

            migrationBuilder.DropTable(
                name: "brands");

            migrationBuilder.DropIndex(
                name: "IX_seller_accounts_UserId",
                table: "seller_accounts");

            migrationBuilder.DropColumn(
                name: "ActiveAccountId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LastAccountId",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_seller_accounts_UserId",
                table: "seller_accounts",
                column: "UserId",
                unique: true);
        }
    }
}
