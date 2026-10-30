using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Commonage.Web.Data;

/// <summary>
/// Search ignores accents by passing documents and search text through <see cref="CommonageDbContext.FoldFunction"/>.
/// The AddAccentFolding migration creates that function from built-in PostgreSQL functions only, so it works on
/// any server: it strips combining accents and Arabic vowel marks and spells out ß, æ, œ, ø, ł, đ, þ, ħ and ı.
/// When the server has the unaccent extension, <see cref="TryUseUnaccentAsync"/> switches the function to it at
/// startup and re-indexes all items. That also happens on the first start after an administrator installs the
/// extension later. To change what the function does, a migration recreates the built-in version (and re-indexes);
/// the next start then switches to unaccent again.
/// </summary>
public static class AccentFolding
{
    public static async Task<bool> TryUseUnaccentAsync(CommonageDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var database = db.Database;
        if (await database.SqlQuery<bool>($"""
                SELECT prosrc LIKE '%unaccent%' AS "Value" FROM pg_proc WHERE oid = 'commonage_fold(text)'::regprocedure
                """).SingleAsync(cancellationToken))
            return true;

        if (!await database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT FROM pg_available_extensions WHERE name = 'unaccent') AS "Value"
                """).SingleAsync(cancellationToken))
        {
            logger.LogWarning(
                "The PostgreSQL extension unaccent is not installed on the database server, so search uses a built-in " +
                "accent folding that covers most, but not all, letters. Commonage switches to unaccent on the first " +
                "start after it is installed (it is part of the PostgreSQL contrib package).");
            return false;
        }

        try
        {
            // One statement, so it either completes or changes nothing. The function names the extension's schema,
            // so it does not depend on the search path. unaccent leaves Arabic vowel marks alone, so they are removed
            // as in the built-in version. Updating every item recomputes its stored search column.
            await database.ExecuteSqlRawAsync("""
                DO $do$
                DECLARE s text;
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS unaccent;
                    SELECT extnamespace::regnamespace::text INTO s FROM pg_extension WHERE extname = 'unaccent';
                    EXECUTE format(
                        'CREATE OR REPLACE FUNCTION commonage_fold(text) RETURNS text LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS %L',
                        format($f$SELECT regexp_replace(%s.unaccent(%L::regdictionary, normalize($1, NFC)), '[\u0640\u064b-\u065f\u0670]', '', 'g')$f$,
                            s, s || '.unaccent'));
                    UPDATE "ArchiveItems" SET "Title" = "Title";
                END
                $do$
                """, cancellationToken);
        }
        catch (PostgresException ex)
        {
            logger.LogWarning(
                "Could not enable the PostgreSQL extension unaccent ({Reason}), so search uses a built-in accent folding " +
                "that covers most, but not all, letters. A database administrator can run CREATE EXTENSION unaccent; " +
                "in the Commonage database, and Commonage switches to it on the next start.", ex.MessageText);
            return false;
        }

        logger.LogInformation("Search now uses the PostgreSQL extension unaccent. All items were re-indexed.");
        return true;
    }
}
