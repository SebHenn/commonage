using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StoryId",
                table: "AuditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoryBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StoryId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ArchiveItemId = table.Column<int>(type: "integer", nullable: true),
                    Caption = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryBlocks", x => x.Id);
                    table.CheckConstraint("CK_StoryBlocks_Kind", "(\"Kind\" = 'Text' AND \"ArchiveItemId\" IS NULL AND \"Text\" IS NOT NULL AND \"Caption\" IS NULL) OR (\"Kind\" = 'Item' AND \"ArchiveItemId\" IS NOT NULL AND \"Text\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_StoryBlocks_ArchiveItems_ArchiveItemId",
                        column: x => x.ArchiveItemId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoryBlocks_Stories_StoryId",
                        column: x => x.StoryId,
                        principalTable: "Stories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_StoryId",
                table: "AuditEntries",
                column: "StoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Stories_Position",
                table: "Stories",
                column: "Position");

            migrationBuilder.CreateIndex(
                name: "IX_StoryBlocks_ArchiveItemId",
                table: "StoryBlocks",
                column: "ArchiveItemId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryBlocks_StoryId_Position",
                table: "StoryBlocks",
                columns: new[] { "StoryId", "Position" });

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_Stories_StoryId",
                table: "AuditEntries",
                column: "StoryId",
                principalTable: "Stories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_Stories_StoryId",
                table: "AuditEntries");

            migrationBuilder.DropTable(
                name: "StoryBlocks");

            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_StoryId",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "StoryId",
                table: "AuditEntries");
        }
    }
}
