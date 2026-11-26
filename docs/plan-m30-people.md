# Plan for M30: people as entities

Status: **proposed; the choices in "Decisions" are open.** Four parts, in this order, each its own pull request, all four in one
release. The plan goes to the `privacy-reviewer` agent before part 1 starts, because people who may be living are the most
sensitive thing an archive names, and part 2 adds public addresses about them. The agent runs again after each part. Run `/gate`
before each push and `/walkthrough` for the pages. Commit subjects follow `M30, first part: ...`.

## What is wanted

A curator records a **person** once: the name they are known by, other names (a birth name, a married name, a nickname, the name
in another script), when they lived, and a short text. The curator links the person to the items they appear in or are named in.
Visitors read a person page at `/people/{id}` with the person's items, **but only when a curator has said the person has died, or
that they may be shown while living.** Everyone else stays with the curators. Roadmap "Later", item 1.

Today a person is a tag of kind `Person` (M19): a name, unique apart from its case, public on every published item that has it,
on `/people`, in the search (`ArchiveItem.TagNames` is in `SearchVector`), in `/items/{id}.json`, and in both kinds of export.
Recording who is in a photo therefore means publishing the name. The new model lets a curator write down who is in a photo
without telling visitors, keeps two people of the same name apart, and lets search find "Onkel Karl" under "Karl Weber".

Kept out on purpose: roles on a link (in the picture, named, photographer); a caption per link ("second from the edge"); a
portrait chosen from the person's photos; family relations; links between a person and a contribution, a contributor, the rights
holder or the credit; people in the feed or in stories as blocks; a person facet in `/search`; places as entities.

## The rule, stated once

**A person is public when a curator chose "Has died" or "May be shown while living", the person is not in the bin, and at least
one item linked to them passes `Visibility.Item(today)`. A public page shows only the person's names marked for visitors, only
the items visitors may see, and the dates of birth and death only for someone who has died. A person who is not public leaves no
trace anywhere a visitor can reach: no name on an item page, no line on `/people`, no search match, no count, no 404 that differs.**

The rule lives in `Data/` only, as for items and stories:

- `Visibility.Person(CommonageDbContext db, DateOnly today)` in `Data/Visibility.cs`: `Disclosure != Hidden`, `DeletedAt == null`
  (belt and braces on the query filter), and `db.ArchiveItems.Where(Item(today)).Any(i => i.Id == link.ArchiveItemId)` over the
  person's links. A method on the context, like `Visibility.Story`, because the item rule cannot be applied to a navigation. It is
  the only place that writes `PersonDisclosure.Deceased` or `MayBeShown` in a query.
- `CommonageDbContext.VisiblePeople` and `BinnedPeople`.
- `Data/VisiblePeople.cs`:
  - `OfItemAsync(db, itemId)`: the public people of one item that visitors already see, with their primary name, for the item page
    and the JSON. It checks `Disclosure` and `DeletedAt` and nothing else, since the item is visible.
  - `PageAsync(db, personId, today)`: the person view for `/people/{id}`: names where `ShownToVisitors`, dates only when
    `Deceased`, the biography, and the items as `ItemSummary` from **one** query
    `db.VisibleItems.Where(i => i.People.Any(l => l.PersonId == id))`, paged.
  - `IndexAsync(db, today)`: names and visible-item counts for `/people`, computed from visible rows only.
- Public code never follows `ItemPerson.Person` or `Person.Links`. The curator preview calls the same `PageAsync`, so it shows what
  a visitor will see (with a banner, also for a hidden person).

## Data model

Migration `AddPeople` (after `AddStories`). New `Domain/Person.cs`:

| Column | Type | Meaning |
|---|---|---|
| Id | int | Key; the address is `/people/{id}` |
| Name | string, required, max 200 | The name the person is known by, written as a whole. No given and family name fields: name order, single names, patronymics and family-first names all fit |
| SortName | string?, max 200 | How to file it ("Weber, Karl"); empty means `Name`. Never shown on its own |
| Disclosure | `PersonDisclosure` (Hidden, Deceased, MayBeShown), string | Who may see this person. Default `Hidden` |
| DisclosureChangedAt | DateTime? | When a curator last chose; null for people carried over from tags (part 3), which the review card counts |
| BornText, BornFrom, BornTo | as `DateText`/`DateFrom`/`DateTo` | Read with `DateRange`, so stored days stay language-neutral |
| DiedText, DiedFrom, DiedTo | the same | |
| Biography | string?, max 4000 | Plain text, curator's words, public when the person is |
| CuratorNote | string?, max 4000 | Why they may be shown, where the knowledge came from. Curators only, never public |
| CreatedAt, UpdatedAt | DateTime (UTC) | From `TimeProvider` |
| DeletedAt | DateTime?, private set | Bin; query filter `DeletedAt == null` |
| Version | uint, `IsRowVersion` (xmin) | Concurrency for the person form |

Methods `SetDisclosure(value, now)`, `MoveToBin`, `RestoreFromBin`, `TrySetBorn`, `TrySetDied`, `Touch`. Names are **not** unique:
two people may share one.

`PersonName` (other names): Id; PersonId (cascade); Name (max 200); `FoldedName`, a stored generated column
`lower(commonage_fold("Name"))`, indexed; Kind (`PersonNameKind`: BirthName, MarriedName, Nickname, OtherSpelling, OtherScript,
Other; string; localized only when shown); Language (optional BCP 47 tag, max 35, checked with `CultureInfo`); ShownToVisitors
(bool). Query filter through the person. At most 20 per person. `Person` gets a matching `FoldedName` column too.

`ItemPerson` (the link): ArchiveItemId (cascade), PersonId (cascade), CreatedAt; key `(ArchiveItemId, PersonId)`; index `PersonId`.
Query filter `l => l.Person.DeletedAt == null && l.ArchiveItem.DeletedAt == null`. At most 200 people per item.

`ItemPeopleText` (search, part 2): one row per item, `ArchiveItemId` key (cascade), `Text`, and a stored `SearchVector` with weight
B, the same pattern as `ItemFileText`. It holds the names of the item's **public-by-disclosure** people that are marked for
visitors, and nothing else. A side table, so that linking a person or changing their visibility never saves the item row: its
`Version` changing would make an open item form report a conflict (the rule for photos).

`AuditEntry` gets `int? PersonId` (FK `SetNull`). `AuditEntry` is not exported.

## Public surface (part 2)

| Surface | Change |
|---|---|
| `/people` | `VisiblePeople` from A to Z by `SortName ?? Name` with the culture's comparer and the letters of `TagIndexModel.InitialOf`, each with its visible-item count (`T.Plural`); "No people are named in the archive yet." with 200. The route moves from `TagIndex` (which keeps `/places`) to `Pages/People/Index` |
| `/people/{id:int}` | 404 unless in `VisiblePeople`; the same `NotFound()` for unknown, hidden, binned and all-items-hidden |
| Item page | A "People" line in `_ItemArticle` (where person tags are now): public people as links, in name order. Hidden people leave nothing: no "and 2 others", no empty line |
| Start page | "All people" while any person is public, as today for person tags |
| `/search` | Finds an item by the visitor-marked names of its public people through `ItemPeopleText`, combined with the item's own vector (`||`), so "Karl Mühlenstraße" finds Karl's photo on Mill Street. No person facet |
| `/items/{id}.json`, Dublin Core | `people`: primary names of the item's public people, as `tags` carried person tags until now (allow-list, `PublicItemRecord`) |
| `/sitemap.xml` | `/people` and each public person who **has died**, without `lastmod` |
| Link preview | The person's name and nothing else: no biography, no dates, no item picture or text; the archive logo as on every page |
| `/feed.xml` | Unchanged: people are not in it |
| `/media/...` | Unchanged; cards use the existing thumbnails and checks |

Person page: `<article>`, `<h1>` with the name in `<bdi>` and `lang` of the material language (or the name's own `Language`);
"Also known as" as a list of the visitor-marked names, each with its own `lang`; "Born" and "Died" in label form (`Born: {0}`), only
for someone who has died; the biography as `text-preserve-lines`; the items as `_ItemCard`s, oldest first, 24 a page, with a
plural count from visible items. The page of someone marked "May be shown while living" answers `X-Robots-Tag: noindex`. Works
without JavaScript; logical CSS properties only; no ids, anchors or `data-` attributes derived from links.

## Curator screens

Under `/Admin/People`, `Policies.ManageArchive`, a "People" link in the admin navigation next to Tags.

- **Index** (part 1): a search box matching `FoldedName` of the name and every other name (prefix, folded in the database as the
  search is), a filter by "who may see", and rows with name, a badge for who may see, linked items and how many visitors see
  (`T.Plural`). A "New person" form (name only). 50 a page.
- **Edit** (part 1): name, sort name, born, died (with `data-date-result`), biography and curator note in one form
  (`data-warn-unsaved`, `Version`; a conflict keeps the typed text). **Who may see this person** in a form of its own, three
  choices: "Only curators" (default), "Has died: visitors may see them", "May be living, and may be shown: they agreed, or are
  publicly known". Choosing the third asks first (`data-confirm`) and the page says what visitors will then see. A died date does
  not change the choice, it only offers it. **Other names**: each with kind, language, "Visitors see this name", Remove
  (`data-confirm`), and an Add form. **Items**: the linked items with their status and, when visitors do not see one, why (the
  reasons partial from stories), with Unlink. Preview. Move to bin (part 4).
- **Item edit page** (part 1): a "People" panel. The linked people with Remove; "Add a person" takes a name. Exactly one person
  with that name or another name (folded): linked. Several: a choice of them, shown with their dates and items so two Karl Webers
  can be told apart. None: "Add “{0}” as a new person?", who starts as "Only curators". Without JavaScript this is a second page;
  with it, a `<datalist>` of matches. The handlers save only `ItemPerson` and `ItemPeopleText`, never the item.
- **Merge** (part 3): on Edit, "This is the same person as..." (name or number). The other person keeps their data and gets this
  one's items, name and other names as other names (folded duplicates dropped); this person goes to the bin without links. Their
  biography and note are shown beside the target's before merging, to copy by hand.
- **Move items** (part 4, the split): on Edit, tick linked items and move them to another person or a new one (from one of the
  other names, which moves with them if chosen).
- **Review card** (part 3): on the dashboard, "People shown without a review: N" for people carried over from person tags.
- **Contributors** (part 1): the personal-data search also lists people whose name, other names or curator note match, items in
  the bin and people in the bin included, so a request "what do you hold about me" finds them.

## Privacy rules in detail

1. A new person, an imported person and a person made from the item form start as "Only curators".
2. A hidden person is not on the item page, `/people`, the search, the JSON, Dublin Core, the sitemap, the feed or the link preview,
   and not in a published-only export. Their id answers the same 404 as an unknown one.
3. A public person whose items are all drafts, waiting, rejected, withdrawn, embargoed or in the bin is as hidden as a hidden
   one. A published record-only item counts as visible. Embargo comes from `Visibility.Item` with the injected clock.
4. A name not marked for visitors is never shown, searched or exported in a public copy, also when the person is public. It is
   how a curator keeps a birth name or a former name private.
5. Dates of birth and death are shown only for someone marked as having died. A birth date of a person who may be living stays
   with the curators, whatever else is shown.
6. The curator note is never public and never in an export without personal data.
7. **No path from a person to a contributor.** There is no link between `Person` and `Contribution`, and `VisiblePeople` never
   reads `ContributorName`, `ContributorEmail`, `ShowName`, `RightsHolder`, `ReceivedFrom` or `InternalNotes`. A person page shows
   no "contributed by", no credit, and no list of what someone sent in, even when the contributor and the person are the same
   human being. The guard test forbids `Contribution` under `Pages/People`.
8. Counts, letters and paging on `/people` and the person page come from visible rows only, so a hidden person or hidden item
   changes nothing about them (byte-identical page test).
9. The item card on a person page is the same card everywhere: record-only items have no picture; an item whose contributor did
   not agree to sharing beyond the website shows on the website as usual and nothing of it travels (the preview has no picture).
10. Computed on each request; the sitemap may lag up to ten minutes (already accepted).
11. What curators write elsewhere (descriptions, credits, captions, story text, the biography of another person) is not checked
    and can name a hidden person. The person rule governs the person record only. The handbook says so.
12. A visitor's note ("Do you know more?") that names someone stays a note; nothing turns it into a person.

## Bin, audit, roles

- People go to the bin for 30 days (`Person.MoveToBin`, part 4); their links and other names are hidden with them by the query
  filters and their names leave `ItemPeopleText`. Bin page "People" section (restore, delete for good with confirm, available at
  once for a removal request); `ArchiveBin.DeletePersonAsync` and a purge step with `PersonPurged`. Binned people are not exported.
  An item in the bin keeps its links; deleting an item for good removes them (cascade).
- New `AuditAction` values, at the end: `PersonCreated, PersonChanged, PersonDisclosureChanged, PersonNameAdded, PersonNameRemoved,
  PersonLinked, PersonUnlinked, PeopleMerged, PersonItemsMoved, PersonMovedToBin, PersonRestored, PersonDeleted, PersonPurged,
  PersonTagsConverted`; new `AuditKind.People`; texts in `AuditText.Describe`, kinds in `KindOf`. `AuditLog.Record` takes an
  optional person. **The entry stores the `PersonId` and no name** (`Subject` stays empty for the person; link actions name the
  item's title as now); the log shows the current name through the link, and "A person since deleted" afterwards, so deleting a
  person on request leaves no name in the log. Every handler records before `SaveChanges`.
- Roles: curators and admins everything. Helpers: see the decision; the recommendation is that a helper may link existing people to
  the drafts they describe and add new ones (who start hidden), sees names only, and gets 403 on `/Admin/People` and on every
  choice of who may see.

## Search and names in any script

- Folding is the search's own: `commonage_fold` in the database, then `lower`, after `Normalize()` (NFC) in C#. The same function
  makes `FoldedName` and `ItemPeopleText.SearchVector`, so curators' matching and visitors' search agree. `AccentFolding.TryUseUnaccentAsync`
  must also touch `PersonNames`, `People` and `ItemPeopleTexts` when it switches (the test in `AccentFoldingTests` gets a person).
- Matching is by the beginning of each word (`ToPrefixQuery`), which keeps marks with letters, so names in Devanagari, Thai and
  Arabic stay whole. Names in scripts without spaces match as one word, as everywhere else in the search (known limit, roadmap
  "Languages").
- No transliteration is guessed: "Мария" and "Maria" are the same person only when a curator adds the other script as another name.
- Sorting uses the request culture's comparer, as `/places` does; `SortName` lets a curator file a name the way the archive's
  readers expect. Names are written whole; the UI never builds "family, given" from parts.
- In lists and sentences each name is wrapped in `<bdi>`, so an Arabic or Hebrew name in a German page, or the other way round,
  keeps its order; the RTL browser run covers `/people` and a person page with a name in another direction.

## Export and import

- `ArchiveExporter.Version` 9 to 10 (part 1); `archive.json` gets `"people"` (streamed, after `stories`) with `ExportPerson`
  (`Id, Name, SortName, Disclosure, Born, Died, Biography, CuratorNote, CreatedAt, Names`) and `ExportPersonName` (`Name, Kind,
  Language, ShownToVisitors`); each item gets `people`, a list of person ids. `items.csv` gets a `people` column with the primary
  names (formula-safe like every cell).
- **Published-only** (a public copy): `VisiblePeople` only, with `Today` captured once for items and people; only names marked
  for visitors; born and died only for someone who has died; no `CuratorNote`, no `Disclosure` beyond what is implied; item
  `people` lists only people in the copy. A test checks no item names a person missing from `people`.
- **Full, without personal data**: leaves out hidden people and their links, every curator note, and birth dates of people who
  may be living. **Full, with personal data**: everyone not in the bin, with everything.
- `ExportTests` field list gets both types. **The import reads people** (part 1 for version 10, part 3 for person tags of
  versions 4 to 9): names, other names, dates, biography. `Disclosure` and `CuratorNote` are `Dropped` (who may be shown is this
  archive's decision; the note is the other archive's curators' words, like `InternalNotes`), so every imported person arrives as
  "Only curators", as every item arrives as a draft. A person of the export is linked to an existing one when exactly one person
  here has the same folded name; otherwise a new one is made; the dry run lists both. Caps: 50,000 people, 20 other names each,
  200 people per item. A person-kind tag in an older export becomes a hidden person the same way.
- `docs/operations.md` format section and the export README name people and what each kind of export holds of them.

## Languages

Only curator content is stored (names, sort name, dates as written, biography, note). The disclosure choices, name kinds, "Born",
"Died", "Also known as", the reasons and messages are localized when shown. Every new text is written out in `T[...]` or
`T.Plural(...)` (ICU), has a German entry, and the base file is rewritten. No sentence is built from a name and words: "Add “{0}”
as a new person?" is one text with a placeholder. Dates are read by `DateRange` in the archive's languages.

## Tests

1. `PersonVisibilityMatrixTests` (seeding copied from `StoryVisibilityMatrixTests`): a public person A with a visible item, and a
   person B (own name, another name, biography, dates and note) in each state: hidden, deceased, may be shown, in the bin, public
   but with only a draft / waiting / rejected / withdrawn / embargoed / binned item, and public with a record-only item. On
   `/people`, `/people/{B}`, `/`, the item page, `/search` for each of B's names, `/items/{id}.json`, `/sitemap.xml`, `/feed.xml`
   and the link preview: hidden states show none of B's names, id or text, `/people` is byte-identical with and without B, and the
   item page equals the page without the link. A name not marked for visitors is never found or shown; birth dates show only for
   deceased; "may be shown" answers `noindex` and is not in the sitemap.
2. Hidden, binned, all-items-hidden and unknown ids give an identical 404 (body and headers).
3. Change over time with the clock moved: embargo opens, disclosure changes both ways, a link is added and removed, the item is
   unpublished, binned, restored and deleted for good; search follows each step.
4. `VisibilityGuardTests`: forbid `PersonDisclosure.`, `db.People`, `db.PersonNames`, `db.ItemPeople`, `.Links` and `Contribution`
   in `Pages/People`, and `\.Person\b` in public pages.
5. `EndpointAccessTests` lists `/people/{id:int}`.
6. `HelperTests`: 403 on every People page and the disclosure handler; linking on a draft as decided.
7. `PersonEditTests`: create, edit, dates, other names (limit, language tag check), the three choices with audit, linking by name
   (one match, several, none, the 200 limit), unlinking, a handler of another person gives 404, the `Version` conflict, linking
   leaves the item's `Version` unchanged, an audit entry per handler with no name in `Subject`.
8. `PersonMergeTests` (part 3) and the move of items (part 4): links deduplicated, names carried, the source in the bin.
9. `PersonTagConversionTests` (part 3): a database migrated to `AddPeople` with person tags (in the bin too, two tags for one item,
   a name that is also a topic tag elsewhere) is migrated on; the people exist with `DisclosureChangedAt` null, the links are
   there, the tags and `TagNames` lose them, the search still finds the names, and what visitors see is unchanged except the tag
   links.
10. Bin tests (part 4): bin, restore, delete, purge after 30 days, the search forgets and remembers.
11. Audit tests: every action has a kind and a text; the Activity filter offers "People"; the log shows a deleted person without a name.
12. Export tests: field lists, the three kinds of export as above, version 10; import of version 10 and of a version-9 export with
    person tags arrives hidden; dry run lines for matched and new people.
13. `PersonalDataTests`: Contributors finds a person by another name and by the note, also in the bin; nothing of a contribution
    appears on a person page when the contributor's name equals the person's.
14. `AccentFoldingTests`: a person found with and without accents in both folding versions, in Latin, Cyrillic, Greek and Arabic.
15. Browser tests: the add-a-person flow with and without JavaScript, the confirm on "may be shown", leaving the person form warns.
16. `AccessibilityTests`: `/people` (already listed) and `/people/1`; Index, Edit, Preview; `/people/1` in the RTL and long-text runs.
17. Demo: Karl becomes a person who has died with a German other name, and a second person in a photo stays "Only curators"; a
    browser test checks that person's name is absent publicly and present under `/Admin`. `ScaleTests` add 5,000 people.

## Parts

| Part | Content | Behaviour change |
|---|---|---|
| 1. People for curators | Model, migration `AddPeople` (with `AuditEntry.PersonId`), `Visibility.Person`, `VisiblePeople`, `/Admin/People` Index, Edit, Preview, the item page's People panel, Contributors, audit, export version 10 and the import of people, demo, tests 4, 6, 7, 11, 12, 13, 15 and the curator half of 16, German entries, CHANGELOG | None for visitors; person tags work as before |
| 2. People for visitors | `/people` and `/people/{id}`, the item page line, `ItemPeopleText` and search, JSON and Dublin Core, sitemap, link preview, start page, `AccentFolding`; tests 1, 2, 3, 5, 14 and the public half of 16; security.md rows; CHANGELOG | Public people become visible. The `privacy-reviewer` must pass this part before merging |
| 3. Person tags become people | Migration `PersonTagsToPeople` (SQL, one transaction: people from person tags as "May be shown" with `DisclosureChangedAt` null, links, tags and `TagNames` cleaned, `ItemPeopleText` filled), the Tags page without the Person kind (the enum value stays, it is stored), older exports' person tags, the review card, merge; tests 8 (merge) and 9; operations.md and release notes | Visitors see the same names, now as people; `/search?tag=Karl` for a former person tag finds nothing |
| 4. Bin, split, handbook | Person bin and purge, move items to another or a new person, handbook in English and German, roadmap, test 8 (move) and 10, `/walkthrough` | Curators can bin people |

## Security rows (docs/security.md)

People (curators only, helpers as decided; three choices of who may see, default "Only curators"; names, dates and text limited
on the server; linking by name, never by path; block-style handlers scoped to their person and never saving the item; the log
keeps no name), people on the website (`Visibility.Person` and `VisiblePeople`; a hidden person answers the same 404 and leaves
nothing on item pages, lists, counts or search; names not marked for visitors and birth dates of people who may be living never
shown; no path to contributors), people beyond the website (preview name only; `noindex` and no sitemap for people who may be
living; not in the feed; JSON carries public names), people in an export (what each kind holds; the import never carries who may
be shown). Known limits: curator text can name a hidden person; a photo itself shows a face whatever the record says; person ids
are sequential; the sitemap may lag ten minutes; a name on an item page of someone shown while living can be indexed with that
page even though the person page is `noindex`.

## Risks

- `ItemPeopleText` going stale (a disclosure change, a name marked private, the bin) is the likeliest leak: one method,
  `PersonSearchText.RefreshAsync(db, personId)`, is called by every handler that changes what visitors may see, and the matrix
  test searches after each change. It updates items in batches of 500, for a person with thousands of items.
- The conversion in part 3 is one-way. It runs in a migration, inside a transaction, and is tested from a real `AddPeople`
  database (`IMigrator.MigrateAsync` to the earlier migration, seed, migrate on). The `update-from` CI jobs run it on real
  0.x archives. Backups before the update are already part of `update.sh`.
- Matching by name when linking or importing can join two different people. The choice page shows dates and items; merge and move
  repair it; an import only matches a single exact folded name.
- Living people carried over from tags are shown as before but were never reviewed; the review card and release notes say so.
- A later page reading `item.People.Select(l => l.Person...)` would skip the rule: stopped by the guard test and the "page without
  the link" matrix check.
- Counting visible items per person over 50,000 items is like the tag counts on `/places`; measure in `ScaleTests` before adding
  any cache, since a cache would keep a hidden person listed.

## Decisions

Open, each with the recommended option first:

1. **Model.** *Recommended:* people as their own entity with links to items, replacing person tags. *Instead:* keep person tags and
   add an optional person record grouping them (smaller, but recording who is in a photo would still publish the name).
2. **Who may see.** *Recommended:* one choice per person, "Only curators" (default) / "Has died" / "May be living, may be shown",
   public only with a visible item. *Instead:* a separate "deceased" fact and a "show" switch (more combinations to explain).
3. **Person tags on upgrade.** *Recommended:* become people shown as before, marked unreviewed, with a dashboard card, so an update
   does not change what visitors see. *Instead:* become hidden people until a curator reviews them (safer, but names vanish from a
   running archive's public pages at update).
4. **A public person without a visible item** answers 404, like a story whose blocks are all hidden. *Recommended:* yes.
5. **Dates.** *Recommended:* born and died public only for someone who has died; never a birth date of someone who may be living.
6. **Other names.** *Recommended:* each has "Visitors see this name", on by default, so a curator can keep a birth or former name
   private. *Instead:* off by default (safer for former names, more clicks for the common case).
7. **Search.** *Recommended:* visitors find items by the visitor-marked names of public people; no person facet in M30.
8. **Beyond the website.** *Recommended:* link preview has the name only; people who may be living get `noindex` and stay out of
   the sitemap; people who died are in it; nobody is in the feed.
9. **JSON and Dublin Core.** *Recommended:* `people` with the public names, as person tags are today. *Instead:* only people who
   died (stricter than the item page itself).
10. **Contributors.** *Recommended:* no link between a person and a contribution, contributor, credit or rights holder in M30;
    the personal-data search finds people.
11. **Link kinds.** *Recommended:* one kind of link (appears in or is named in), no roles and no caption per link in M30.
12. **Helpers.** *Recommended:* may link existing people to their drafts and add new hidden ones, see names only, and never choose
    who may see. *Instead:* nothing of people for helpers.
13. **Activity log.** *Recommended:* stores the person's id, never their name, so a deletion on request leaves no name behind.
14. **Merge and split.** *Recommended:* merge keeps the target's data and adds the other's names; the other goes to the bin without
    links. Split is "move these items to another or a new person".
15. **Export.** *Recommended:* published-only holds public people only, visitor-marked names, dates only for people who died;
    without personal data leaves out hidden people, notes and birth dates of people who may be living; version 10.
16. **Import.** *Recommended:* people are read, always arrive as "Only curators", the note is not read, and a person is matched
    only by a single exact folded name.
17. **Same names.** *Recommended:* allowed; two people may share a name, unlike tags.
18. **Out of M30.** *Recommended:* no portrait, no relations, no people in the feed or as story blocks; each can follow when asked.
