using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogueFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AltText",
                table: "MediaFiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultRightsUri",
                table: "ArchiveSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Credit",
                table: "ArchiveItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InternalNotes",
                table: "ArchiveItems",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLocation",
                table: "ArchiveItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivedFrom",
                table: "ArchiveItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivedOn",
                table: "ArchiveItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceNumber",
                table: "ArchiveItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RightsHolder",
                table: "ArchiveItems",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RightsUri",
                table: "ArchiveItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_ReferenceNumber",
                table: "ArchiveItems",
                column: "ReferenceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveItems_ReferenceNumber",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "AltText",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "DefaultRightsUri",
                table: "ArchiveSettings");

            migrationBuilder.DropColumn(
                name: "Credit",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "InternalNotes",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "OriginalLocation",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "ReceivedFrom",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "ReceivedOn",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "ReferenceNumber",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "RightsHolder",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "RightsUri",
                table: "ArchiveItems");
        }
    }
}
