using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kasa.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DashboardVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DashboardVersion",
                table: "Companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DashboardVersion",
                table: "Companies");
        }
    }
}
