using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferenceCode",
                table: "Contributions",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArchiveSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    DefaultLanguage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccentColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    LogoId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AllowSearchEngines = table.Column<bool>(type: "boolean", nullable: false),
                    PrivacyReviewed = table.Column<bool>(type: "boolean", nullable: false),
                    ImprintNotNeeded = table.Column<bool>(type: "boolean", nullable: false),
                    RestoreTestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveTexts",
                columns: table => new
                {
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveTexts", x => new { x.Language, x.Kind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contributions_ReferenceCode",
                table: "Contributions",
                column: "ReferenceCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveSettings");

            migrationBuilder.DropTable(
                name: "ArchiveTexts");

            migrationBuilder.DropIndex(
                name: "IX_Contributions_ReferenceCode",
                table: "Contributions");

            migrationBuilder.DropColumn(
                name: "ReferenceCode",
                table: "Contributions");
        }
    }
}
