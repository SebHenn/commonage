# Plan for M30: people as entities

Status: **decided on 2026-11-26** (see "Decisions"; the privacy review is in "Changes after the privacy review"). Four parts, in
this order, each its own pull request. **Parts 1 to 3 are not released without part 4**: all four go out in one release, and a
test stops a release made in between (see "Parts"). The `privacy-reviewer` agent reviewed this plan and runs again after each
part. Run `/gate` before each push and `/walkthrough` for the pages. Commit subjects follow `M30, first part: ...`.

## What is wanted

A curator records a **person** once: the name they are known by, other names (a birth name, a married name, a nickname, the name
in another script), when they lived, and a short text. The curator links the person to the items they appear in or are named in.
Visitors read a person page at `/people/{key}` with the person's items, **but only when a curator has said the person has died, or
that they may be shown while living.** Everyone else stays with the curators. Roadmap "Later", item 1.

Today a person is a tag of kind `Person` (M19): a name, unique apart from its case, public on every published item that has it,
on `/people`, in the search (`ArchiveItem.TagNames` is in `SearchVector`), in `/items/{id}.json`, and in both kinds of export.
Recording who is in a photo therefore means publishing the name. The new model lets a curator write down who is in a photo
without telling visitors, keeps two people of the same name apart, and lets search find "Onkel Karl" under "Karl Weber".

Kept out on purpose: roles on a link (in the picture, named, photographer); a caption per link ("second from the edge"); a
portrait chosen from the person's photos; family relations; links between a person and a contribution, a contributor, the rights
holder or the credit; people in the feed or in stories as blocks; a person facet in `/search`; places as entities; anything of
people for helpers.

## The rule, stated once

**A person is public when a curator chose "Has died" or "May be shown while living", the person is not in the bin, and at least
one item linked to them passes `Visibility.Item(today)`. A public page shows only the person's main name and the other names
marked for visitors, only the items visitors may see, and the dates of birth and death only for someone who has died. A person
who is not public leaves no trace anywhere a visitor can reach: no name on an item page, no line on `/people`, no search match, no
count, no 404 that differs.**

The rule lives in `Data/` only, as for items and stories:

- `Visibility.Person(CommonageDbContext db, DateOnly today)` in `Data/Visibility.cs`: `Disclosure != Hidden`, `DeletedAt == null`
  (belt and braces on the query filter), and `db.ArchiveItems.Where(Item(today)).Any(i => i.Id == link.ArchiveItemId)` over the
  person's links. A method on the context, like `Visibility.Story`, because the item rule cannot be applied to a navigation.
  **Outside `Pages/Admin`, `Data/Visibility.cs` is the only file that names `PersonDisclosure`** (besides `Domain/Person.cs`,
  which defines it); the export, the item page and the search ask `Data/VisiblePeople.cs`, which asks `Visibility`.
- `CommonageDbContext.VisiblePeople` and `BinnedPeople`.
- `Data/VisiblePeople.cs`, all returning records made for the public (`PersonCard`, `PersonView`) that have **no** field for
  `CuratorNote`, `DisclosureChangedAt`, `SortName` or the internal `Id`:
  - `OfItemAsync(db, itemId, today)`: the public people of one item visitors already see, for the item page.
  - `DeceasedOfItemAsync(db, itemId, today)`: only those who have died, for the JSON and Dublin Core.
  - `PageAsync(db, publicId, today)`: the person view: main name, other names where `ShownToVisitors`, dates only when
    `Deceased`, the biography, and the items as `ItemSummary` from **one** query
    `db.VisibleItems.Where(i => i.People.Any(l => l.PersonId == id))`, paged.
  - `IndexAsync(db, today)`: names and visible-item counts for `/people`, computed from visible rows only.
  - `SearchNames(db, today)`: the queryable of public names used by the search (below).
- Public code never follows `ItemPerson.Person` or `Person.Links`. The curator preview calls the same `PageAsync`, so it shows what
  a visitor will see (with a banner, also for a hidden person).

## Data model

Migration `AddPeople` (after `AddStories`). New `Domain/Person.cs`:

| Column | Type | Meaning |
|---|---|---|
| Id | int | Key; used by the curator pages and the export, never in a public address |
| PublicId | string, 12 characters, unique index | The public address `/people/{PublicId}`: 60 random bits from `RandomNumberGenerator`, written in lower-case Crockford base32 (no `i`, `l`, `o`, `u`). Made once when the person is created, never changed, never reused |
| Name | string, required, max 200 | The name the person is known by, written as a whole. No given and family name fields: name order, single names, patronymics and family-first names all fit |
| FoldedName | stored generated column `lower(commonage_fold("Name"))`, indexed | Matching by name for curators and the search |
| SortName | string?, max 200 | How to file it ("Weber, Karl"); empty means `Name`. Never shown on its own |
| Disclosure | `PersonDisclosure` (Hidden, Deceased, MayBeShown), string | Who may see this person. Default `Hidden` |
| DisclosureChangedAt | DateTime? | When a curator last chose; null for people carried over from tags (part 3), whom the review list shows |
| BornText, BornFrom, BornTo | as `DateText`/`DateFrom`/`DateTo` | Read with `DateRange`, so stored days stay language-neutral |
| DiedText, DiedFrom, DiedTo | the same | |
| Biography | string?, max 4000 | Plain text, curator's words, public when the person is |
| CuratorNote | string?, max 4000 | Why they may be shown, where the knowledge came from. Curators only, never public |
| CreatedAt, UpdatedAt | DateTime (UTC) | From `TimeProvider` |
| DeletedAt | DateTime?, private set | Bin; query filter `DeletedAt == null` |
| Version | uint, `IsRowVersion` (xmin) | Concurrency for the person form |

Methods `SetDisclosure(value, now)`, `MoveToBin`, `RestoreFromBin`, `TrySetBorn`, `TrySetDied`, `Touch`; the constructor makes the
`PublicId` and leaves `Disclosure` at `Hidden`, and nothing else sets it except `SetDisclosure`. Names are **not** unique: two
people may share one.

`PersonName` (other names): Id; PersonId (cascade); Name (max 200); `FoldedName` as above, indexed; Kind (`PersonNameKind`:
BirthName, MarriedName, Nickname, OtherSpelling, OtherScript, Other; string; localized only when shown); Language (optional BCP 47
tag, max 35, checked with `CultureInfo`); **ShownToVisitors (bool, default false)**. Query filter through the person. At most 20
per person.

`ItemPerson` (the link): ArchiveItemId (cascade), PersonId (cascade), CreatedAt; key `(ArchiveItemId, PersonId)`; index `PersonId`.
Query filter `l => l.Person.DeletedAt == null && l.ArchiveItem.DeletedAt == null`. At most 200 people per item. Linking and
unlinking save only `ItemPerson` rows, never the item: its `Version` changing would make an open item form report a conflict (the
rule for photos).

There is **no stored search text** for people: the search reads the names at query time (below), so it cannot go stale.

`AuditEntry` gets `int? PersonId` (FK `SetNull`). `AuditEntry` is not exported.

## Public surface (part 2)

| Surface | Change |
|---|---|
| `/people` | `VisiblePeople` from A to Z by `SortName ?? Name` with the culture's comparer and the letters of `TagIndexModel.InitialOf`, each with its visible-item count (`T.Plural`); "No people are named in the archive yet." with 200. The route moves from `TagIndex` (which keeps `/places`) to `Pages/People/Index` |
| `/people/{key}` | The route takes any string. A key that is not 12 base32 characters answers `NotFound()` without a query; otherwise 404 unless in `VisiblePeople`. Unknown, malformed, hidden, binned and all-items-hidden give the **same** response |
| Item page | A "People" line in `_ItemArticle` (where person tags are now): public people as links to their `PublicId`, in name order. Hidden people leave nothing: no "and 2 others", no empty line |
| Start page | "All people" while any person is public, as today for person tags |
| `/search` | Finds an item by the main name and visitor-marked names of its public people, computed in the query (below). No person facet |
| `/items/{id}.json`, Dublin Core | `people`: main names of the item's public people **who have died** (allow-list, `PublicItemRecord`). People shown while living are on the item page only |
| `/sitemap.xml` | `/people` and each public person who **has died**, by `PublicId`, without `lastmod` |
| Link preview | Has died: the person's name and nothing else. May be shown while living: the archive's title, no person name. Never a biography, a date, an item picture or text; the archive logo as on every page |
| `/feed.xml` | Unchanged: people are not in it |
| `/media/...` | Unchanged; cards use the existing thumbnails and checks |

Person page: `<article>`, `<h1>` with the name in `<bdi>` and `lang` of the material language (or the name's own `Language`);
"Also known as" as a list of the visitor-marked names, each with its own `lang`; "Born" and "Died" in label form (`Born: {0}`), only
for someone who has died; the biography as `text-preserve-lines`; the items as `_ItemCard`s, oldest first, 24 a page, with a
plural count from visible items. The page of someone marked "May be shown while living" sends **both** `X-Robots-Tag: noindex`
and `<meta name="robots" content="noindex">`. Works without JavaScript; logical CSS properties only; no ids, anchors or `data-`
attributes derived from links.

## Search and names in any script

- **Computed at query time.** `ItemSearch` adds the item's public names to its own vector inside the query:
  `i.SearchVector || to_tsvector('simple', string_agg(name.FoldedName, ' '))` over `VisiblePeople.SearchNames` for that item
  (main names of public people and their names marked for visitors; Npgsql translates `string.Join` to `string_agg`). Since the
  item already passes `VisibleItems`, the item part of the rule holds, and the names come from the person rows as they are now: a
  disclosure change, a name marked private or the bin is seen by the next search. There is no refresh to forget. Names weigh as
  tags do (B), so "Karl Mühlenstraße" finds Karl's photo on Mill Street.
- `ScaleTests` measure this with 50,000 items, 5,000 people and 20,000 links against the search's existing budget. If it does not
  fit, the fallback is two steps (the ids of public people whose names match, then items linked to them, ORed in), with the known
  limit that words split between a name and the item text no longer combine. A stored copy is not the fallback.
- Folding is the search's own: `commonage_fold` in the database, then `lower`, after `Normalize()` (NFC) in C#. The same function
  makes every `FoldedName`, so curators' matching and visitors' search agree. `AccentFolding.TryUseUnaccentAsync` must also touch
  `People` and `PersonNames` when it switches, so their generated columns are made again.
- Matching is by the beginning of each word (`ToPrefixQuery`), which keeps marks with letters, so names in Devanagari, Thai and
  Arabic stay whole and a combining mark is not split from its letter. Names in scripts without spaces match as one word, as
  everywhere else in the search (known limit, roadmap "Languages").
- No transliteration is guessed: "Мария" and "Maria" are the same person only when a curator adds the other script as another name.
- Sorting uses the request culture's comparer, as `/places` does; `SortName` lets a curator file a name the way the archive's
  readers expect. Names are written whole; the UI never builds "family, given" from parts.
- In lists and sentences each name is wrapped in `<bdi>`, so an Arabic or Hebrew name in a German page, or the other way round,
  keeps its order; the RTL browser run covers `/people` and a person page with a name in another direction.

## Curator screens

Under `/Admin/People`, `Policies.ManageArchive` (curators and admins; helpers get 403), a "People" link in the admin navigation
next to Tags, not shown to helpers.

- **Index** (part 1): a search box matching `FoldedName` of the name and every other name (prefix, folded in the database as the
  search is), a filter by "who may see", and rows with name, a badge for who may see, linked items and how many visitors see
  (`T.Plural`). A "New person" form (name only). 50 a page. Until part 3 has run, a person whose folded name equals a `Person`-kind
  tag's shows **"A public tag with this name still exists"** beside them (Index, Edit, and the item page's People panel), so a
  curator does not think hiding the person hides the tag.
- **Edit** (part 1): name, sort name, born, died (with `data-date-result`), biography and curator note in one form
  (`data-warn-unsaved`, `Version`; a conflict keeps the typed text). **Who may see this person** in a form of its own, three
  choices: "Only curators" (default), "Has died: visitors may see them", "May be living, and may be shown: they agreed, or are
  publicly known". Choosing either public choice asks first (`data-confirm`, and a confirm page without JavaScript), and the
  question names what becomes public: the name, the other names marked for visitors, **the biography**, the items visitors can
  see, and for "Has died" also the dates. A died date does not change the choice, it only offers it. **Other names**: each with
  kind, language, "Visitors see this name" (off when added), Remove (`data-confirm`), and an Add form. **Items**: the linked items
  with their status and, when visitors do not see one, why (the reasons partial from stories), with Unlink. Preview (by `Id`).
  Move to bin (part 4).
- **Item edit page** (part 1, not shown to helpers): a "People" panel. The linked people with Remove; "Add a person" takes a name.
  Exactly one person with that name or another name (folded): linked. Several: a choice of them, shown with their dates and items
  so two Karl Webers can be told apart. None: "Add “{0}” as a new person?", who starts as "Only curators". Without JavaScript this
  is a second page; with it, a `<datalist>` of matches. The handlers save only `ItemPerson`, never the item.
- **Merge** (part 3): on Edit, "This is the same person as..." (name or number). The target keeps its data and gets this one's
  items; this one's name and other names become the target's other names (folded duplicates dropped), **all with "Visitors see
  this name" off**. The target's choice of who may see becomes the **stricter** of the two (Only curators, then May be shown while
  living, then Has died), and the confirm page says so in a visible warning before anything happens ("Visitors will no longer see
  ..." when that removes the target or its items from public pages). It warns too when the target is "Only curators" (the merged
  items' person disappears for visitors) or in the bin (the result stays in the bin with all links). This person goes to the bin
  without links. Both biographies and notes are shown side by side, to copy by hand.
- **Move items** (part 4, the split): on Edit, tick linked items and move them to another person or a **new person, who starts as
  "Only curators"**. Other names carried along start with "Visitors see this name" off. Moving to an existing person warns when
  that person's choice shows the items' person to visitors where this one did not, or hides them.
- **Review list** (part 3): a dashboard card "People from former tags: N shown without review, M kept hidden" (`T.Plural` each),
  linking to `/Admin/People?review=1`: the carried-over people (`DisclosureChangedAt` null) with their items, checkboxes, and bulk
  "Only curators" and "Has died" (one audit entry per person). The card goes when the list is empty.
- **Contributors** (part 1): the personal-data search also lists people whose name, other names or curator note match, items in
  the bin and people in the bin included, so a request "what do you hold about me" finds them.

## Privacy rules in detail

1. A new person, an imported person, a person made from the item form and a person made by moving items start as "Only curators".
2. A hidden person is not on the item page, `/people`, the search, the JSON, Dublin Core, the sitemap, the feed or the link preview,
   and not in a published-only export. Their key answers the same 404 as an unknown or malformed one. Public addresses use the
   random `PublicId`, so trying numbers finds nothing.
3. A public person whose items are all drafts, waiting, rejected, withdrawn, embargoed or in the bin is as hidden as a hidden
   one. A published record-only item counts as visible. Embargo comes from `Visibility.Item` with the injected clock.
4. Another name is shown, searched or exported in a public copy only when a curator ticked "Visitors see this name"; it is off for
   every new, merged, moved and imported name, without exception. A birth name or a former name stays private unless chosen.
5. Dates of birth and death are shown only for someone marked as having died. A birth date of a person who may be living stays
   with the curators, whatever else is shown.
6. People shown while living are on the website only: not in the sitemap, `noindex` on their page, no name in the link preview,
   not in the JSON, Dublin Core or feed.
7. The curator note is never public and never in an export without personal data. `PersonView` and `PersonCard` cannot carry it.
8. **No path from a person to a contributor.** There is no link between `Person` and `Contribution`, and `VisiblePeople` never
   reads `ContributorName`, `ContributorEmail`, `ShowName`, `RightsHolder`, `ReceivedFrom` or `InternalNotes`. A person page shows
   no "contributed by", no credit, and no list of what someone sent in, even when the contributor and the person are the same
   human being.
9. Counts, letters and paging on `/people` and the person page come from visible rows only, so a hidden person or hidden item
   changes nothing about them (byte-identical page test).
10. The item card on a person page is the same card everywhere: record-only items have no picture; an item whose contributor did
    not agree to sharing beyond the website shows on the website as usual and nothing of it travels (the preview has no picture).
11. Computed on each request; only the sitemap may lag up to ten minutes (already accepted).
12. What curators write elsewhere (titles, descriptions, credits, captions, story text, the biography of another person) is not
    checked and can name a hidden person. The person rule governs the person record only. The handbook says so.
13. A visitor's note ("Do you know more?") that names someone stays a note; nothing turns it into a person.
14. Helpers see nothing of people: no People pages, no panel on the item form, no person names on any page they reach.

## Bin, audit, roles

- People go to the bin for 30 days (`Person.MoveToBin`, part 4); their links and other names are hidden with them by the query
  filters, and the search stops finding their names at once since it reads the rows. Bin page "People" section (restore, delete
  for good with confirm, available at once for a removal request); `ArchiveBin.DeletePersonAsync` and a purge step with
  `PersonPurged`. Binned people are not exported. An item in the bin keeps its links; deleting an item for good removes them
  (cascade).
- New `AuditAction` values, at the end: `PersonCreated, PersonChanged, PersonDisclosureChanged, PersonNameAdded, PersonNameRemoved,
  PersonLinked, PersonUnlinked, PeopleMerged, PersonItemsMoved, PersonMovedToBin, PersonRestored, PersonDeleted, PersonPurged,
  PersonTagsConverted`; new `AuditKind.People`; texts in `AuditText.Describe`, kinds in `KindOf`. `AuditLog.Record` takes an
  optional person. **The entry stores the `PersonId` and no name** (`Subject` stays empty for the person; link actions name the
  item's title as now); the log shows the current name through the link, and "A person since deleted" once the `SetNull` has run,
  so deleting a person on request leaves no person name in the log. Every handler records before `SaveChanges`.
- Known limits of the log: an item's title in `Subject` and an item's revisions can contain a person's name, and are kept; the
  handbook's removal-request steps say to check them. Entries of a deleted person stay as "A person since deleted".
- Roles: curators and admins everything. Helpers nothing (decision 12).

## Export and import

- `ArchiveExporter.Version` 9 to 10 (part 1); `archive.json` gets `"people"` (streamed, after `stories`) with `ExportPerson`
  (`Id, PublicId, Name, SortName, Disclosure, Born, Died, Biography, CuratorNote, CreatedAt, Names`) and `ExportPersonName`
  (`Name, Kind, Language, ShownToVisitors`); each item gets `people`, a list of person ids. `items.csv` gets a `people` column
  with the main names, **built from the same filtered set as `people`** (formula-safe like every cell).
- **Published-only** (a public copy): `VisiblePeople` only, with `Today` captured once for items and people; main names and names
  marked for visitors; born and died only for someone who has died; no `CuratorNote`, no `Disclosure`, no `SortName`; item
  `people` lists only people in the copy.
- **Full, without personal data**: leaves out hidden people and their links, **every name not marked for visitors**, every curator
  note, and birth dates of people who may be living. **Full, with personal data**: everyone not in the bin, with everything.
- A test per kind checks that no item and no CSV row names a person missing from that kind's `people`.
- `ExportTests` field list gets both types. **The import reads people** (part 1 for version 10, part 3 for person tags of
  versions 4 to 9): names, other names, dates, biography. `Disclosure`, `ShownToVisitors`, `PublicId` and `CuratorNote` are
  `Dropped` (who may be shown is this archive's decision; the addresses are the other archive's; the note is the other curators'
  words, like `InternalNotes`). **Every imported person is a new person, "Only curators", with every other name off for visitors;
  the import never matches an existing person**, as every item arrives as a draft. The dry run (curators only, like the import)
  counts the people it would create. Caps: 50,000 people, 20 other names each, 200 people per item. A person-kind tag in an older
  export becomes a new hidden person the same way, one per tag name.
- `docs/operations.md` format section and the export README name people and what each kind of export holds of them.

## Languages

Only curator content is stored (names, sort name, dates as written, biography, note). The disclosure choices, name kinds, "Born",
"Died", "Also known as", the reasons, warnings and messages are localized when shown. Every new text is written out in `T[...]` or
`T.Plural(...)` (ICU), has a German entry, and the base file is rewritten. No sentence is built from a name and words: "Add “{0}”
as a new person?" is one text with a placeholder; the review card's two counts are two plural texts. Dates are read by
`DateRange` in the archive's languages.

## Tests

1. `PersonVisibilityMatrixTests` (seeding copied from `StoryVisibilityMatrixTests`): a public person A with a visible item, and a
   person B (own name, another name, biography, dates and note) in each state: hidden, deceased, may be shown, in the bin, public
   but with only a draft / waiting / rejected / withdrawn / embargoed / binned item, public with a record-only item, and **hidden
   with two visible items**. On `/people`, `/people/{B}`, `/`, the item page and the link preview: hidden states show none of B's
   names, key or text; `/people` and the item page are byte-identical with and without B after normalising only the CSP nonce and
   the antiforgery token. A name not marked for visitors is never shown; birth dates show only for deceased; "may be shown" sends
   the header and the meta tag.
2. `PersonNotFoundTests`: unknown, malformed (too short, too long, wrong letters, an integer id), hidden, binned and
   all-items-hidden keys give the same status, body, `Content-Length`, `Cache-Control`, `Set-Cookie` and other headers.
3. Separately named tests for each channel: `PeopleInSitemap` (only deceased, by `PublicId`), `PeopleNotInFeed`,
   `PeopleInItemJson` and `PeopleInDublinCore` (only deceased), `PeopleInLinkPreview` (name for deceased, archive title for may be
   shown, never a hidden name), `PeopleInSearch` (each of B's names in each state).
4. Change over time with the clock moved: embargo opens, disclosure changes both ways, a name is marked and unmarked, a link is
   added and removed, the item is unpublished, binned, restored and deleted for good; the search follows each step at once.
5. `VisibilityGuardTests`: over **every** `.cs` and `.cshtml` file of the web project outside `Data/` and `Pages/Admin`, forbid
   `db.People`, `db.PersonNames`, `db.ItemPeople`, `.Links`, `\.Person\b` and `Contribution` in people code; and `PersonDisclosure`
   anywhere outside `Pages/Admin` except `Data/Visibility.cs` and `Domain/Person.cs`.
6. `PublicPersonProjectionTests`: the SQL of `PageAsync`, `IndexAsync`, `OfItemAsync` and the export's published-only query
   (`ToQueryString`) never selects `CuratorNote` or `DisclosureChangedAt`, and the search's names query never selects a name with
   `ShownToVisitors` false.
7. `EndpointAccessTests` lists `/people/{key}`.
8. `HelperTests`: with a hidden person linked to a draft the helper describes, the helper gets 403 on every People page and
   handler, the item form has no People panel, and the person's name is on no page the helper reaches.
9. `PersonEditTests`: create (`PublicId` made, 12 base32 characters, unique), edit, dates, other names (limit, language tag check,
   off by default), the three choices with audit, the confirmation names the biography, linking by name (one match, several,
   none, the 200 limit), unlinking, a handler of another person gives 404, the `Version` conflict, linking leaves the item's
   `Version` unchanged, an audit entry per handler with no name in `Subject`, and the "A public tag with this name still exists"
   note for a matching `Person`-kind tag.
10. `PersonMergeTests` (part 3) and the move of items (part 4): links deduplicated, names carried with visitors off, the stricter
    choice taken with the warning shown, the warnings for a hidden or binned target, a new person from a move is hidden, the
    source in the bin.
11. `PersonTagConversionTests` (part 3), from a database migrated to `AddPeople` (`IMigrator.MigrateAsync` to it, seed, migrate on):
    a person tag only on a draft item becomes hidden; on a published item, "May be shown"; only on a binned item, hidden; on both a
    visible and a draft item, "May be shown"; on an item with `PublicFrom` tomorrow, hidden. All have `DisclosureChangedAt` null,
    links in place (the bin included), the tags and `TagNames` without them, a name that is also a topic tag elsewhere keeps the
    topic, and the dashboard counts match. A test runs the migration's SQL rule and `Visibility.Item(today)` over the same items
    and expects the same set, so the two cannot drift.
12. Bin tests (part 4): bin, restore, delete, purge after 30 days, the search forgets and remembers.
13. Audit tests: every action has a kind and a text; the Activity filter offers "People"; the log shows a deleted person as "A
    person since deleted" without a name.
14. Export tests: field lists, the three kinds of export as above, version 10; the CSV column against `people` per kind; import of
    version 10 and of a version-9 export with person tags creates new hidden people even when one of the same name exists; dry
    run counts; the dry run is 403 for helpers.
15. `PersonalDataTests`: Contributors finds a person by another name and by the note, also in the bin; a person page, `/people` and
    the item page show nothing of a contribution when the contributor's name equals the person's (with `ShowName` both on and off).
16. `AccentFoldingTests`: a person found with and without accents in both folding versions, in Latin, Cyrillic, Greek, Arabic and
    Devanagari, a name with combining marks typed decomposed and composed, and a hidden person with the same folded name as a
    public one: the search finds only the public one's items and `/people` lists one.
17. Browser tests: the add-a-person flow with and without JavaScript, the confirms on both public choices, leaving the person form
    warns, the review list's bulk actions.
18. `AccessibilityTests`: `/people` (already listed) and a person page; Index, Edit, Preview, the review list; a person page in the
    RTL and long-text runs.
19. `ReleaseGuardTests` (part 1, removed in part 4): when the top entry of `CHANGELOG.md` has a version and a date (a release pull
    request), the `PersonTagsToPeople` migration and `ArchiveBin.DeletePersonAsync` must exist. A release with parts 1 to 3 only
    fails the build.
20. Demo: Karl becomes a person who has died with a German other name marked for visitors, and a second person in a photo stays
    "Only curators"; a browser test checks that person's name is absent publicly and present under `/Admin`. `ScaleTests` add
    5,000 people and 20,000 links and time `/people`, a person page and the search.

## Parts

All four parts go out in one release; test 19 makes a release in between fail, and `docs/development.md` "Releasing" gets a line
saying so until part 4 is merged.

| Part | Content | Behaviour change |
|---|---|---|
| 1. People for curators | Model with `PublicId`, migration `AddPeople` (with `AuditEntry.PersonId`), `Visibility.Person`, `VisiblePeople`, `/Admin/People` Index, Edit, Preview, the item page's People panel, the tag-name note, Contributors, audit, export version 10 and the import of people, demo, tests 5, 6, 8, 9, 13, 14, 15, 17 (less the review list), 19 and the curator half of 18, German entries, CHANGELOG | None for visitors; person tags work as before |
| 2. People for visitors | `/people` and `/people/{key}`, the item page line, search at query time, JSON and Dublin Core, sitemap, link preview, start page, `AccentFolding`; tests 1, 2, 3, 4, 7, 16 and the public half of 18, `ScaleTests`; security.md rows; CHANGELOG | Public people become visible. The `privacy-reviewer` must pass this part before merging |
| 3. Person tags become people | Migration `PersonTagsToPeople` (SQL, one transaction, below), the Tags page without the Person kind (the enum value stays, it is stored), older exports' person tags, the review list and card, merge; tests 10 (merge) and 11; operations.md and release notes | Names of people on visible items stay public, now as people; names only on hidden items become hidden people; `/search?tag=Karl` for a former person tag finds nothing |
| 4. Bin, split, handbook | Person bin and purge, move items to another or a new person, handbook in English and German, roadmap, tests 10 (move) and 12, test 19 removed with the "Releasing" line, `/walkthrough` | Curators can bin people |

The conversion in part 3, in one transaction: a person per `Person`-kind tag (name from the tag), a link per item that has the tag
(the bin included), then **"May be shown while living" only for people with at least one linked item that passes the item rule
in SQL** (`"Status"` published as stored, not in the bin, `"PublicFrom" IS NULL OR "PublicFrom" < CURRENT_DATE`: strictly before
today, so a day's difference in time zone keeps the person hidden, never shown); all others "Only curators". `DisclosureChangedAt`
stays null for all, one `PersonTagsConverted` entry, then the person tags leave the items and the tag table and `TagNames` is made
again. The release notes and the card say "N shown without review, M kept hidden".

## Security rows (docs/security.md)

People (curators and admins only, helpers nothing; three choices of who may see, default "Only curators"; other names private
unless ticked; names, dates and text limited on the server; linking by name, never by path; handlers scoped to their person and
never saving the item; the log keeps no person name), people on the website (`Visibility.Person` and `VisiblePeople`; random
`PublicId` addresses; a hidden person answers the same 404 as an unknown or malformed key and leaves nothing on item pages, lists,
counts or search; birth dates of people who may be living never shown; search reads the names at query time; no path to
contributors), people beyond the website (people who died: name in the preview, sitemap, JSON and Dublin Core; people shown while
living: none of these, `noindex` header and meta; nobody in the feed), people in an export (what each kind holds; the import
creates hidden people only and never matches). Known limits: curator text (titles, descriptions, captions, stories, biographies)
can name a hidden person; a photo itself shows a face whatever the record says; item titles in the log and item revisions can
hold names; the sitemap may lag ten minutes; a name on an item page of someone shown while living can be indexed with that page
even though the person page is `noindex`.

## Risks

- The search reading names at query time could be slow on a large archive: `ScaleTests` measure it in part 2, and the fallback
  is another query, never a stored copy that can go stale.
- The conversion in part 3 is one-way. It runs in a migration, inside a transaction, and is tested from a real `AddPeople`
  database; its SQL rule is tested against `Visibility.Item`. The `update-from` CI jobs run it on real 0.x archives. Backups
  before the update are already part of `update.sh`.
- People carried over as "May be shown" were never reviewed; the review list, the card and the release notes say so, and hiding
  them is one bulk action.
- A release of parts 1 to 3 alone would leave public person tags next to hidden people of the same name: test 19 and the
  tag-name note.
- Matching by name when linking can join two different people. The choice page shows dates and items; merge and move repair it;
  the import never matches.
- A later page reading `item.People.Select(l => l.Person...)` would skip the rule: stopped by the guard test over every public file
  and the byte-identical "page without the link" checks.
- Counting visible items per person over 50,000 items is like the tag counts on `/places`; measure in `ScaleTests` before adding
  any cache, since a cache would keep a hidden person listed.

## Changes after the privacy review

The `privacy-reviewer` found no leak in the design and raised twelve concerns. These changes answer them and are part of the plan:

1. **The upgrade could publish people never meant to be public.** The conversion shows a former person tag only when one of its
   items is visible now, evaluated in SQL; the rest become hidden. The review list has bulk actions, and the conversion is tested
   for draft-only, published, binned, mixed and embargoed items.
2. **Other names on by default would publish former names.** "Visitors see this name" is off for every new, merged, moved and
   imported name.
3. **A stored search copy could go stale.** There is no `ItemPeopleText` and no refresh: the search reads the names in the query.
4. **Living people travelled beyond the website.** People shown while living are not in the link preview (archive title), JSON,
   Dublin Core or sitemap, and their page sends `noindex` as both header and meta tag.
5. **Helpers were given names.** Helpers get nothing of people in M30, tested with a hidden person on their draft.
6. **Sequential ids let anyone count and probe people.** Public addresses use a random 12-character `PublicId`; malformed keys
   give the same 404.
7. **Merge and split could widen what is shown.** Merge takes the stricter choice with a visible warning, warns for hidden and
   binned targets, and carries names with visitors off; a split makes a hidden person.
8. **The export without personal data kept private names.** It leaves out names not marked for visitors, hidden people and
   notes, and the CSV column is built from the same set.
9. **Matching on import could attach a stranger's data to a person here.** The import always creates new hidden people.
10. **A release in the middle would mix tags and people.** Parts 1 to 3 do not ship alone (test 19), and curator pages flag a
    person who still has a public tag of the same name.
11. **The log holds names elsewhere.** Item titles in `Subject` and item revisions are named as known limits, and the removal
    steps in the handbook say to check them.
12. **The tests needed to be sharper.** The guard covers every public file and pins `PersonDisclosure` to `Data/Visibility.cs`;
    the matrix has a hidden person with two visible items; byte-identical checks normalise only the nonce and antiforgery token;
    the 404 check compares `Content-Length`, `Set-Cookie` and cache headers; each channel has its own test; the contributor
    name equal to a person name, Devanagari, combining marks and a hidden namesake are tested; the confirmation names the
    biography; the public projections are checked never to select the note or `DisclosureChangedAt`.

## Decisions (accepted 2026-11-26)

Decided by the coordinator, to whom the maintainer (Seb H.) delegated them, after the privacy review:

1. **Model:** people are their own records with links to items, replacing person tags.
2. **Who may see:** one choice per person, "Only curators" (default), "Has died" or "May be living, may be shown"; public only with
   a visible item.
3. **Person tags on upgrade:** a converted person is "May be shown" and unreviewed only when at least one linked item passes the
   item rule (in SQL); all others are hidden. The card and the notes say "N shown without review, M kept hidden"; the review list
   is clickable with bulk "Only curators" and "Has died".
4. **A public person without a visible item** answers 404, like a story whose blocks are all hidden.
5. **Dates:** born and died public only for someone who has died; never a birth date of someone who may be living.
6. **Other names:** "Visitors see this name" is off by default, without exceptions.
7. **Search:** visitors find items by the public names of public people, computed at query time from `VisiblePeople`; no stored
   copy and no refresh; measured in `ScaleTests`; no person facet.
8. **Beyond the website:** link preview has the name for people who died and the archive title for people shown while living;
   their pages send `noindex` as header and meta tag and stay out of the sitemap; people who died are in it; nobody is in the feed.
9. **JSON and Dublin Core:** only people who have died.
10. **Contributors:** no link between a person and a contribution, contributor, credit or rights holder in M30; the personal-data
    search finds people.
11. **Link kinds:** one kind of link, no roles and no caption per link in M30.
12. **Helpers:** nothing of people in M30 (403, not shown), tested with a hidden person present.
13. **Activity log:** stores the person's id, never the name. Known limits: item titles in `Subject` and item revisions can hold
    names; `SetNull` leaves "A person since deleted" entries.
14. **Merge and split:** names arriving by merge or move start hidden from visitors; merge takes the stricter choice with a visible
    warning, and warns when the target is hidden or in the bin; a split makes a hidden person.
15. **Export:** version 10. Published-only holds public people, their main and visitor-marked names, dates only for people who
    died. Without personal data leaves out hidden people, names not marked for visitors, all notes and birth dates of people who
    may be living. `items.csv` is built from the same filtered set.
16. **Import:** always creates new hidden people and never matches an existing one; the dry run is for curators only.
17. **Same names:** allowed; two people may share a name, unlike tags.
18. **Out of M30:** no portrait, no relations, no people in the feed or as story blocks.
19. **Public addresses:** `/people/{PublicId}`, a random 12-character base32 key with a unique index, used by links and the
    sitemap; curator pages use the integer id; unknown and malformed keys give the same 404.
