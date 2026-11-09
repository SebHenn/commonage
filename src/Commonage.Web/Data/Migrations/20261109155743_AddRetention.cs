using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityLogKeptYears",
                table: "ArchiveSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedKeptMonths",
                table: "ArchiveSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedOrWithdrawnAt",
                table: "ArchiveItems",
                type: "timestamp with time zone",
                nullable: true);

            // When older ones were rejected or withdrawn is not recorded anywhere reliable, so their time starts with this
            // update. That way, setting a time never removes anything at once that an archive had before.
            migrationBuilder.Sql("""UPDATE "ArchiveItems" SET "RejectedOrWithdrawnAt" = now() WHERE "Status" IN (3, 4);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityLogKeptYears",
                table: "ArchiveSettings");

            migrationBuilder.DropColumn(
                name: "RejectedKeptMonths",
                table: "ArchiveSettings");

            migrationBuilder.DropColumn(
                name: "RejectedOrWithdrawnAt",
                table: "ArchiveItems");
        }
    }
}
