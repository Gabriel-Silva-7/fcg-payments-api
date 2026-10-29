using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Payments.Migrations
{
    /// <inheritdoc />
    public partial class PaymentRecoveryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRecoveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConflictId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRecoveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRecoveryAttempts_MessageConflicts_ConflictId",
                        column: x => x.ConflictId,
                        principalTable: "MessageConflicts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecoveryAttempts_ConflictId_AttemptedAtUtc",
                table: "PaymentRecoveryAttempts",
                columns: new[] { "ConflictId", "AttemptedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRecoveryAttempts");
        }
    }
}
