using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kasa.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sprint3Erp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentAccountId",
                table: "BankTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpVoucherNo",
                table: "BankTransactions",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExportError",
                table: "BankTransactions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExportId",
                table: "BankTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExportStatus",
                table: "BankTransactions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExportedAt",
                table: "BankTransactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchRule",
                table: "BankTransactions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchStatus",
                table: "BankTransactions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MatchedAt",
                table: "BankTransactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CurrentAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxNumber = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Iban = table.Column<string>(type: "nvarchar(34)", maxLength: 34, nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrentAccounts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MatchAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankTransactionId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CurrentAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchAudits_BankTransactions_BankTransactionId",
                        column: x => x.BankTransactionId,
                        principalTable: "BankTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchAudits_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_MatchStatus_BookedAt",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "MatchStatus", "BookedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CurrentAccountId",
                table: "BankTransactions",
                column: "CurrentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_ExportId",
                table: "BankTransactions",
                column: "ExportId",
                unique: true,
                filter: "[ExportId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CurrentAccounts_CompanyId_Code",
                table: "CurrentAccounts",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrentAccounts_CompanyId_Iban",
                table: "CurrentAccounts",
                columns: new[] { "CompanyId", "Iban" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrentAccounts_CompanyId_TaxNumber",
                table: "CurrentAccounts",
                columns: new[] { "CompanyId", "TaxNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchAudits_BankTransactionId",
                table: "MatchAudits",
                column: "BankTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchAudits_CompanyId",
                table: "MatchAudits",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Unpublished",
                table: "OutboxMessages",
                column: "CreatedAt",
                filter: "[PublishedAt] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_CurrentAccounts_CurrentAccountId",
                table: "BankTransactions",
                column: "CurrentAccountId",
                principalTable: "CurrentAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_CurrentAccounts_CurrentAccountId",
                table: "BankTransactions");

            migrationBuilder.DropTable(
                name: "CurrentAccounts");

            migrationBuilder.DropTable(
                name: "MatchAudits");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_MatchStatus_BookedAt",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CurrentAccountId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_ExportId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "CurrentAccountId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ErpVoucherNo",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ExportError",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ExportId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ExportStatus",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ExportedAt",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchRule",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchStatus",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                table: "BankTransactions");
        }
    }
}
