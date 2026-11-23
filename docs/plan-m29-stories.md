# Plan for M29: stories and exhibitions

Status: **decided (see "Decisions"); part 1 not started.** Three parts, in this order, each its own pull request, all three in
one release. The plan goes to the `privacy-reviewer` agent before part 1 starts, because part 2 adds public addresses that show
items by reference. The agent runs again after each part. Run `/gate` before each push and `/walkthrough` for the pages. Commit
subjects follow `M29, first part: ...`.

## What is wanted

A curator puts together a **story**: a title, a short summary and an ordered list of **blocks**. A block is plain text written by
the curator, or a reference to an item with an optional caption written by the curator. Visitors read it at `/stories/{id}` in
one layout. Roadmap "Later", item 1.

Kept out on purpose: rich text, Markdown or HTML; more than one layout; drag-and-drop (ordering uses "Move earlier / Move
later" buttons, as photos and collections do); choosing which photo of an item a block shows (it uses the item's card picture);
an embargo date for a story; stories in the feed, in search, in JSON or Dublin Core; reading stories when importing an export; a
"Part of the story" link on the item page.

## The rule, stated once

**A story is public when it is published, not in the bin, and has at least one visible block. Its item blocks show only items
that `Visibility.Item(today)` allows, and only what `ItemSummary.FromItem` shows of them (no picture for a record-only item).
A block whose item is hidden is left out without a trace: no placeholder, no caption, no item number, no link, no count.**

The rule lives in `Data/` only, as it does for M27:

- `Visibility.Story(DateOnly today)` in `Data/Visibility.cs`: published, and at least one text block or one item block whose item
  passes the item rule. The item condition is built from `Visibility.Item`, never a second spelling of `ItemStatus.Published`.
  The explicit `DeletedAt == null` is belt and braces on top of the query filter.
- `CommonageDbContext.VisibleStories` and `BinnedStories` (`IgnoreQueryFilters`, `DeletedAt != null`).
- `Data/VisibleStoryBlocks.cs`: `LoadAsync(db, storyId, ct)` returns `StoryBlockView` (`Text` or `Item(ItemSummary, Caption)`).
  It reads the block rows, runs **one** query `db.VisibleItems.Where(i => ids.Contains(i.Id)).Select(ItemSummary.FromItem)` and
  drops every item block whose id is missing. Public code never follows `StoryBlock.ArchiveItem`. Pictures come only through
  `ItemSummary.ThumbnailMediaFileId` (null for record-only), served by the existing `/media/{id}/thumbnail`.
- The curator preview uses the same `LoadAsync`, so the preview shows what a visitor will see.

## Data model

Migration `AddStories` (after `ItemPublicFrom`). New `Domain/Story.cs`:

| Column | Type | Meaning |
|---|---|---|
| Id | int | Key; the address is `/stories/{id}` |
| Title | string, required, max 200 | Plain text |
| Summary | string?, max 1000 | Lead paragraph, list text and link preview description |
| Status | `StoryStatus` (Draft, Published), stored as a string | Not `ItemStatus`: a story has no contribution states |
| CreatedAt, UpdatedAt | DateTime (UTC) | From `TimeProvider` |
| PublishedAt | DateTime? | Set by `Publish(now)`, kept by `Unpublish()` as for items |
| DeletedAt | DateTime?, private set | Bin; query filter `DeletedAt == null` |
| Position | int, indexed | Order on `/stories` and in the admin list |
| Version | uint, `IsRowVersion` (xmin) | Concurrency for the title/summary form |
| Blocks | `List<StoryBlock>` | Cascade |

Methods `Publish`, `Unpublish`, `MoveToBin`, `RestoreFromBin`, `Touch`, shaped like `ArchiveItem`.

`StoryBlock`: Id; StoryId (cascade); Position (renumbered 0..n-1 after every change); Kind (`StoryBlockKind` Text or Item, string);
Text (max 20000, Text only); ArchiveItemId (Item only, FK **cascade**); Caption (max 1000, Item only; hidden whenever the item is).
Check constraint: Text kind has Text and no item, Item kind has an item and no Text. Indexes `(StoryId, Position)` and
`ArchiveItemId`. Query filter `b => b.Story.DeletedAt == null`. Limit of 200 blocks per story, enforced in the page model.

`AuditEntry` gets `int? StoryId` (FK `SetNull`). `AuditEntry` is not exported.

## Public surface (part 2)

| Surface | Change |
|---|---|
| `/stories` | `VisibleStories` ordered by `Position, Id`: title, summary, the first visible item card's picture as cover or a text tile; paged like collections; "No stories yet." with 200 |
| `/stories/{id:int}` | 404 unless in `VisibleStories`; the same `NotFound()` for unknown, draft, binned and all-hidden |
| Start page | A "Stories" section with the first three when any is visible; no header link |
| `/sitemap.xml` | `/stories` and each visible story (follows `AllowSearchEngines`, `public, max-age=600`) |
| `/feed.xml`, `/search`, JSON, Dublin Core | Unchanged: stories are not in them |
| Link preview | The story's title and summary, **no image**, nothing from an item |
| `/media/...` | Unchanged; item thumbnails go through the existing visibility checks |

Story page: `<article>`, `<h1>`, summary as lead; a text block is `text-preserve-lines` with `lang` of the material language; an
item block is a `<figure>` with the shared card partial (split from `_ItemCards.cshtml`) and a `<figcaption>`; breadcrumb; works
without JavaScript; logical CSS properties only.

## Curator screens (part 1)

Under `/Admin/Stories`, `Policies.ManageArchive`, a "Stories" link in the admin navigation next to Collections.

- **Index**: title, status, block count and hidden-block count (`T.Plural`), move earlier/later, a "New story" form (title only).
- **Edit**: title and summary form (`data-warn-unsaved`, `Version`; a conflict keeps the typed text); Publish/Unpublish, Preview,
  Move to bin (reversible, no confirm). Blocks in order, each with an anchor: text blocks have a textarea form; item blocks show
  the card, a caption form and, when visitors do not see the item, why (localized when shown: draft, public from {date}, in the
  bin, withdrawn; "Visitors see this item without its photos." for record-only). Every block has Move earlier, Move later and
  Remove (`data-confirm`). "Add text" and "Add an item" (item number or an `/items/{n}` or `/Admin/Items/Edit/{n}` address; binned
  or unknown items refused, rejected and withdrawn refused, drafts and embargoed accepted with a note). After a move or add the
  page redirects to the block's anchor with `data-focus-on-load`, as the photo order does.
- **Preview**: `_StoryBody` through `VisibleStoryBlocks` with a banner.
- **Item edit page** (part 3): "Used in N stories" with links, and a note when unpublishing or binning an item removes it from
  published stories.

## Privacy rules in detail

1. Draft, waiting, rejected, withdrawn items: dropped with their caption.
2. Embargoed: dropped until the day, using the injected `TimeProvider`.
3. Record-only: title, date and link, no picture.
4. In the bin: dropped; restoring the item brings the block back.
5. Deleted for good: the block goes with it (cascade).
6. Contributor did not agree to sharing beyond the website: the card shows on the website as items do; nothing reaches a surface
   that travels (preview has no image or item text, no feed, no JSON).
7. No leak of existence: a hidden block leaves no figure, id, caption, empty slot or "removed" text; a story with only hidden
   blocks answers the same 404; hidden-block counts exist only under `/Admin`.
8. Computed on each request; the sitemap may lag up to ten minutes (already accepted in `security.md`).
9. Curator text and captions are the curator's words and are not checked; the handbook says so.
10. A story draft is never public.

## Bin, audit, roles

- Stories go to the bin for 30 days (`Story.MoveToBin`); Bin page "Stories" section (restore, delete for good with confirm);
  `ArchiveBin.DeleteStoryAsync` and a purge step with `StoryPurged`. Binned stories are not exported.
- New `AuditAction` values, added at the end: `StoryCreated, StoryChanged, StoryPublished, StoryUnpublished, StoryItemAdded,
  StoryItemRemoved, StoryBlocksArranged, StoriesArranged, StoryMovedToBin, StoryRestored, StoryDeleted, StoryPurged`; new
  `AuditKind.Stories`; texts in `AuditText.Describe`, kinds in `KindOf`. `AuditLog.Record` takes an optional story; item
  add/remove also name the item. `StoryBlocksArranged` is recorded once per 30-minute sitting, like photos. Every handler
  records before `SaveChanges`.
- Roles: helpers get nothing (403); curators and admins everything.

## Export and import

- `ArchiveExporter.Version` 8 to 9; `archive.json` gets a `"stories"` array (streamed) with `ExportStory` and `ExportStoryBlock`.
- Full export: stories not in the bin and blocks whose item is not in the bin. Published-only export: `VisibleStories` and only
  the blocks `VisibleStoryBlocks` keeps, so a hidden item's id and caption never reach a public copy.
- `ExportTests` field list gets both types. **The import does not read stories**: `ExportImportTable` lists both as `Dropped`
  with the reason that their items arrive as drafts with new numbers; the dry run shows one plural line counting them (a capped
  counter, never deserialising the stories). Version 9 must be accepted.
- `docs/operations.md` format section and the export README text name stories.

## Languages

Only curator content is stored (title, summary, text, caption, the audit subject). Status words, "Untitled", the hidden-block
reasons and messages are localized when shown. Every new text is written out in `T[...]` or `T.Plural(...)` (ICU), has a German
entry, and the base file is rewritten. "Earlier/later", never up/down or left/right.

## Tests

1. `StoryVisibilityMatrixTests` (copy the seeding from `VisibilityMatrixTests`): a published story with a text block, a visible
   item A with a caption and an item B (own title word, caption and photo) in each state: draft, waiting, rejected, withdrawn,
   embargoed, in the bin, record-only, contributor not sharing, published. On `/stories`, `/stories/{id}`, `/`, `/sitemap.xml`,
   `/feed.xml`, `/search`, `/items/{B}` and B's thumbnail: hidden states show none of B's title, caption, id or file, and the
   story page HTML equals the page without block B (after the token and id are normalised); record-only shows title and link
   but no picture; not-sharing has nothing of B in any `og:` tag or the feed; story text never appears in search or the feed.
2. A draft, a binned story, an all-hidden story and an unknown id give an identical 404 and appear nowhere.
3. Change over time with the clock moved: embargo opens, publish/unpublish, bin and restore an item, delete an item for good.
4. `VisibilityGuardTests`: forbid `StoryStatus.Published`, `db.Stories`, `StoryBlocks` and `.Blocks` in public code.
5. `EndpointAccessTests` lists `/stories` and `/stories/{id:int}` as meant for visitors.
6. `HelperTests`: 403 on every Stories page and handler.
7. `StoryEditTests`: create, edit, publish, unpublish, add text and items (number and address), refusals, the block limit, move at
   both ends, a block of another story gives 404, the `Version` conflict, an audit entry per handler, one arranged entry per sitting.
8. Bin tests (part 3): bin, restore, delete, purge after 30 days.
9. Audit tests: every action has a kind and a text; the Activity filter offers "Stories".
10. Export tests: field lists, published-only and full exports as above, version 9, import dry run counts and creates nothing.
11. Browser tests for each reused behaviour: keyboard move keeps focus; the same move without JavaScript; Remove asks first;
    leaving an edited text block warns.
12. `AccessibilityTests`: public pages `/stories` and `/stories/1`; curator pages Index, Edit, Preview; `/stories/1` in the RTL
    and long-text runs.
13. Demo: a story at the end of the seeding ("Mill Street over thirty years": three text blocks, three published items with
    captions, one block naming the demo draft); a browser test checks the draft's title is absent publicly and present with
    its status line under `/Admin`.

## Parts

| Part | Content | Behaviour change |
|---|---|---|
| 1. Stories for curators | Model, migration (with `AuditEntry.StoryId`), `Visibility.Story`, `VisibleStories`, `VisibleStoryBlocks`, `/Admin/Stories` Index, Edit, Preview and `_StoryBody`, folder policy, audit, export version 9 and import table, demo story, tests 4, 6, 7, 9, 10, 11 and the curator half of 12, German entries, CHANGELOG | None for visitors |
| 2. Stories for visitors | `/stories`, `/stories/{id}`, start page, sitemap, link preview, card partial; tests 1, 2, 3, 5 and the public half of 12; security.md rows; German entries; CHANGELOG | Published stories become public. The `privacy-reviewer` must pass this part before merging |
| 3. Bin, item page, handbook | Story bin and purge, "Used in N stories", handbook in English and German, roadmap table (the milestone table is duplicated near lines 531 to 730: fix that), test 8, `/walkthrough` | Curators can bin stories |

## Security rows (docs/security.md)

Stories (public only when published, not in the bin and with a visible block; item blocks only through `VisibleStoryBlocks`; a
hidden story answers the same 404), stories beyond the website (no image or item text in the preview; not in feed, search, JSON),
stories in an export (published-only holds visible stories and blocks; the import does not read them). Known limit: story text
and captions are not checked and can name a hidden item or person; the sitemap may list a hidden story for up to ten minutes.

## Risks

- A later page reads `story.Blocks.Select(b => b.ArchiveItem...)`: stopped by the guard test and the "same page without the
  block" matrix check.
- A caption about a hidden item is the likeliest leak: it is dropped with its block, also in the published-only export.
- Removing a text block is immediate (with a confirm); there is no history for story text. Say so in the handbook; a history is
  a follow-up if wanted.
- EF query-filter warnings for `StoryBlock` reaching `ArchiveItem`: `VisibleStoryBlocks` never relies on the navigation.
- Two older places use `DateTime.UtcNow` (the feed's updated fallback and the item citation); new code uses `TimeProvider`.

## Changes after the privacy review

The `privacy-reviewer` found no design-level leak and asked for these, which are part of the plan:

1. **No trace in the HTML.** The public `_StoryBody` emits no block id, position, anchor, `data-` attribute or "n of m". Anchors
   and ids exist only on the Edit page. The "same page without block B" test normalises only the antiforgery token (not ids) and
   has a case with a hidden block between two visible ones.
2. **Block changes never touch the story row.** Block handlers save only `StoryBlock` rows: no `Touch`, no `Version` bump (an
   open title form must not report a conflict), and moving a block leaves `Story.UpdatedAt` alone. The sitemap has no `lastmod`
   for stories, the page shows no "updated" date, and no ETag or `Last-Modified` derives from block or item data. Test: a block
   move leaves `Version` unchanged.
3. **The rule's shape.** `Visibility.Item` is an expression over `ArchiveItem` and cannot be applied to a navigation. The story
   rule is a method on the context: `db.VisibleItems.Any(i => i.Id == b.ArchiveItemId)` inside the story query, so embargo and
   withdrawn come with it. The guard test also forbids `.ArchiveItem` and `b.ArchiveItem` under `Pages/Stories` and
   `Pages/Index`. Test: a story whose only item is embargoed answers 404 until the day.
4. **`StoryBlock.ArchiveItemId` is a nullable optional FK with cascade.** Admin code that labels blocks looks items up with
   `IgnoreQueryFilters`; public code loads by id through `VisibleItems`. Test: deleting an item for good leaves the story with
   its other blocks in order.
5. **`/stories` is computed from visible rows only**: total, paging and covers. Covers come from one query, not one per story,
   and the page size is capped. Test: `/stories` is byte-identical with and without a hidden story.
6. **Link preview and sitemap.** The preview holds no item text, id or picture; when the summary is empty the description is empty
   (never the first text block). A story cover must never go into `og:image` without a `MayTravel` check (comment in the code).
   Story ids are sequential, so a gap is visible, as for items: listed under known limits.
7. **A published-only export is a public copy**, so captions of visible items travel with it; this is intended (the items do too).
   `Today` is captured once and used for both the items and the stories, and a test checks that no block in a published-only
   export names an item missing from `items`.
8. **404 comparison includes headers** (`Cache-Control`, `Vary`), not only status and body.

## Decisions

All recommended answers accepted by the maintainer (Seb H.) on 2026-11-23:

1. Helpers do not draft stories; only curators and admins.
2. Stories are not in `/feed.xml`.
3. Stories are not searched in M29.
4. The import does not read stories (counted and reported in the dry run).
5. A block goes when its item is deleted for good.
6. A published story whose blocks are all hidden answers 404, like a draft.
