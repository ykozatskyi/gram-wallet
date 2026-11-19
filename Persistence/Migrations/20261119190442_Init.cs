using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GramWalletService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payout_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    destination_raw = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    destination_friendly = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount_nano = table.Column<long>(type: "bigint", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    wallet_seqno = table.Column<int>(type: "integer", nullable: true),
                    external_message_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    transaction_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    signed_boc = table.Column<string>(type: "text", nullable: true),
                    valid_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    resign_count = table.Column<int>(type: "integer", nullable: false),
                    sign_failures = table.Column<int>(type: "integer", nullable: false),
                    last_send = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_broadcast_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fee_reserve_nano = table.Column<long>(type: "bigint", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    insufficient_notified = table.Column<bool>(type: "boolean", nullable: false),
                    terminal_notified = table.Column<bool>(type: "boolean", nullable: false),
                    success_notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payouts", x => x.id);
                    table.CheckConstraint("ck_payouts_amount_positive", "amount_nano > 0");
                });

            migrationBuilder.CreateTable(
                name: "payout_tracked_hashes",
                columns: table => new
                {
                    payout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_tracked_hashes", x => new { x.payout_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_payout_tracked_hashes_payouts_payout_id",
                        column: x => x.payout_id,
                        principalTable: "payouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payouts_created_at_utc",
                table: "payouts",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_payouts_status_created_at_utc",
                table: "payouts",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_payouts_request_id",
                table: "payouts",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payout_settings");

            migrationBuilder.DropTable(
                name: "payout_tracked_hashes");

            migrationBuilder.DropTable(
                name: "payouts");
        }
    }
}
