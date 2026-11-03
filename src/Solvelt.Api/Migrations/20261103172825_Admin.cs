using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Admin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleLimitMinutes",
                table: "Sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Sessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MfaAt",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StepUpAt",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModeratedAt",
                table: "products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModeratedBy",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModerationPrevStatus",
                table: "products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModerationReason",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetId",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "application_assignments",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedTo = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_assignments", x => x.SellerId);
                });

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSucceeded = table.Column<bool>(type: "boolean", nullable: false),
                    LastResult = table.Column<string>(type: "text", nullable: true),
                    Runs = table.Column<long>(type: "bigint", nullable: false),
                    Failures = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "moderation_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    ReporterKind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<string>(type: "text", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "staff_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "staff_members",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_members", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetType_TargetId",
                table: "AuditEvents",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Type",
                table: "AuditEvents",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_admin_notes_SellerId",
                table: "admin_notes",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_notes_UserId",
                table: "admin_notes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_reports_Kind_TargetId",
                table: "moderation_reports",
                columns: new[] { "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_moderation_reports_Status_CreatedAt",
                table: "moderation_reports",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_invitations_Email",
                table: "staff_invitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_staff_invitations_TokenHash",
                table: "staff_invitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_notes");

            migrationBuilder.DropTable(
                name: "application_assignments");

            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropTable(
                name: "moderation_reports");

            migrationBuilder.DropTable(
                name: "platform_settings");

            migrationBuilder.DropTable(
                name: "staff_invitations");

            migrationBuilder.DropTable(
                name: "staff_members");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TargetType_TargetId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Type",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IdleLimitMinutes",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "MfaAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "StepUpAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ModeratedAt",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ModeratedBy",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ModerationPrevStatus",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ModerationReason",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "AuditEvents");
        }
    }
}
