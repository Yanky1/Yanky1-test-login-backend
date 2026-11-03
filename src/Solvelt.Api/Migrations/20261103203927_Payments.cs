using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PaymentEmails",
                table: "notification_preferences",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ledger_journals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceRef = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Memo = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_journals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_disputes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    FeeCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    EvidenceDueBy = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_disputes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_job_leases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_job_leases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "payment_notices",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_notices", x => new { x.SellerId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "payment_payouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripePayoutId = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ArrivalDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_payouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    MarketplaceOrderId = table.Column<string>(type: "text", nullable: true),
                    MarketplaceBookingId = table.Column<string>(type: "text", nullable: true),
                    Ref = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_references", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BatchDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StripeTransferId = table.Column<string>(type: "text", nullable: true),
                    DestinationAccountId = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    Retries = table.Column<int>(type: "integer", nullable: false),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_transfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payout_accounts",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetailsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    ChargesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PayoutsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RequirementsDueJson = table.Column<string>(type: "text", nullable: false),
                    RequirementsPastDueJson = table.Column<string>(type: "text", nullable: false),
                    DisabledReason = table.Column<string>(type: "text", nullable: true),
                    RequirementsDeadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PayoutInterval = table.Column<string>(type: "text", nullable: true),
                    PayoutDelayDays = table.Column<int>(type: "integer", nullable: true),
                    BankLast4 = table.Column<string>(type: "text", nullable: true),
                    BankName = table.Column<string>(type: "text", nullable: true),
                    LastEventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_accounts", x => x.SellerId);
                });

            migrationBuilder.CreateTable(
                name: "payout_holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    PlacedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PlacedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_holds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "processed_payment_events",
                columns: table => new
                {
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_payment_events", x => new { x.Source, x.EventId });
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_flags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_flags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Account = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ledger_entries_ledger_journals_JournalId",
                        column: x => x.JournalId,
                        principalTable: "ledger_journals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_BookingId",
                table: "ledger_entries",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_JournalId",
                table: "ledger_entries",
                column: "JournalId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_OrderId",
                table: "ledger_entries",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_SellerId_Currency_Account",
                table: "ledger_entries",
                columns: new[] { "SellerId", "Currency", "Account" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_journals_BookingId",
                table: "ledger_journals",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_journals_IdempotencyKey",
                table: "ledger_journals",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_journals_OrderId",
                table: "ledger_journals",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_journals_SellerId_CreatedAt",
                table: "ledger_journals",
                columns: new[] { "SellerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_ExternalId",
                table: "payment_disputes",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_SellerId",
                table: "payment_disputes",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_payouts_SellerId",
                table: "payment_payouts",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_payouts_StripePayoutId",
                table: "payment_payouts",
                column: "StripePayoutId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_EventId_Ref",
                table: "payment_references",
                columns: new[] { "EventId", "Ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_MarketplaceBookingId",
                table: "payment_references",
                column: "MarketplaceBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_MarketplaceOrderId",
                table: "payment_references",
                column: "MarketplaceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_Ref",
                table: "payment_references",
                column: "Ref");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transfers_IdempotencyKey",
                table: "payment_transfers",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transfers_SellerId",
                table: "payment_transfers",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transfers_Status_NextAttemptAt",
                table: "payment_transfers",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_transfers_StripeTransferId",
                table: "payment_transfers",
                column: "StripeTransferId");

            migrationBuilder.CreateIndex(
                name: "IX_payout_accounts_StripeAccountId",
                table: "payout_accounts",
                column: "StripeAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payout_holds_SellerId_ReleasedAt",
                table: "payout_holds",
                columns: new[] { "SellerId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_flags_ResolvedAt",
                table: "reconciliation_flags",
                column: "ResolvedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "payment_disputes");

            migrationBuilder.DropTable(
                name: "payment_job_leases");

            migrationBuilder.DropTable(
                name: "payment_notices");

            migrationBuilder.DropTable(
                name: "payment_payouts");

            migrationBuilder.DropTable(
                name: "payment_references");

            migrationBuilder.DropTable(
                name: "payment_transfers");

            migrationBuilder.DropTable(
                name: "payout_accounts");

            migrationBuilder.DropTable(
                name: "payout_holds");

            migrationBuilder.DropTable(
                name: "processed_payment_events");

            migrationBuilder.DropTable(
                name: "reconciliation_flags");

            migrationBuilder.DropTable(
                name: "ledger_journals");

            migrationBuilder.DropColumn(
                name: "PaymentEmails",
                table: "notification_preferences");
        }
    }
}
