using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContributionLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Introduction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CollectionId = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContributionLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContributionLinks_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contributions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArchiveItemId = table.Column<int>(type: "integer", nullable: false),
                    ContributionLinkId = table.Column<int>(type: "integer", nullable: true),
                    ContributorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContributorEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ShowName = table.Column<bool>(type: "boolean", nullable: false),
                    Rights = table.Column<int>(type: "integer", nullable: false),
                    RightsHolder = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConsentText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contributions_ArchiveItems_ArchiveItemId",
                        column: x => x.ArchiveItemId,
                        principalTable: "ArchiveItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Contributions_ContributionLinks_ContributionLinkId",
                        column: x => x.ContributionLinkId,
                        principalTable: "ContributionLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContributionLinks_CollectionId",
                table: "ContributionLinks",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContributionLinks_Token",
                table: "ContributionLinks",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributions_ArchiveItemId",
                table: "Contributions",
                column: "ArchiveItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributions_ContributionLinkId",
                table: "Contributions",
                column: "ContributionLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Contributions");

            migrationBuilder.DropTable(
                name: "ContributionLinks");
        }
    }
}
