using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonage.Web.Data.Migrations
{
    /// <summary>
    /// Part 3 of M30: every tag of the kind Person (2) becomes a person, in one transaction (a migration is one). The people are
    /// "May be shown while living" only when an item linked to them passes the item rule written in SQL below, and hidden
    /// otherwise; their <c>DisclosureChangedAt</c> stays empty, which is what the review list of curators looks for. The rule is
    /// the item rule of <c>Visibility.Item</c> with one deliberate difference: an embargo must have ended on an earlier day than
    /// today (<c>&lt; CURRENT_DATE</c>), so a difference of a day between the database's and the archive's time zone can only keep a
    /// person hidden, never show one. A test runs both over the same items.
    /// </summary>
    public partial class PersonTagsToPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One row per person tag, with the address of its person made here: 12 characters of Crockford's base32 from 60 random
            // bits (twelve bytes of a random UUID, not the ones with its version and variant bits; 256 is a multiple of 32, so no bias).
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE person_tag_map ON COMMIT DROP AS
                SELECT t."Id" AS tag_id,
                       t."Name" AS name,
                       (SELECT string_agg(substr('0123456789abcdefghjkmnpqrstvwxyz', (get_byte(r.bytes, u.i) % 32) + 1, 1), '' ORDER BY u.ord)
                          FROM unnest(ARRAY[0, 1, 2, 3, 4, 5, 9, 10, 11, 12, 13, 14]) WITH ORDINALITY AS u(i, ord)) AS public_id,
                       EXISTS (SELECT 1
                                 FROM "ArchiveItemTag" it
                                 JOIN "ArchiveItems" i ON i."Id" = it."ItemsId"
                                WHERE it."TagsId" = t."Id"
                                  AND i."Status" = 1
                                  AND i."DeletedAt" IS NULL
                                  AND (i."PublicFrom" IS NULL OR i."PublicFrom" < CURRENT_DATE)) AS shown,
                       NULL::integer AS person_id
                  FROM "Tags" t
                 -- The WHERE names the tag, so that the subquery depends on the row and is run again for each tag, not once.
                 CROSS JOIN LATERAL (SELECT decode(replace(gen_random_uuid()::text, '-', ''), 'hex') AS bytes WHERE t."Id" IS NOT NULL) r
                 WHERE t."Kind" = 2;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "People" ("PublicId", "Name", "Disclosure", "DisclosureChangedAt", "CreatedAt", "UpdatedAt")
                SELECT m.public_id, m.name, CASE WHEN m.shown THEN 'MayBeShown' ELSE 'Hidden' END, NULL, now(), now()
                  FROM person_tag_map m
                 ORDER BY m.tag_id;

                UPDATE person_tag_map m
                   SET person_id = p."Id"
                  FROM "People" p
                 WHERE p."PublicId" = m.public_id;

                -- Every item with the tag, the ones in the bin included.
                INSERT INTO "ItemPeople" ("ArchiveItemId", "PersonId", "CreatedAt")
                SELECT it."ItemsId", m.person_id, now()
                  FROM "ArchiveItemTag" it
                  JOIN person_tag_map m ON m.tag_id = it."TagsId";
                """);

            // The person tags leave the items and the tag table, and the words the search reads from tags are made again for the
            // items that had one. A name that is also a topic tag (another tag row, such as the same name without accents) stays.
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE person_tag_items ON COMMIT DROP AS
                SELECT DISTINCT it."ItemsId" AS item_id
                  FROM "ArchiveItemTag" it
                  JOIN person_tag_map m ON m.tag_id = it."TagsId";

                DELETE FROM "ArchiveItemTag" it
                 USING person_tag_map m
                 WHERE it."TagsId" = m.tag_id;

                DELETE FROM "Tags" t
                 USING person_tag_map m
                 WHERE t."Id" = m.tag_id;

                UPDATE "ArchiveItems" i
                   SET "TagNames" = (SELECT string_agg(t."Name", ' ' ORDER BY t."Id")
                                       FROM "ArchiveItemTag" it
                                       JOIN "Tags" t ON t."Id" = it."TagsId"
                                      WHERE it."ItemsId" = i."Id")
                 WHERE i."Id" IN (SELECT item_id FROM person_tag_items);
                """);

            // One line for all of them, without a name, and none when there was no person tag.
            migrationBuilder.Sql("""
                INSERT INTO "AuditEntries" ("At", "Action")
                SELECT now(), 'PersonTagsConverted'
                 WHERE EXISTS (SELECT 1 FROM person_tag_map);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One way: the people stay, and the tags are not made again. Restore a backup to go back.
        }
    }
}
