using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemFileText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractedText",
                table: "MediaFiles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemFileTexts",
                columns: table => new
                {
                    ArchiveItemId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple', commonage_fold(coalesce(\"Text\", ''))), 'D')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemFileTexts", x => x.ArchiveItemId);
                    table.ForeignKey(
                        name: "FK_ItemFileTexts_ArchiveItems_ArchiveItemId",
                        column: x => x.ArchiveItemId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemFileTexts_SearchVector",
                table: "ItemFileTexts",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemFileTexts");

            migrationBuilder.DropColumn(
                name: "ExtractedText",
                table: "MediaFiles");
        }
    }
}
