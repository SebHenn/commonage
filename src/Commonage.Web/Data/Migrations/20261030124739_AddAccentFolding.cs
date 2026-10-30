using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <summary>
    /// Makes search ignore accents: "Muhle" finds "Mühle". Documents and search text pass through commonage_fold,
    /// created here from built-in functions only, so it needs no extension. <see cref="AccentFolding"/> switches it
    /// to the unaccent extension at startup when the server has it. A generated column cannot change its expression
    /// on PostgreSQL before 17, so the search column and its index are dropped and created again.
    /// </summary>
    public partial class AddAccentFolding : Migration
    {
        private const string WithoutFolding = "setweight(to_tsvector('simple', coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('simple', coalesce(\"TagNames\", '') || ' ' || coalesce(\"DateText\", '')), 'B') || setweight(to_tsvector('simple', coalesce(\"Description\", '')), 'C')";
        private const string WithFolding = "setweight(to_tsvector('simple', commonage_fold(coalesce(\"Title\", ''))), 'A') || setweight(to_tsvector('simple', commonage_fold(coalesce(\"TagNames\", '') || ' ' || coalesce(\"DateText\", ''))), 'B') || setweight(to_tsvector('simple', commonage_fold(coalesce(\"Description\", ''))), 'C')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Decomposes letters (é becomes e plus a combining accent), drops the combining accents, Arabic vowel
            // marks and the Arabic stretching character, and spells out letters that have no decomposition, as
            // unaccent does. Finally composes what is left again, so Korean syllables and Japanese kana with
            // voicing marks come out as they went in.
            migrationBuilder.Sql("""
                CREATE FUNCTION commonage_fold(text) RETURNS text LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
                    SELECT normalize(translate(
                        replace(replace(replace(replace(replace(replace(replace(replace(
                            regexp_replace(normalize($1, NFD), '[\u0300-\u036f\u0640\u064b-\u065f\u0670]', '', 'g'),
                            'ß', 'ss'), 'ẞ', 'SS'), 'æ', 'ae'), 'Æ', 'AE'), 'œ', 'oe'), 'Œ', 'OE'), 'þ', 'th'), 'Þ', 'TH'),
                        'ØøŁłĐđÐðĦħı',
                        'OoLlDdDdHhi'), NFC)
                $$;
                """);

            ReplaceSearchVector(migrationBuilder, newSql: WithFolding);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ReplaceSearchVector(migrationBuilder, newSql: WithoutFolding);

            // The unaccent extension, if AccentFolding enabled it, stays installed. Nothing depends on it any more.
            migrationBuilder.Sql("DROP FUNCTION commonage_fold(text);");
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
