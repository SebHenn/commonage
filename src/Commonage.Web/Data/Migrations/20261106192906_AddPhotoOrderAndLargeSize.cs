using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoOrderAndLargeSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Caption",
                table: "MediaFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasLarge",
                table: "MediaFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Photos keep the order they had, which was the order they arrived in.
            migrationBuilder.Sql("""
                UPDATE "MediaFiles" AS file
                SET "Position" = numbered.position
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "ArchiveItemId" ORDER BY "Id") - 1 AS position FROM "MediaFiles") AS numbered
                WHERE file."Id" = numbered."Id";
                """);

            migrationBuilder.AddColumn<int>(
                name: "RenderedRotation",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Rotation",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Archives that exist already have made a choice about what they show. Only new ones start with the large size.
            migrationBuilder.AddColumn<bool>(
                name: "LargePhotos",
                table: "ArchiveSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsTranscription",
                table: "ArchiveItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Caption",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "HasLarge",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RenderedRotation",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Rotation",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "LargePhotos",
                table: "ArchiveSettings");

            migrationBuilder.DropColumn(
                name: "NeedsTranscription",
                table: "ArchiveItems");
        }
    }
}
