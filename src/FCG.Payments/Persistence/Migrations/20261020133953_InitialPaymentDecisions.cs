using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Payments.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPaymentDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "payment_decisions",
                schema: "public",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    placed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    delay_ticks = table.Column<long>(type: "bigint", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_decisions", x => x.order_id);
                    table.CheckConstraint("CK_payment_decisions_amounts", "price >= 0 AND approval_limit >= 0");
                    table.CheckConstraint("CK_payment_decisions_delay", "delay_ticks >= 0 AND delay_ticks <= 3000000000");
                    table.CheckConstraint("CK_payment_decisions_identity", "order_id <> '00000000-0000-0000-0000-000000000000'::uuid AND user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND game_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_payment_decisions_result", "(status = 'Approved' AND reason IS NULL AND price <= approval_limit) OR (status = 'Rejected' AND reason = 'AmountAboveLimit' AND reason IS NOT NULL AND price > approval_limit)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_decisions",
                schema: "public");
        }
    }
}
