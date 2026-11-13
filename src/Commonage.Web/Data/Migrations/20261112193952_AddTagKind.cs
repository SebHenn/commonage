using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Tags",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Tags");
        }
    }
}
