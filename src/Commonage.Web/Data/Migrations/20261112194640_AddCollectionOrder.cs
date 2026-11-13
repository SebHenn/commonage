using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverMediaFileId",
                table: "Collections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "Collections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Sort",
                table: "Collections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Collections_CoverMediaFileId",
                table: "Collections",
                column: "CoverMediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_Position",
                table: "Collections",
                column: "Position");

            migrationBuilder.AddForeignKey(
                name: "FK_Collections_MediaFiles_CoverMediaFileId",
                table: "Collections",
                column: "CoverMediaFileId",
                principalTable: "MediaFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Collections_MediaFiles_CoverMediaFileId",
                table: "Collections");

            migrationBuilder.DropIndex(
                name: "IX_Collections_CoverMediaFileId",
                table: "Collections");

            migrationBuilder.DropIndex(
                name: "IX_Collections_Position",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "CoverMediaFileId",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "Sort",
                table: "Collections");
        }
    }
}
