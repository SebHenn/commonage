# Plan for M27: visibility levels

Status: **plan, no code yet.** The four questions are answered (see "Decisions"), and the adversarial review of this plan by the
`privacy-reviewer` agent is done and folded in (see "Changes after the review"). Part 1 can start. M27 changes the rule every public page rests on
("only published items are public"), so the order is: plan, review of the plan, then code in small parts, each reviewed.

## What is wanted

1. A **record without its media**: the item page, search and lists show title, description, date, place and credit,
   but no picture, page image or recording, anywhere.
2. An **embargo**: a published item that is not public until a date. Until then it is as hidden as a draft.
3. One place decides: `PublishedItems` becomes `VisibleItems`, and nothing public reads items another way.

Not planned: members-only (visitor accounts are not planned; it waits for a group to ask).

## The rule, stated once

An item is **visible** when `Status == Published`, it is not in the bin (query filter, as now) and
`PublicFrom` is empty or today (in the archive's time zone, see below) or earlier.
Its **media are visible** when the item is visible and `MediaLevel == Everything`.

Two queries in `Data/` (the only place that may write `ItemStatus.Published` in a query):

- `db.VisibleItems(time)`: what record pages, lists, counts, feeds, search and the sitemap start from.
- `db.VisibleMedia(time)`: media of visible items with `MediaLevel == Everything`. `/media/*` and every place that
  picks a cover or a first photo start from this, never from `VisibleItems.SelectMany(MediaFiles)`.

`time` comes from the injected `TimeProvider`, passed as a parameter so EF translates it, so tests can set the clock.
`PublishedItems` is deleted, so the compiler lists every place that must choose.

## Data model

On `ArchiveItem`:

| Column | Type | Meaning |
|---|---|---|
| `MediaLevel` | enum, default `Everything` | `Everything` or `RecordOnly`. Stored as a number, named in code. |
| `PublicFrom` | `DateOnly?` | Null = no embargo. Public from the start of that day in the archive's time zone (`ArchiveSettings.TimeZone`, as `Dates.Zone`). |

Migration with defaults that change nothing for existing items. Export: both fields are exported in the full export,
`ArchiveExporter.Version` goes up, `docs/operations.md` says so. In the **published-only** export an embargoed item is
left out and a record-only item has no media files and no photo list (the export test names both). `Unpublish`
keeps the embargo date; `Publish` of a draft never sets one by itself.

Why a date and not a time: a curator thinks "from the first of March". The archive already has a configured time zone, so the
day is that zone's day, the same one every other date on the pages uses.

Why two fields and not one status: "published" stays the curator's yes/no; an embargo is a date on a yes; record-only
is a property of what is shown. Each can change without the others.

## Every public path that must go through the rule

Found by reading `PublishedItems` and `ItemStatus.Published` in `src/` (this list is the checklist for the matrix test):

| Surface | Today | Change |
|---|---|---|
| Start page (recent, decades, popular tags, tag kinds, "help wanted") | `PublishedItems` | `VisibleItems`; the recent grid shows no picture for record-only |
| Search (text, filters, popular tags) | `PublishedItems` + `ItemSearch` | `VisibleItems`; **PDF text (`ItemFileText`) must not match a record-only item**, or search would reveal what its pages say |
| Tag index, collection pages | `PublishedItems` | `VisibleItems` |
| Collection cards, counts, years, cover (`ItemSummary`, `CollectionCards`) | **`ItemStatus.Published` written out** | go through the new query; a cover must be `VisibleMedia`. A collection whose items are all embargoed has no card |
| Item page and its media list, photo n of m, next/previous, "from the same time" (`ItemSurroundings`) | `PublishedItems` | `VisibleItems`; media included only if `MediaLevel == Everything`; surroundings show no picture for record-only |
| Item page: recording, transcript | description is the transcript | Open question 3 |
| Link preview (OpenGraph) and "cite this item" | first photo if `MayTravel` | no image for record-only; the text part is unchanged |
| Feed and sitemap (`PublicFeeds`) | `PublishedItems`, **and `Items.Any(Published)` written out for collections** | `VisibleItems`; no enclosure or image for record-only |
| "Do you know more about this?" (`Tell`) | `PublishedItems` | `VisibleItems`: an embargoed item cannot receive notes |
| `/media/{id}/{variant}`, `/page/{n}`, `/audio` | `PublishedItems.SelectMany(MediaFiles)` | `VisibleMedia` |
| Published-only export | `PublishedItems` | as in the data model |
| Demo content | writes `Published` | unchanged; demo items are `Everything` |
| Curator pages, preview | all items | unchanged, but the badge and the preview banner say "record only" / "public from {date}" so a curator is never surprised |

Browser caching: `/media` is `public, max-age=600`. Switching an item to record-only leaves a picture in a visitor's browser
for up to ten minutes. This is the existing, documented limit for unpublishing; it is stated for the new level too.

## What a curator sees and does

- Item form: "What visitors get" (everything / the record only, without photos and recordings) and "Public from" (date,
  optional). German text for both; plural and date formatting through the existing helpers.
- List and bulk: filter and bulk-set for both (helpers cannot set either, like publishing).
- Every change is in the activity log: new `AuditAction`s `ItemMediaLevelChanged` and `ItemPublicFromChanged`, with
  `AuditText.Describe` and `KindOf`. The date is formatted by the existing day helper, not stored as text in one language.
- The item page for a visitor on a record-only item says in one fixed sentence that the pictures are not shown, so the empty
  space is not mistaken for a fault. (One whole sentence, a translation entry.)

## Tests (the point of the plan)

1. **`VisibilityMatrixTests`**: one set of items in every state (published/full, published/record-only,
   published/embargoed future, published/embargo today and past, draft, in the bin), each with a unique marker word in title,
   description, tag, collection title, PDF text and photo alt text. For every public address in the table, assert the marker
   of a hidden thing is **absent** and of a visible one **present**. The list of addresses is built from the table above;
   a new public address added to `EndpointAccessTests` must also be added to the matrix (the matrix test fails if
   `EndpointAccessTests`' list has an address it does not know).
2. **Clock test**: with a fake `TimeProvider`, an item appears at the start of its day UTC and not the second before.
3. **Media test**: record-only photo, page, audio, card, all variants return 404 to a visitor, 200 to a curator;
   an embargoed item's media likewise.
4. **Guard test**: no source file under `Pages/` (except `Pages/Admin`), `Search/`, `Media/` and `Export/` mentions
   `ItemStatus.Published`, `ArchiveItems` or `IgnoreQueryFilters`. This is a rule about a forbidden path to the data, not a pin on
   an implementation.
5. Browser: the record-only item page (no broken image, the sentence is there), the form, RTL and a long text.
6. Export: round trip fields, the published-only cases, version number.

## Parts and review

| Part | Content | Behaviour change |
|---|---|---|
| 1 | `VisibleItems`/`VisibleMedia`, `PublishedItems` deleted, `ItemSummary` and `PublicFeeds` moved onto it, the matrix and guard tests against today's behaviour | none, which is the proof |
| 2 | `MediaLevel`: migration, form, list, audit, every media surface, search over PDF text, export | record-only |
| 3 | `PublicFrom`: migration, form, clock, `Tell`, collections and counts | embargo |
| 4 | Export version and published-only cases, handbook (both languages), `security.md` rows, walkthrough on phone and desktop width | docs |
| 5 | Adversarial privacy review of the whole milestone: a reviewer who tries to find an item or file that leaks through any address, including ones not in the table | none |

`privacy-reviewer` runs after every part, as always; part 5 is a separate pass whose only job is to break the rule.

## Risks

- **A second route to items nobody thought of** (an `Include` from another entity: collection to items, tag to items, contribution
  to item). Mitigation: the guard test, and the matrix has every entity that has a path to items. Query filters are not used for
  visibility because curators need the same entities, so the discipline is the query and the test.
- **Search reveals more than the page shows** (PDF text, the recording's transcript). Called out above.
- **Time**: an embargo is evaluated at request time; anything cached longer than a request (feeds, `max-age`) can show an
  item late or hide it late. Feeds and the sitemap are sent with `max-age=600` too, so an item that becomes record-only or embargoed can stay in a shared cache for ten minutes; `security.md` says so.
- **Curator error**: record-only is easy to forget. The badge and the preview banner exist for this.

## Decisions
(Recommendations taken by the user.) Embargo hides the whole item; a date, not a time; a record-only recording keeps its
transcript (the handbook says so); contributors cannot ask for record-only. Where the review changed a detail, below wins.

## Questions asked (kept for the record)

1. **Embargo hides the whole item** (recommended: as hidden as a draft until the day) or only its media (the record is
   public, the media appear on the day)? The second is "record only until a date" and could be one combined field; the first
   is simpler to reason about and cannot leak by title.
2. **Date granularity**: a date, from the start of that day UTC (recommended), or date and time?
3. **Record-only and a recording's transcript**: the description is the transcript (M25), so a record-only recording would
   still show it. Accept this (the curator chose a record, and the transcript is text), or refuse record-only for items with audio
   until the transcript flag is used? Recommended: accept, and say it in the handbook.
4. **Contribution links**: may a contributor ask for "record only" when sending (a "please do not show my photo" choice)?
   Recommended: no, not in M27; curators set it, and the contributor's consent text is not changed.

## Changes after the review

Gaps found by an adversarial read of the source, all adopted:

1. **`/collections/{id}`** names a collection by id even with no visible item. It returns 404 when it has none (table and matrix).
2. **The search page's collection list** shows only collections with a visible item.
3. **PDF text exclusion** is a flag on `ItemFilter` set by public callers only, so `Pages/Admin` keeps searching PDFs. It covers both the text clause and the date clause. The matrix has a PDF word and a date that only a PDF contains.
4. **Expressions, not extension methods.** `ItemSummary.FromItem` and `ToCards` are expressions over navigations (`c.Items`, `i.MediaFiles`), so part 1 adds shared `Expression<Func<...>>` definitions for "visible on this day" and "media visible on this day", both taking the day as a parameter (which `ToCards`, `LoadCollectionCards` and Collections/Details do not have yet). `db.VisibleItems`/`VisibleMedia` are built from them. A record-only item has no thumbnail in `FromItem`, so no card links to `/media/{id}/thumbnail`. A collection card with no visible cover uses the existing no-cover look.
5. **Item page, link preview and feed** filter the media in the query, not in the view: a record-only item loads no files at all.
6. **Captions and alt text** are text about the media and are hidden with it. The description and the transcript stay (decision 3). Matrix marker in a caption.
7. **Curator preview** shows the media of a record-only item, with a banner naming the level ("Preview. Visitors see the record only.", "Preview. Public from {date}."); the old "This item is public" line is only for an item that is. The collection cover picker warns for a record-only or embargoed item, like it does for an unpublished one.
8. **Time zone**: the archive's, not UTC (the form, badge and preview show the date as the curator entered it).
9. **Ordering and "recent"**: an effective public date, `max(PublishedAt, PublicFrom)`, orders the start page, search "recently added" and the feed and is the feed's `published`, so an embargoed item surfaces when it opens.
10. **Caching** corrected above.
11. **Guard test** has an explicit allowlist (the media endpoints and processing use `IgnoreQueryFilters` on purpose, the contribute page for contributions, the exporter's full export for `ArchiveItems`) and forbids the navigation routes too (`.Items`, `.MediaFiles`, `db.MediaFiles`, `db.Collections`, `db.Tags`) in public code outside `Data/`. It is a tripwire: the matrix is what proves behaviour.
12. **Matrix coverage** is a list of item-related addresses kept in the test, plus an explicit list of addresses that are not about items (`/Account/*`, `/branding/*`, `/health`, `/date-check`, `/contribute/{token}`); `EndpointAccessTests`' list must be covered by the two.
13. **Media test**: `/media/*` returns 404 for hidden media to everyone; the staff path is `/admin/media/*`. A helper sees only drafts today, so an embargoed published item's media is not theirs either; that stays, and a test says it.
14. **Published-only export** drops the media rows, the photo list, exported covers and a collection's cover that points at a record-only file; embargoed items are out. File text stays out of the export, as now, and the test names the decision.
15. **More matrix cases**: a tag only on hidden items (tag index, popular tags, tag kinds, suggestions); a collection of only hidden items (page, list, sitemap); a cover that is a hidden file; decade counts and year spans; next/previous and "from the same time" with an embargoed neighbour; POST to `Tell` on an embargoed item; the day `PublicFrom == today`; an item moved between collections; unpublish and publish again after the embargo.
