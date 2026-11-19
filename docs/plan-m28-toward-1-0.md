# Plan for M28: toward 1.0

Status: **plan, no code yet.** Three parts, in this order, each its own pull request. The plan goes to the `privacy-reviewer` agent
before part 1 starts, as M27 did, because part 1 brings material into the archive from outside and part 2 adds public addresses.

## What is wanted

1. **Commonage reads its own export**, so two archives can be merged and a group can move into a fresh installation. It uses
   the import's dry run and changes nothing until a curator confirms.
2. **Dublin Core and a JSON alternate** for every public item, so other tools can read a record without scraping the page.
3. **A 1.0 checklist** that is written down and checked, not remembered.

Not planned: reading another system's export (Omeka, Mukurtu, ...); OAI-PMH (it stays in "Later"); restoring users, settings,
the activity log or visitor notes from an export (a backup does that, see `operations.md`).

## Part 1: reading Commonage's own export

The input is the ZIP the export writes: `archive.json` with `"format": "commonage-export"` and the files beside it. Versions 4
and later are read (version 4 is the first with tags). Anything else, or a file that is not a Commonage export, is refused with a
plain message that names the version found and the versions understood.

**What arrives, and as what**

- Every item arrives as a **draft**, whatever it was in the source. Reading an export never makes anything public by itself
  (rule 1). Its `mediaLevel` and `publicFrom` are kept, so a curator who held something back in one archive does not lose that
  choice by moving it; it is shown in the dry run so it is not a surprise.
- Collections are matched by title and otherwise created. Tags are matched by name and kind and otherwise created. The date is
  taken as the text written (`date.text`) and read again by the same parser, so a date in a vocabulary this version lacks is
  reported in the dry run, not guessed.
- Files are taken from the ZIP by the path `archive.json` names, and are stored through `MediaUploadService`, the same path as an
  upload, so size limits, decoding and derivatives are never skipped. The checksum in the export is checked against the file; a
  mismatch is reported and the file is left out.
- Alt text, caption, position, rotation, rights URI, credit, description, `needsTranscription` and `helpWanted` come with the item.
  A table of every property of the export's item, file, collection, tag and settings records, each marked read, dropped or
  dropped-because, is part of the code, and a reflection test fails when the exporter gains a field the table lacks.

**What does not arrive, and why**

- **Contributor details and consent text** (name, email, reference code, consent). Consent was given to the other archive for
  that archive; carrying it over would claim a consent this archive never collected (rule 3). The item keeps its credit line and
  rights statement as text. Left out of the dry run's result with that reason, and said in the handbook.
- Visitor notes, activity log, users, settings, the texts of the pages, the bin, revisions, `internalNotes`, `receivedFrom` and
  the other catalogue fields that name a donor. The `notes` and `contribution` parts of the JSON are never deserialised into
  storable objects, never logged and never shown.
- The status, the published date and the ids (the same reason as the CSV import: no id or status column on purpose).

**Merging and repeating.** Duplicates are found as the CSV import finds them: by (collection, title, date) and by the content of a
file, so a second run adds nothing. This needs one decision (see "Decisions"): what to do with an item that is a duplicate but
whose description differs. Proposed: skip it and list it, never overwrite (rule 4's spirit: nothing is lost at once).

**Safety.** The ZIP is from outside, so it is handled as a contribution is: a size and entry-count limit, a name inside the ZIP is
only a key and never a path, a ZIP made to expand without end is stopped, and the JSON is read as a stream with a depth limit and
a cap on the number of items per run. It waits in `import-staging/` and is removed after the run or a day. One import at a time,
as now. It needs a curator, and is listed under `Administer` like the export. Every import is in the activity log with how many
items and files came in, and names no contributor.

**Tests.** Export, import into an empty database and export again: the two `archive.json` agree except for the fields listed above
(a round trip test, which also catches a field added to the export and forgotten here). A version 4 file, a file with a wrong
checksum, a ZIP with `../` names, a file with a future version, a second run, and a draft with `mediaLevel` and `publicFrom`.
Against the export test that forces a decision for each new field: the importer gets the same table.

## Part 2: Dublin Core and a JSON alternate

- `/items/{id}.json` and, on the item page, `<link rel="alternate" type="application/json">` and `<meta name="DC.*">` tags
  (`DC.title`, `DC.description`, `DC.date`, `DC.creator` only when the credit is public as it is on the page, `DC.rights` from the
  rights URI, `DC.language`, `DC.subject` from tags, `DC.identifier` the page address, `DC.type`).
- The JSON has the same fields as the page shows, nothing more: no ids of private things, no contributor details, no original file
  names, no personal data. Files are listed with their public `/media` addresses **only when the item's media are visible**; a
  record-only item lists none.
- Both start from `VisibleItems` and `VisibleMedia`. A draft, an embargoed or a binned item answers 404, exactly like its page, so
  the address cannot be used to find out that something exists. The JSON address goes into `VisibilityMatrixTests.Addresses`
  and `EndpointAccessTests` on purpose, and the guard test covers the new file.
- Cached for a short time only (ten minutes at most, as the feed), so an item taken down does not stay in a cache for long. Said
  in `security.md`.
- Dates keep their meaning: the text as written plus the earliest and latest day, as the export does, because "about 1950"
  must not become a day.

## Part 3: the 1.0 checklist

A page `docs/release-1.0.md` that lists each condition with its evidence, and a test or script where a condition can be
checked by a machine:

| Condition | How it is checked |
|---|---|
| A real group has run it for a season | Written note from the group (their name only if they agree); not something a test can show |
| The upgrade works from every 0.x minor | `scripts/` upgrade test run against each earlier minor in CI, restoring a backup made by that version |
| Export and import round trip | Part 1's test |
| A support policy | `SUPPORT.md`: which versions get security fixes, for how long, how to report |
| A second person can release | Written handover or second approver, as `handover.md` starts |
| No open `LEAK` from the privacy review, no known accepted limit missing from `security.md` | The last review, dated |
| Accessibility and translations | Browser tests green, no untranslated text in the resource test |

Nothing in this part changes behaviour, so it comes last and is the only part that needs the user and the group, not code.

## Decisions to make

1. **Duplicate with a different description:** skip and list (proposed), or offer to add the other text as a revision.
2. **Contributor details:** left out (proposed), or optional behind a checkbox saying the curator holds that consent.
3. **`publicFrom` and `mediaLevel` kept on import** (proposed) or reset, so everything arrives plain.
4. **Is "a real group running it" a condition for 1.0**, or only for calling it supported?

## Changes after the review

The review found no leak in the plan as such, but fourteen gaps. What they change:

**Part 1**

- **Sharing consent is not dropped silently.** An item without a contribution always travels (previews, feed, and in part 2 the
  JSON). So an item whose source says `consentCoversSharing: false` is **not imported**: it is listed in the dry run with that
  reason. Storing the restriction would need a new field and a migration; it waits until a group asks. Test: such an item is
  absent after import.
- **Status.** `Rejected` and `Withdrawn` items are skipped and listed, so a withdrawal is not undone. The importer has no
  path that writes `Published` or `PublicFrom` beyond copying a validated future-or-past date; a source-file test checks it.
- **Insert only.** Nothing existing is updated, removed or merged, no `Remove` or `ExecuteDelete`. An item that sits in the bin is
  not seen by the duplicate check; this is stated in the handbook and tested.
- **Unknown values are issues, not exceptions.** An unknown `status`, `mediaLevel` or tag `kind` is one line in the dry run for that
  item. A `publicFrom` must be a real date.
- **Keys.** Titles, tags and collections are compared in NFC with an ordinal, culture-free comparison, so a Mac-written (NFD)
  title and a server in a Turkish culture do not make duplicates. Items with files are matched by the checksum first; items
  without files can be duplicated by a changed title, and the handbook says so. `coverFileId`, collection position and sort are not
  trusted. Collections created per run are capped, and creating one has its own `AuditAction`.
- **Dates.** The exported `from` and `to` are used when present and sane, `text` stays as the display text, and a mismatch is
  reported, not fatal. Tests with Chinese, right-to-left and Cyrillic titles and a date text the parser cannot read.
- **The ZIP.** `archive.json` is read through a counting stream with an upper limit (256 MB), `MaxDepth` set, and caps on items,
  files per item, tags per item, collections and the length of each field (the limits of `ItemImport`). A total uncompressed
  budget covers all entries, not only each one. The dry run decodes outside the request or within a limit, so it cannot time out
  and leave staging behind. Tests: a highly compressible `archive.json` and an entry whose `Length` lies.
- **File paths** in `archive.json` must match `files/<id>/<n>-...`, be unique per file, and never name `archive.json`, `items.csv`,
  `branding/` or a README. No fallback to matching by bare name. `contentType` and `sizeBytes` are untrusted and the type is
  detected again. Files the export itself marked `missing` or with a bad checksum are skipped and counted.
- **Where.** The page is for admins like the export, listed in `Program.cs`, `EndpointAccessTests` and `AccessibilityTests`, and the
  dry run answers `no-store` because it names other items' titles.
- **Failing midway.** The audit entry is written first and updated at the end; a second run resumes because duplicates are
  skipped. "One import at a time" is a lock released in `finally`.
- **`ImportStaging.DeleteOld`** catches only `IOException` today. If it runs in a background loop, a permission error stops the app.
  Fix and test that before part 1 adds more staging.
- **Tests across versions.** A round trip with `includesPersonalData=true` leaves no contributor name, email or consent text in
  the database, the activity log, the logs or the dry-run page. Exports of versions 4 to 8 and a published-only export.

**Part 2**

- **Same rules as the preview.** `DC.description`, the file list and the JSON description are left out when the item may not
  travel; the JSON and DC follow the preview rules and not "what the page shows". With search engines off, the JSON answers
  `X-Robots-Tag: noindex` and the DC tags are left out. The JSON address is not in the sitemap.
- **A dedicated record with an allow-list**, never a serialised entity. `DC.creator` is exactly what the page prints. Never
  `OriginalFileName`, `Sha256`, `SizeBytes`, keys, contribution, next or previous links or totals. A test serialises an item that
  has every private field filled and asserts none appears.
- **Hidden means the same answer.** Draft, embargoed, binned, rejected, withdrawn and unknown ids get one identical 404 (same body
  and headers as the page's), never 401 or 403. The `<link rel="alternate">` is rendered only inside the visible result. The 200
  has `Cache-Control: public, max-age=600` and no `ETag` or `Last-Modified` from `UpdatedAt`, which would show edit times made
  during an embargo; the 404 is `no-store`. Test: embargoed until tomorrow is 404 today and 200 tomorrow with the clock moved.

**Part 3**

- Where the dated review output is kept is named, with no contributor details or real archive material in it.
- Every part has its `CHANGELOG.md` entry and `operations.md` update, and `security.md` gets rows for the ZIP import, the public JSON
  and the consent decision.

## Decisions to make (updated)

Decision 1 to 4 above stand. The review adds one: **items whose source did not allow sharing are skipped (proposed)**, or a field
is added to remember it.
