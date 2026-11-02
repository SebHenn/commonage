# Roadmap to v0.1

Estimates assume about 10 hours a week. Every milestone ends with something demonstrable.

| Milestone | Weeks | Done when |
|---|---|---|
| **M0 Setup** ✅ | 1 | A fresh clone runs with `docker compose up`; CI builds, tests and scans for secrets. |
| **M1 Text records** ✅ | 2–4 | A curator logs in, creates and publishes a text-only record, and visitors see it. Drafts stay hidden. UI text is localisable (German and English). |
| **M2 Photos** ✅ | 5–8 | Curators upload photos; originals stay private; previews are generated in the background without EXIF/GPS data. Files of unpublished items return 404. Show it to a partner group. |
| **M3 Search and browse** ✅ | 9–11 | Tags, uncertain dates ("circa 1950"), PostgreSQL full-text search, filters, pagination, mobile layout. |
| **M4 Contributions** ✅ | 12–15 | Anyone with the link can submit material with consent and rights details; curators approve or reject it. Spam protection; email optional. |
| **M5 Trust** ✅ | 16–19 | Export (metadata + files), tested backup and restore, audit log, curator management, deletion of personal data. |
| **M6 Release v0.1** ✅ | 20–22 | HTTPS deployment guide, documentation, demo content, accessibility and security review, five-task test with a real group ([results](usability-test.md#results)). Released as v0.1.0 under the AGPL-3.0. |

## Decisions

- License: **GNU AGPL v3.0**. What is built on Commonage stays open, also when it is offered as a hosted service;
  groups running it unchanged have nothing to do. Every page links to the source code (`Source:Url`, default the
  main repository). Contributions come in under the same license with a DCO sign-off, no contributor agreement.
  The license covers the software, never the archives' content.
- One web application (Razor Pages) plus one test project. Split into more projects only when it hurts.
- A contribution creates an `ArchiveItem` with status `Submitted`; publishing only changes its status.
- Item status: `Draft`, `Published`, `Submitted` (a contribution waiting for review), `Rejected` (kept
  hidden until a curator deletes it) and `Withdrawn` (the contributor took back their consent: hidden, and it
  can never be published again without a new contribution). Contributors withdraw by asking the archive, as the
  consent text says, because they have no account and need not give an email address.
- Contributions come in through contribution links: a curator creates a link for a collection ("Share your
  photos of the Mill Street flood") and shares it. The address holds a random 128-bit token; closed and unknown
  links show "not found". Links are closed, never deleted, so contributions keep their origin.
- A `Contribution` record next to the item holds the contributor's name, optional email, whether their name
  may be shown publicly, a rights statement (made it themselves / someone else with permission / unknown, plus
  the photographer or author if known) and the consent text exactly as shown, in the contributor's language.
  Only curators see it, except the name when the contributor chose to be credited.
- Spam protection without third-party services: the unlisted link, the antiforgery token, a hidden honeypot
  field, a minimum time between showing and sending the form (`Contributions:MinimumFillSeconds`, default 3,
  measured with a data-protected timestamp) and a limit per IP address (`Contributions:MaxSubmissionsPerHour`,
  default 30, generous because a community event may share one address). Detected spam gets the normal thank-you
  page and is not saved. Behind a reverse proxy, forwarded headers must be configured ([deployment.md](deployment.md)),
  or every visitor counts as the proxy's address.
- No emails are sent. Curators see waiting contributions on the item list. Any item can be deleted with its
  files and contribution record.
- Personal data: "Contributors" finds a person's contributions by part of their name or email address. Curators
  can remove a contributor's name and email address (the rights statement, the photographer or author and the
  consent text stay, because the archive needs them to keep using the material), mark a contribution as withdrawn,
  or delete the item. Backups keep removed data until they are replaced; docs/operations.md says so.
- Two roles: curators manage the archive; admins can also manage accounts, export and read the activity log.
  New accounts get a one-time setup link (an Identity password-reset token, valid seven days) that the admin
  passes on, because Commonage sends no emails. Admins cannot change, disable or delete their own account, so an
  admin always remains. Disabling locks the account out and changes its security stamp, which is checked every
  minute, so open logins end quickly. Identity's messages are translated through `LocalizedIdentityErrorDescriber`.
- The activity log (`AuditEntry`) is written by `AuditLog.Record` into the same `SaveChanges` as the change it
  describes, so there is never a change without an entry or the other way round. Actions are stored by name. An
  entry keeps the item's title and the account name as they were, so it still makes sense after either is deleted
  (the link to the item is set to null). It never holds contributor names or email addresses, so removing a
  contributor's data never touches the log. Entries are kept indefinitely for now.
- Export: one ZIP with `archive.json` (complete, versioned format), `items.csv` (for spreadsheets, protected
  against formula injection), the original files and a README; streamed to the browser, so no temporary copy is
  needed. Files are checked against their SHA-256 while exporting. Contributor names and emails only on request.
  Admins only, and every export is logged.
- Backup and restore are shell scripts around `pg_dump`/`pg_restore` and `tar` on the Docker volumes, not an
  app feature: they work when the app does not, and restore a complete archive including accounts and login keys.
  `scripts/test-backup-restore.sh` tests the full cycle in CI.
- Production runs behind Caddy (`compose.production.yaml`), which gets certificates by itself and is the only
  container with open ports. The app trusts forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`) only because
  nothing else can reach it, and accepts only the archive's own host name (`AllowedHosts`). Operators switch it on
  with `COMPOSE_FILE` in `.env`, so the same `docker compose` commands and backup scripts work in development and
  production.
- Every response carries security headers with a strict Content Security Policy; pages therefore contain no inline
  script or style (enforced by `SecurityHeaderTests`). Passwords need 12 characters and no forced symbols. Empty
  error responses get a translated page; visitors never see technical details.
- Demo content is generated by the app (drawn pictures, invented town), so it is free of anyone else's rights, and
  only fills an empty archive. English and German.
- The version lives in `Directory.Build.props` and `CHANGELOG.md`; curators see it in the footer, visitors do not.
  A release is a Git tag (`v0.1.0`), which the deployment guide checks out.
- Every public page queries `CommonageDbContext.PublishedItems`, never `ArchiveItems` directly.
- Migrations are applied automatically at startup (single-instance self-hosting).
- UI text is English-keyed and translated through `Resources/SharedResource.{culture}.resx`.
- **Languages.** Commonage is meant to support most languages in the long run, for its interface and for the
  archive's content, in every part of the app. v0.1 ships English and German, but all code is written so that
  more languages can be added without rework:
  - Every text the interface shows comes from the resource files. Sentences are whole format strings with
    placeholders, never pieced together, because word order differs between languages.
  - Dates and numbers are formatted with the current culture. Fixed formats are only used where they are
    deliberately language-neutral, such as ISO dates.
  - Stored data is language-neutral where it can be (`DateFrom`/`DateTo`, status values), and what people
    type is stored as typed.
  - No assumptions about scripts: words are not always separated by spaces, letters can carry combining
    marks, and text can run right to left. Layout uses Bootstrap's start/end utilities, not left/right.
  - Language-specific knowledge (word lists such as month names or "circa") lives in one clearly marked place
    per feature, so a language can be added there and tested.
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

### Languages

See the "Languages" decision above for the rules that already apply.

- **Interface languages.** One list of UI languages instead of three (today in `Program.cs`, `SetLanguage`
  and the layout's language buttons), a language picker that scales beyond two, and correct plural forms.
  A translation workflow for volunteers who don't use Git, for example Weblate on the resx files.
- **Right-to-left languages** (Arabic, Hebrew, Persian, Urdu): `dir="rtl"` from the current language,
  Bootstrap's RTL stylesheet, and a check of icons, spacing and the pager.
- **Content language.** A per-archive setting for the language(s) the material is written in, separate from
  each visitor's UI language. Dates and search use it, and item pages mark their content with a `lang`
  attribute so screen readers and fonts pick the right language. Archives with material in several languages
  may need a language per item.
- **Sorting.** Alphabetical order follows the database collation. Per-language order (Swedish å after z,
  Spanish ñ after n) needs ICU collations in PostgreSQL, chosen from the content language.
- **Emails, exports and demo content**: exports (README) are English only; the demo exists in English and German.
  Both are translated like the interface.
- **Dates in more languages.** `DateRange` has English and German words built in: month names and the words
  for "circa", "before", "until", "after", "since", "between … and", "century" and decades. Plan:
  - Month names come from .NET (`CultureInfo.DateTimeFormat`: full, abbreviated and genitive forms, such as
    Polish "12 marca 1953"), lowercased, without trailing dots and with accents removed. Regional extras such as
    Austrian "Jänner" stay a short hand-written list. The Debian-based Docker image includes the ICU data this
    needs.
  - Everything else goes into a `DateVocabulary` per language, written in C# with its own tests, because some
    grammar is not a word list (French "XIXe siècle" uses Roman numerals). The patterns are built once at
    startup from the active vocabularies.
  - Dates are read in the archive's content language(s) (see above), independent of each visitor's UI
    language. Merging all languages does not work, because words
    clash: "listopad" is November in Polish and Czech but October in Croatian.
  - Stored dates stay language-neutral (`DateFrom`/`DateTo`, the curator's own `DateText`), so adding a
    language never requires reprocessing existing items. Ambiguous numeric dates ("03/04/1953": 4 March in
    the US, 3 April in the UK) stay rejected unless the archive's languages settle the order.
  - Harder cases, each its own piece of work: Japanese and Chinese date forms ("1953年3月12日", era years such as
    "昭和28年") and other calendars (the Thai Buddhist year 2496 is 1953; Hijri dates), which would otherwise be
    misread as Western years.
- **Search in languages without spaces** (Chinese, Japanese, Thai). Today a run of characters without spaces
  is one search word, so "歴史" does not find "東京都の歴史". Plan: index each character of these scripts as its
  own word and search for the typed characters as a phrase ("歴 followed by 史"), using PostgreSQL's built-in
  phrase search, so still no extension and no search server. Matches are by characters, not real words
  (京都 also matches inside 東京都), and long texts make the index bigger. Real word splitting would need an
  extension such as PGroonga or a separate search server.
- **Language-aware search.** The content language picks PostgreSQL's built-in rules for that
  language (for example `german` or `english`), so "Schulen" and "Schule" count as the same word, and that
  handles words joined to articles or prepositions in Arabic and Hebrew. Mixed-language archives need a
  decision: one language per archive, or per item.

### Trust and accessibility

- Two-factor authentication for curators and admins.
- A limit on login attempts per address, not only per account.
- A description (alt text) for each photo, written by curators and suggested by contributors.
- Field errors linked to their fields with `aria-describedby`; a session with screen reader users.
- How long the activity log is kept, and whether old entries are thinned out.
- Encrypting the keys that protect logins and setup links (Data Protection) with a key outside the database.

