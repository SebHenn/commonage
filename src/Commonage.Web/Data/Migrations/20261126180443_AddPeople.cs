using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PersonId",
                table: "AuditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<string>(type: "character(12)", fixedLength: true, maxLength: 12, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FoldedName = table.Column<string>(type: "text", nullable: false, computedColumnSql: "lower(commonage_fold(\"Name\"))", stored: true),
                    SortName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Disclosure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Hidden"),
                    DisclosureChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BornText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BornFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    BornTo = table.Column<DateOnly>(type: "date", nullable: true),
                    DiedText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DiedFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    DiedTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Biography = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CuratorNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemPeople",
                columns: table => new
                {
                    ArchiveItemId = table.Column<int>(type: "integer", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPeople", x => new { x.ArchiveItemId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_ItemPeople_ArchiveItems_ArchiveItemId",
                        column: x => x.ArchiveItemId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemPeople_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonNames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FoldedName = table.Column<string>(type: "text", nullable: false, computedColumnSql: "lower(commonage_fold(\"Name\"))", stored: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    ShownToVisitors = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonNames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonNames_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_PersonId",
                table: "AuditEntries",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemPeople_PersonId",
                table: "ItemPeople",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_People_FoldedName",
                table: "People",
                column: "FoldedName");

            migrationBuilder.CreateIndex(
                name: "IX_People_PublicId",
                table: "People",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonNames_FoldedName",
                table: "PersonNames",
                column: "FoldedName");

            migrationBuilder.CreateIndex(
                name: "IX_PersonNames_PersonId",
                table: "PersonNames",
                column: "PersonId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_People_PersonId",
                table: "AuditEntries",
                column: "PersonId",
                principalTable: "People",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_People_PersonId",
                table: "AuditEntries");

            migrationBuilder.DropTable(
                name: "ItemPeople");

            migrationBuilder.DropTable(
                name: "PersonNames");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_PersonId",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "PersonId",
                table: "AuditEntries");
        }
    }
}
