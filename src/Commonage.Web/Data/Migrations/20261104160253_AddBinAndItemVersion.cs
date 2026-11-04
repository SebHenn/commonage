using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBinAndItemVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ArchiveItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ArchiveItems",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ArchiveItems");
        }
    }
}
