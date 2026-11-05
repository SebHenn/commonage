# Roadmap

v0.1 is done; what comes next is under [After v0.1](#after-v01).

## To v0.1

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
- No emails are sent. Curators see waiting contributions on the item list. Any item can be moved to the bin, and from
  there deleted with its files and contribution record.
- Personal data: "Contributors" finds a person's contributions by part of their name or email address. Curators
  can remove a contributor's name and email address (the rights statement, the photographer or author and the
  consent text stay, because the archive needs them to keep using the material), mark a contribution as withdrawn,
  or move the item to the bin and delete it there. Backups keep removed data until they are replaced; docs/operations.md says so.
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
  A release is a Git tag (`v0.1.0`), which the deployment guide checks out. From 0.1.1, the release workflow publishes
  Docker images for x86 and ARM to `ghcr.io/sebhenn/commonage` after a maintainer's approval, with SBOM and build
  attestation; production pulls them (`COMMONAGE_VERSION`), development still builds from source.
- Repository: squash merges only; `main` and `v*` tags protected by rulesets; required checks; Dependabot, CodeQL,
  dependency review, Scorecard and a DCO sign-off check. One maintainer for now, so pull requests need passing
  checks but no approving review; nobody can bypass the rules. GitHub does not let authors approve their own pull
  requests, so Scorecard's branch protection and code review alerts are dismissed for this reason. Require a review
  when a second maintainer joins.
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
- Deleting never removes anything at once: items and photos go to a bin for 30 days first, hidden by EF Core query
  filters so no query can show them by accident (`Data/ArchiveBin.cs`). Pages that need them ask with
  `IgnoreQueryFilters`. "Delete for good" exists for personal-data requests and spam.
- `ArchiveItem` carries PostgreSQL's `xmin` as a concurrency token. A form sends the version it opened, so a save from an
  outdated form fails, shows both versions and keeps what was typed; saving again is a deliberate overwrite.
- Uploads are refused before any of the request is read when the disk is nearly full (`Operations/ArchiveHealth.cs`),
  because a full disk also stops PostgreSQL. `backup.sh` writes `App_Data/last-backup`, outside the media folder, so a
  restore cannot bring back an old date.
- Original uploads are never modified. Files are stored under generated names and served through an
  endpoint that checks permissions. Only curators can download originals; visitors see previews of
  published items. (The per-file `IsPublic` flag is postponed until a group needs it.)
- Uploads are identified by their content (magic bytes), never by file name or browser content type.
  Accepted for now: JPEG, PNG, TIFF, WebP.
- Previews and thumbnails are generated one at a time by a background service (`Channel<T>`), with the
  state in the database so unfinished work resumes after a restart.

## After v0.1

Planned in November 2026 from audits of the code (visitors and contributors, curators and admins, and product gaps
compared with Omeka S, Mukurtu, CollectiveAccess and AtoM), an adversarial review of the plan, and a walk through the
running demo archive at phone, tablet and desktop width, acting out the main tasks. Only one usability test exists so
far and it recorded no per-task detail, so this order is a judgement, not evidence. A tester tries the app between
milestones and reports what comes up, and the order changes if that calls for it. Nothing waits for scheduled sessions:
every milestone is tested in the running app, in addition to the automated tests.

The walk-through also showed what to keep: the public pages checked at 320 px have no sideways scrolling, in German
too; the review page explains the contribution and the consent clearly; and uncertain dates read naturally.

The aim of the next stretch: the archive is safe to trust with real material, looks like the group's own, and a
volunteer can get through a shoebox of 300 photos in an evening.

### Milestones

Sizes are weeks at 10 hours a week, as a measure of relative effort, not dates. They add up to about 54 weeks; allow
15–20% more for holidays, fixes and translation. v0.1 took far less calendar time than its estimate, so measure the
real pace over M7 and M8 before promising dates. Each milestone is a minor release (M7 is v0.2).

| Milestone | Size | Done when |
|---|---|---|
| **M7 Nothing gets lost** ✅ | 3 | A deleted item can be restored for 30 days. A nearly full disk refuses uploads with a friendly message instead of stopping the database. Two curators editing one item do not silently overwrite each other. Admins see when the last backup ran. The README says what works today. |
| **M8 Safety net** ✅ | 3 | `scripts/update.sh` upgrades a server with a backup first and rolls back after a failed upgrade, and CI proves both starting from the previous published image. Browser and axe tests run in CI at phone and desktop width. |
| **M9 Quick wins** ✅ | 2 | A contributor sees "Understood as 1945 to 1955" before sending, so an unreadable date no longer costs them their chosen photos. Every form has a working error summary. A curator goes through a filtered list with "Save and next". The browser's large-font setting takes effect. |
| **M10 Archive identity and trust pages** ✅ | 4 | An admin sets the name, logo and colour without touching code, and buttons and links really change colour. About, Contact, Privacy and Imprint pages are linked wherever a contributor needs them. A checklist guides a new admin through setup. |
| **M11 Look and feel** ✅ | 4 | The demo archive opens with the group's name, recent photos and collection image cards, sharp on a phone and with portraits uncropped. Search shows active filters as removable chips. Pages work in right-to-left and in a long-text test language. |
| **M12 Catalogue fields** ✅ | 3 | A curator records rights, credit, reference number, where the original is kept and alt text on an item, and can filter for what is missing. |
| **M13 Shoebox** ✅ | 4 | A curator turns 300 scans into drafts with shared values, sees progress and can retry failures, is warned about identical files, and publishes a chosen set in one action. An item without a real title cannot be published. |
| **M14 Phone-first sending** | 4 | Over slow mobile data, a contributor sees their chosen photos, watches a progress bar and cannot send twice. The new tasks are walked through in the running app and given to the tester, and a week of fixes is done. |
| **M15 Readable items** | 4 | A visitor reads a scanned letter at full size, sees "Photo 2 of 4" with its caption, and moves on to the next item or to items from the same time. Curators reorder, rotate and describe each photo. |
| **M16 Rights and sharing** | 3 | Every item page says whether it may be reused, in plain language. "Cite this item" gives a line to copy. A link pasted into a messenger shows the picture and the archive's name. |
| **M17 Language reach** | 5 | A translator without Git has tried Weblate on a few pages. Missing translations fail a test. Counts read correctly under Polish plural rules. Item text carries the right `lang`. Dates that are accepted today are still accepted. |
| **M18 Accounts and system** | 4 | A curator logs in with an authenticator app. Login keys are encrypted with a key outside the database, and the backup test still passes. An admin sees version, disk use, failed previews (with retry) and backup age on one page. |
| **M19 Discovery** | 3 | A visitor taps "1960s" and finds the flood, opens "Places" and picks "Mill Street". A curator merges "Mill St" into "Mill Street". |
| **M20 Community knowledge** | 4 | With the feature switched on, a visitor says "that's my aunt on the left" or asks for removal from an item page, and a curator handles it. A curator prints a poster with a QR code for a contribution link. |
| **M21 Import and scale** | 4 | A group imports a spreadsheet and a ZIP of scans after a dry run that lists every problem. A 50,000-item archive on a small ARM server stays fast and exports without running out of memory. |

The review after M14 also decides what comes next. PDF documents, audio or an earlier import may well replace M15
onwards, depending on what the groups say.

### What each milestone holds

Items marked *(can slip)* move to a later milestone when the time runs out.

**M7 Nothing gets lost**

- Bin: deleting an item or a photo sets `DeletedAt`; Restore; purge after 30 days. "Delete for good" stays for
  personal-data requests. Query filters leave binned material out of every query unless a page asks for it.
- Disk guard for contributor and curator uploads: refuse early from `Content-Length` when free space on the media
  volume or the temporary upload path is low, and add a daily limit on anonymous uploads per link.
- Optimistic concurrency (`xmin`) on `ArchiveItem`, with a translated message that keeps the typed text.
- `data-warn-unsaved` for all forms on the edit page. Uploading a photo must never save half-typed text, because on a
  published item that would make it public.
- Login limit per address, generous enough for a shared connection.
- Backup age and free space as an admin notice: `backup.sh` writes a timestamp that the app can read.
- Contributors search also finds the photographer or author, and exports without personal data leave that name out.
- Correct the docs: what the README promises, the update advice in `security.md`, and moving servers by backup and
  restore.

**M8 Safety net**

- `scripts/update.sh`: check free space, back up, pull, wait for `https://$COMMONAGE_DOMAIN/health` or a Compose
  health check (the app has no open port in production), and roll back through `restore.sh --yes` plus the old version
  and tag, warning that changes since the backup are lost. The script must survive replacing itself.
- CI starts the previous published image with `compose.production.yaml`, upgrades it to the candidate, and also forces
  a failed upgrade.
- Playwright with axe in CI on the main pages at 390 and 1280 px, one smoke test per JavaScript behaviour, and a run
  without JavaScript. A short manual zoom and screen reader checklist for each release.
- `SecurityHeaderTests` finds pages by itself instead of using a list, which today misses the contribution form,
  Thanks and Login.
- A test that fails when a new domain property is neither exported nor explicitly excluded.
- A written update policy: how fast a security fix ships, how long an older minor version gets fixes, and how
  operators follow releases. Pinned servers otherwise stop receiving fixes without noticing.

**M9 Quick wins**

- One shared way to show field errors, for all forms: a summary that lists them with links, `aria-describedby` merged
  with existing help links, and focus on the summary. Today's `ModelOnly` summary stays empty after field errors.
- A rate-limited date check showing "Understood as …" on the contribution form and the curator form, before saving.
  Saved items already show "Found when visitors filter by", so the wording can match. Sending is disabled after the
  first click.
- A file picker in the page's language. The browser's own control follows the browser's language, so an English page
  shows "Dateien auswählen" on a German computer, and the other way round. Use a translated button with the chosen
  file names, keeping the native control without JavaScript.
- Text-only items get a designed tile showing that they are text. Today they appear as an empty grey box in search and
  collections, which looks like a picture failed to load.
- Unambiguous dates and times in the curator pages. `en` shows `11/4/2026`, which means different days to American
  and British readers.
- Small things seen on screen: the search placeholder is cut off on narrow phones in German ("Archiv durchsuch"), and
  the Filters button on phones looks disabled.
- Remove the fixed root font size. Larger targets for the language buttons and tag pills, a heading on search results,
  `aria-current` in the navigation.
- Collections and Search always visible on phones; a breadcrumb on collection pages; admin navigation on every admin
  page, with touch-friendly controls.
- For curators: "Save and next" and "Save and add another", thumbnails and collection, tag and "needs" filters in the
  item list, the review queue oldest first and moving on to the next contribution after a decision. On a review, the
  photos come first: today they sit below the form and below the Publish and Reject buttons, about 1,400 px down.
  The edit page's heading names the item instead of saying "Edit item". Draft preview *(can slip)*.

**M10 Archive identity and trust pages**

- `ArchiveSettings` with `DefaultLanguage` and texts stored per language from the start: name, tagline, intro, contact,
  colour, logo. `DefaultLanguage` also becomes the fallback language, which today is always English. `lang` uses the
  full culture name, so `pt-BR` and `zh-Hant` survive.
- The archive's time zone. Curator pages and the activity log use the server's clock, which is UTC in the Docker image
  unless an operator sets `TZ`, and the docs do not mention it: in the walk-through, a German afternoon showed up an
  hour early. Check that the image has the time zone data this needs.
- Logo: new image code sharing the NetVips helpers, since the preview pipeline only writes JPEG and is tied to
  `MediaFile`.
- `/branding.css`, because inline styles are forbidden. It must set Bootstrap's button component variables,
  `--bs-link-color-rgb` with hover and the focus ring, since `--bs-primary` alone changes neither buttons nor links.
  Check contrast for link text, button text and hover, suggest a darker shade, version the address by settings hash,
  and prove it with a browser test.
- The archive's name in the title, navigation and footer, with "Powered by Commonage" and the source link staying.
- Fixed pages: About, Contact and withdrawal, Privacy, Imprint. Plain text with auto-links. Empty pages stay unlinked,
  and the checklist shows that they are unset. The consent text promises people can ask for removal, and today nothing
  says how.
- "This form belongs to {0}" on the contribution form; a reference code on Thanks, which says that no email follows.
- The "allow search engines" switch works at once: `noindex` on public pages and a basic `robots.txt`.
- A "Getting started" checklist on the admin page: name, contact, legal pages, first collection, first link, backup
  seen, restore tested.
- A short translatable curator handbook, including how to handle a removal request from someone in a photo, how to
  describe a photo, and how to record offline donations. Extended with each workflow milestone.

**M11 Look and feel**

- First the structural part of languages, so the design is tested in both directions once: one `SupportedLanguages`
  list, `dir` and Bootstrap's RTL stylesheet, a picker that scales, and pseudo and RTL test languages in the browser
  tests.
- A cheap seeded benchmark at 5–10k items before adding home page queries.
- Design foundation: remove the template's `_Layout.cshtml.css` and its stylesheet link, and put tokens for accent,
  surface, text, radius and spacing in `site.css`, on top of Bootstrap's variables. Readable body text, prose limited to
  68 characters, consistent cards with a visible focus state, logical properties only. System fonts only, because no
  single font covers every script. New admin screens from M12 on are built on this.
- Home page: the group's name and intro, search, recently added photos, collection cards with images, tags last.
  Today the first screen on a phone has no picture at all, and the page's heading is "Collections".
- Item page: on wide screens the text sits beside the photo. Today the description comes after every photo, so one
  added portrait pushed it about 1,600 px down, and the first photo alone runs past the bottom of an 800 px high
  screen. Photos stop sitting to one side with an empty column next to them.
- The language picker moves to the header. In the footer, a visitor who does not read the page's language has to find
  it at the bottom of a long page.
- Clearer navigation: "Collections" means the public list in the top bar and the management page in the admin row, the
  site name and "Collections" lead to the same place, and links dominate with large underlined blue headings. A
  slimmer demo notice: with the header, it takes up about 130 px of a phone screen.
- Images: an 800 px thumbnail with `srcset`, `contain` instead of cropping, the first preview loaded eagerly, ETags.
- One regeneration mechanism, also behind "retry failed" and rotation later: published files stay Ready and keep
  serving while new versions replace them atomically, because setting them back to Pending makes published photos
  return 404. Decide the 800 and 3000 px sizes together and backfill once.
- Search: removable filter chips, selects that apply at once, a helpful empty state. Print stylesheet *(can slip)*.

**M12 Catalogue fields**

Comes before bulk entry so a shoebox evening does not create backfill work.

- `Rights` as rightsstatements.org and Creative Commons URIs with plain labels, an archive-wide default, `RightsHolder`
  and `Credit`. A contributor's answer can propose a statement; an open licence is never assumed. Publishing a credit
  takes an explicit curator action, because those names were collected as curator-only.
- Reference number, where the original is kept, received from and when, internal notes, all curator-only and left out
  of public search. Names here are covered by the personal-data tools.
- A plain `AltText` on the photo, and curators can correct contributor details.
- "Needs rights" and "needs alt text" filters. Export format bump; the existing `rights` CSV columns are renamed
  deliberately, because they mean the contributor's answer.
- A simple spreadsheet import here if a tester or group reports needing it.

**M13 Shoebox**

- Files sent one at a time with progress, retry and safe resume; a documented limit for the path without JavaScript.
  One large POST would hit the request limit with a few dozen TIFFs.
- Photos on the New item form, instead of "Save the item first, then add photos". "Save as draft" becomes the main
  button there; today that is "Save and publish", on an item that cannot have photos yet.
- "Add many photos": one draft per file, with shared date, tags, rights and collection. A placeholder title is a flag
  shown through the resource files, never stored text, and it blocks publishing.
- Bulk actions on the selection or the whole filter: publish, add tag, set date, move collection, set rights. One audit
  entry per item, and a confirmation that shows the count.
- Duplicate warning through an index on `Sha256`, and a badge on contributions, never revealed to contributors.
- Previews appear without a reload, with visible queue progress. Tag suggestions *(can slip)*.

**M14 Phone-first sending**

- A shorter form with photos first. It is about 1,900 px long on a phone today and asks for a title before anything
  else, while a person arrives with a photo in mind. A full-width Send button, and no repeated sentence about curators
  looking at everything.
- `contribute.js` as progressive enhancement: file list, early checks, progress, no double sending. Thumbnails of
  chosen files need `img-src blob:`, allowed only on these routes (the policy is one global string today).
- Text drafts in `localStorage`: title, date and description only, never name, email or rights holder, with an expiry
  and a "Clear this draft" control, because events share devices.
- Try HEIC on a real iPhone. The browser may already convert it, and the bundled libvips probably has no HEVC decoder.
- A walk-through of new tasks, also given to the tester: send three photos over mobile data with a wrong date, read a
  letter, find everything from the 1950s in one street. It would help to have someone over 70 and a screen reader user
  try it, but nothing waits for that. A week of fixes follows, then decide what comes after this milestone.

**M15 Readable items**

- `Position`, `Caption` and `Rotation` on photos, Move earlier/later buttons, "use as cover". `AltText` per photo
  closes the alt-text item. "Move to a new item" and photo descriptions from contributors *(can slip)*.
- A public "large" image of about 3000 px, a per-archive setting, served through the published check, relying on
  browser zoom instead of a lightbox.
- Numbered thumbnail strip with captions. Previous and next, "More from {0}", "From the same time". Dates link to
  search.
- Description as the documented place for a transcript, with a "needs transcription" flag, since zoom helps only
  sighted visitors and handwriting cannot be searched.

**M16 Rights and sharing**

- Rights and approved credit on item pages. An optional reuse question and new consent wording on the form; earlier
  contributions stay under their stored text. Their consent covers "this website", which matters for harvesting later.
- "Cite this item", `rel=canonical`, a share button.
- Link previews, an Atom feed and a sitemap, all following the search engine switch.

**M17 Language reach**

The structural part is in M11. This is the rest of [Languages](#languages), reordered, since no group has asked for
a third language yet:

- A spike on plurals first: Weblate's RESX format has no plural support and needs an English base file, which the
  repository does not have, because English keys live in code. Compare ICU syntax in one value with other formats.
- A generated base file and a test that fails when a key is missing.
- Decide Weblate hosting, how its commits pass the sign-off check, and how translators accept the license. Consent and
  privacy strings need a reviewer, because the consent text is what a person agreed to.
- `DateVocabulary` and the content-language setting. First define the policy (content languages plus the current
  interface language), with regression tests that today's accepted dates stay accepted.
- Export README and demo content translated; a decision on language per item for mixed archives.
- A third language is a goal that does not block the milestone, since it depends on a volunteer.

Until then, write counts in label form (`Photos: {0}`), which needs no plural grammar.

**M18 Accounts and system**

- Authenticator two-factor login with recovery codes, using a QR encoder that M20 reuses. Admins can require it. Setup
  links stay the recovery path, because no email is sent. State where the secrets are protected.
- Data Protection keys encrypted with a key outside the database, including restore docs, the backup test (which
  relies on old logins still working) and what happens if that key is lost.
- System page: version, disk use and growth, failed previews with retry, `unaccent` state, last migration. Storage
  checks in `/health`, with a note for uptime monitors that a degraded answer is still HTTP 200.
- How long the activity log and Rejected or Withdrawn items are kept; activity log filters.

**M19 Discovery**

- Decade timeline with predefined CSS classes for bar widths, because the policy forbids inline styles.
- Tag page with rename, merge and delete unused. `Tag.Kind` (topic, place, person) with `/places` and `/people`. Naming
  living people stays a curator's decision.
- Collections: order, date span, cover, default sort, delete when empty.

**M20 Community knowledge**

- "Do you know more about this?", off by default: information, correction or removal request, stored as a private note,
  protected like the contribution form, and covered by the personal-data tools.
- "Help wanted" flag and a "Can you help?" page. Dashboard cards for what needs attention.
- Links: copy button, a printable poster with QR code (checked against the no-inline-style test), and an opt-in
  invitation on the collection page, which gives up being unlisted. A clear page for closed links.

**M21 Import and scale**

- CSV with its own template, without id or status columns, with defined handling of the apostrophe that exports add
  for spreadsheet safety. ZIP or a folder on the media volume. A dry run lists unreadable dates, unknown collections,
  missing files and duplicates.
- A 50,000-item benchmark on a small ARM container. Expected fixes: a composite index, cached counts, capped deep
  paging, and export streaming the JSON as well as the query, since today the whole document stays in memory.

### Rules for every milestone

- A change to the data model updates the export, its format version and `operations.md`.
- New pages are covered by the security header and browser tests. New text is in the resource files, with German.
- New screens work on a phone, with a keyboard and without JavaScript where they can.
- Each release passes the upgrade test and says in its notes whether regeneration needs to run.
- By M13: a short maintainer handover note, an architecture overview in `development.md`, `good first issue` labels,
  a second release approver when someone is available, and a README note that exports, backups and images stay usable
  if the project stops. The release approval and the Dependabot token currently depend on one person.

### Later, roughly by value

1. **PDF documents.** The bundled libvips has neither PDFium nor poppler, so this means a separate poppler step, with
   its parser risk, image size and ARM build to weigh. Text goes into search. AVIF and GIF in the same pass.
2. **Oral histories.** Audio through ffmpeg, range requests, native player, metadata stripped, and a transcript as part
   of the feature, because accessibility requires it.
3. **Visibility levels.** Record without media first, then members only and embargo dates. `PublishedItems` becomes
   `VisibleItems(viewer)`.
4. **Stories and exhibitions.** Ordered text and item blocks, plain text, one layout.
5. **Helper role.** Describes and uploads, but cannot publish, delete or see contributors' email addresses.
6. **Description history** with restore, and changed field names in the activity log.
7. **Display names** and "last login" for accounts.
8. **People as entities.** Name variants and person pages, hidden by default for people who may be living.
9. **Dublin Core**, and `/items/{id}.json` through `rel=alternate` (inline JSON-LD would break the security policy).
10. **IIIF** manifests with static tiles and a vendored viewer, once alt text and rights exist.
11. **OAI-PMH**, off by default, limited by consent and rights.
12. **Optional email** with the group's own mailbox: digest, password reset, receipt. The app must work without it.
13. **Places with coordinates and a self-hosted map** (vendored MapLibre and one regional PMTiles file).
14. **Short video** without re-encoding. **S3-compatible storage**, always through the permission-checking endpoint.
    **OCR for typed documents** as an opt-in container. **Near-duplicates** through a perceptual hash.
15. **Reading Commonage's own export**, for merging archives. Moving a server already works through backup and restore.

### Not planned

- **Several archives in one installation.** A tenant key would touch every query, backup, export and personal-data
  rule. Document one Compose project per archive behind a shared Caddy instead.
- **Cultural protocols in Mukurtu's style.** A partial copy would give false assurance. Point those communities to
  Mukurtu.
- **Public comment threads.** Moderation load, spam and statements about third parties; M20's private notes meet the
  need.
- **Mandatory email, newsletters, visitor accounts.** Contributors needing no email is a core promise.
- **Server-side AI** (transcription, handwriting recognition, face recognition, auto-tagging). Too heavy for small
  servers, cloud services break the no-third-party rule, and face recognition creates biometric data nobody consented
  to. Document a local whisper.cpp workflow for transcripts.
- **A dynamic IIIF image server, or video transcoding and streaming.** Another service and too much load. Long films
  can be linked from the group's own PeerTube.
- **Third-party map tiles, web fonts from CDNs, analytics, automatic update checks.** At most a version check that an
  admin clicks.
- **Rich text, Markdown or HTML in descriptions and pages.** XSS risk and policy exceptions, for little gain.
- **Configurable custom fields.** Too complex at this scale; a few fixed fields are enough.
- **A JavaScript-heavy interface.** No single-page app, and no drag-and-drop as the only way to reorder.

### Open decisions

1. **Order: decided, as above.** Look and feel comes before the shoebox work, so new curator screens are built once on
   the final design, and M9 brings the quickest curator improvements forward. It changes only if the tester's reports
   show a group is digitising now and needs the shoebox first.
2. **Default legal texts.** A factual default privacy text marked as a template to adapt, and no default imprint.
3. **Optional email.** Decide after M20, from whether contributions wait for weeks in practice.
4. **Exports without personal data: decided.** They leave out the names of photographers and authors too (M7).
5. **Support policy.** How long older minor versions get security fixes (M8).
6. **Weblate.** Hosted or self-hosted, and how translators accept the license (M17).

### Languages

See the "Languages" decision above for the rules that already apply.

These are the plans in detail. The structural part lands in M11 and most of the rest in M17; what is left
(sorting, search without spaces, language-aware search, other calendars) follows once the content language exists.

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

The earlier list, now placed in the milestones:

- A limit on login attempts per address: M7.
- Field errors linked to their fields with `aria-describedby`: M9.
- A description (alt text) for each photo: M12 and M15, with suggestions from contributors if time allows.
- A session with screen reader users: M14.
- Two-factor authentication, encrypted Data Protection keys, and how long the activity log is kept: M18.
