# Roadmap to v0.1

Estimates assume about 10 hours a week. Every milestone ends with something demonstrable.

| Milestone | Weeks | Done when |
|---|---|---|
| **M0 Setup** ✅ | 1 | A fresh clone runs with `docker compose up`; CI builds, tests and scans for secrets. |
| **M1 Text records** ✅ | 2–4 | A curator logs in, creates and publishes a text-only record, and visitors see it. Drafts stay hidden. UI text is localisable (German and English). |
| **M2 Photos** ✅ | 5–8 | Curators upload photos; originals stay private; previews are generated in the background without EXIF/GPS data. Files of unpublished items return 404. Show it to a partner group. |
| **M3 Search and browse** ✅ | 9–11 | Tags, uncertain dates ("circa 1950"), PostgreSQL full-text search, filters, pagination, mobile layout. |
| **M4 Contributions** | 12–15 | Anyone with the link can submit material with consent and rights details; curators approve or reject it. Spam protection; email optional. |
| **M5 Trust** | 16–19 | Export (metadata + files), tested backup and restore, audit log, curator management, deletion of personal data. |
| **M6 Release v0.1** | 20–22 | HTTPS deployment guide, documentation, demo content, accessibility and security review, five-task test with a real group. |

## Decisions

- One web application (Razor Pages) plus one test project. Split into more projects only when it hurts.
- A contribution creates an `ArchiveItem` with status `Submitted`; publishing only changes its status.
- Item status: `Draft` and `Published` so far; `Submitted`, `Rejected` and `Withdrawn` arrive with contributions (M4). Files will have their own `IsPublic` flag.
- Every public page queries `CommonageDbContext.PublishedItems`, never `ArchiveItems` directly.
- Migrations are applied automatically at startup (single-instance self-hosting).
- UI text is English-keyed and translated through `Resources/SharedResource.{culture}.resx`.
- Uncertain dates: `DateFrom` / `DateTo` for filtering, `DateText` for display. Curators type one date field
  ("circa 1950", "1950er Jahre", "vor 1945"); `DateRange.TryParse` derives the range, and text it cannot read
  is rejected rather than stored without a range. "Circa" widens a year by five years on each side.
- Search uses PostgreSQL full-text search; no separate search server. A stored generated `tsvector` column
  covers title (weight A), tags and date text (B) and description (C). It uses the `commonage` text search
  configuration: `simple` (no stemming, same for German and English) with the `unaccent` dictionary in front,
  so "Muhle" finds "Mühle" and "Strasse" finds "Straße". Every search word matches word beginnings, so
  "Schule" also finds "Schulen". The database user needs to be allowed to create the `unaccent` extension
  (a trusted extension, so the database owner can).
- Tags are a `Tag` table with a many-to-many link to items, matched case-insensitively. `ArchiveItem.TagNames`
  repeats the names so the generated search column can include them; `SetTags` keeps it in sync.
- Filtering, sorting and paging live in `Search/ItemSearch.cs` and take the base query as input, so public
  pages still start from `PublishedItems`. The page number is the query value `p`, because `page` is reserved
  by Razor Pages.
- Original uploads are never modified. Files are stored under generated names and served through an
  endpoint that checks permissions. Only curators can download originals; visitors see previews of
  published items. (The per-file `IsPublic` flag is postponed until a group needs it.)
- Uploads are identified by their content (magic bytes), never by file name or browser content type.
  Accepted for now: JPEG, PNG, TIFF, WebP.
- Previews and thumbnails are generated one at a time by a background service (`Channel<T>`), with the
  state in the database so unfinished work resumes after a restart.

## Open decisions

- License (AGPL-3.0 is the current favourite). Must be chosen before the repository is made public.
