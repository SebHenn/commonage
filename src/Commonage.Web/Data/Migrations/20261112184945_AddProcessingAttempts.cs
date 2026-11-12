using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessingAttempts",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingAttempts",
                table: "MediaFiles");
        }
    }
}
