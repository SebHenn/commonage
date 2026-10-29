using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <summary>
    /// Makes search ignore accents: "Muhle" finds "Mühle". The "commonage" text search configuration is
    /// "simple" with the unaccent dictionary in front. A generated column cannot change its expression on
    /// PostgreSQL before 17, so the search column and its index are dropped and created again.
    /// </summary>
    public partial class AddUnaccentSearch : Migration
    {
        private const string WithoutUnaccent = "setweight(to_tsvector('simple', coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('simple', coalesce(\"TagNames\", '') || ' ' || coalesce(\"DateText\", '')), 'B') || setweight(to_tsvector('simple', coalesce(\"Description\", '')), 'C')";
        private const string WithUnaccent = "setweight(to_tsvector('commonage', coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('commonage', coalesce(\"TagNames\", '') || ' ' || coalesce(\"DateText\", '')), 'B') || setweight(to_tsvector('commonage', coalesce(\"Description\", '')), 'C')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.Sql("""
                CREATE TEXT SEARCH CONFIGURATION commonage (COPY = simple);
                ALTER TEXT SEARCH CONFIGURATION commonage
                    ALTER MAPPING FOR hword, hword_part, word WITH unaccent, simple;
                """);

            ReplaceSearchVector(migrationBuilder, newSql: WithUnaccent);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ReplaceSearchVector(migrationBuilder, newSql: WithoutUnaccent);

            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION commonage;");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }

        private static void ReplaceSearchVector(MigrationBuilder migrationBuilder, string newSql)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveItems_SearchVector",
                table: "ArchiveItems");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "ArchiveItems");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "ArchiveItems",
                type: "tsvector",
                nullable: true,
                computedColumnSql: newSql,
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveItems_SearchVector",
                table: "ArchiveItems",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }
    }
}
