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
  covers title (weight A), tags and date text (B) and description (C). It uses the `simple` text search
  configuration (no stemming, same for German and English). Every search word matches word beginnings, so
  "Schule" also finds "Schulen". This works for every language that separates words with spaces, in any
  script (Hindi and Korean included). Chinese, Japanese and Thai only match at the start of a run of
  characters, and words joined to articles (Arabic "al-", Hebrew "ha-") only match the whole joined word;
  see "After v0.1".
- Search ignores accents ("Muhle" finds "Mühle", "Strasse" finds "Straße") without requiring a PostgreSQL
  extension. Documents and search text pass through the database function `commonage_fold`. A migration
  creates it from built-in functions only (Unicode decomposition, dropping combining accents and Arabic vowel
  marks, spelling out ß, æ, œ, ø, ł and a few more), so it works on any server with a UTF-8 database (the
  default). At every start `AccentFolding` checks whether the `unaccent` extension can be used; if so, it
  switches the function to `unaccent` (which covers more letters, such as ligatures) and re-indexes all items.
  Hosts that install the extension later get it on the next start; hosts that cannot install it keep the
  built-in folding and see a warning in the log. The search text is folded in the database before the query
  is built, and only letters, digits and the marks that belong to letters reach `to_tsquery`.
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

## After v0.1

- **Search in languages without spaces** (Chinese, Japanese, Thai). Today a run of characters without spaces
  is one search word, so "歴史" does not find "東京都の歴史". Plan: index each character of these scripts as its
  own word and search for the typed characters as a phrase ("歴 followed by 史"), using PostgreSQL's built-in
  phrase search, so still no extension and no search server. Matches are by characters, not real words
  (京都 also matches inside 東京都), and long texts make the index bigger. Real word splitting would need an
  extension such as PGroonga or a separate search server.
- **Language-aware search.** Per-archive language setting that picks PostgreSQL's built-in rules for that
  language (for example `german` or `english`), so "Schulen" and "Schule" count as the same word, and that
  handles words joined to articles or prepositions in Arabic and Hebrew. Mixed-language archives need a
  decision: one language per archive, or per item.

## Open decisions

- License (AGPL-3.0 is the current favourite). Must be chosen before the repository is made public.
