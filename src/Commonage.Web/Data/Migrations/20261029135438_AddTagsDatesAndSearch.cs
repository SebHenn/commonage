using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagsDatesAndSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DateFrom",
                table: "ArchiveItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DateText",
                table: "ArchiveItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateTo",
                table: "ArchiveItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagNames",
                table: "ArchiveItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "ArchiveItems",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('simple', coalesce(\"TagNames\", '') || ' ' || coalesce(\"DateText\", '')), 'B') || setweight(to_tsvector('simple', coalesce(\"Description\", '')), 'C')",
                stored: true);

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveItemTag",
                columns: table => new
                {
                    ItemsId = table.Column<int>(type: "integer", nullable: false),
                    TagsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveItemTag", x => new { x.ItemsId, x.TagsId });
                    table.ForeignKey(
                        name: "FK_ArchiveItemTag_ArchiveItems_ItemsId",
                        column: x => x.ItemsId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArchiveItemTag_Tags_TagsId",
                        column: x => x.TagsId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_DateFrom",
                table: "ArchiveItems",
                column: "DateFrom");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_DateTo",
                table: "ArchiveItems",
                column: "DateTo");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_SearchVector",
                table: "ArchiveItems",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItemTag_TagsId",
                table: "ArchiveItemTag",
                column: "TagsId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NormalizedName",
                table: "Tags",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveItemTag");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveItems_DateFrom",
                table: "ArchiveItems");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveItems_DateTo",
                table: "ArchiveItems");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveItems_SearchVector",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "DateFrom",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "DateText",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "DateTo",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "TagNames",
                table: "ArchiveItems");
        }
    }
}
