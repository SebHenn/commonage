# Security

How Commonage protects an archive, what the v0.1 security review checked, and what is still open. To report a
vulnerability, see [SECURITY.md](../SECURITY.md).

## What is worth protecting

- **Unpublished material**: drafts, contributions waiting for review, rejected and withdrawn items, and every
  original file (originals can hold GPS positions and camera serial numbers in their EXIF data).
- **Contributors' personal data**: names, email addresses, and what they sent in.
- **Curator and admin accounts**, which can publish, change and delete anything.
- **The archive's availability and integrity**: material that took years to collect.

## Who could attack

- Anyone on the internet: visitors, bots, and people who found or guessed a contribution link.
- Contributors, who can send in any file and any text.
- A curator account whose password was guessed or reused elsewhere.

## Measures

| Area | Measure |
|---|---|
| Access | Public pages and public image addresses read only from published items (`VisibleItems`, `VisibleMedia`). Admin pages are protected by policy per folder; admin-only pages (accounts, export, activity log) by a second policy. Originals and previews of unpublished items are served only to curators. An item under embargo (`PublicFrom` in the future, in the archive's time zone) is as hidden as a draft: it passes through `VisibleItems`/`VisibleMedia` with the day as a parameter, tested with a clock. Feeds and the sitemap are cached for ten minutes, so an item can show up to ten minutes late or stay that long after it is unpublished. An item shown as a record only (`MediaLevel.RecordOnly`) gives visitors no file, thumbnail, cover, link-preview picture or PDF-text match; `VisibleMedia` and `Visibility.ItemWithMedia` are the one place that decides, and `ItemFilter.SearchAllFileText` is off unless a curator searches. What a curator chooses ahead of publishing, such as a draft's photo as a collection's cover, is passed over until its item is published. Tested in `PublicationTests`, `MediaTests`, `AccountTests`, `ExportTests`, `CollectionTests`. `EndpointAccessTests` lists every address that is open without a login, so a new one cannot be open by accident. |
| Accounts | Passwords of at least 12 characters (length over forced symbols, NIST SP 800-63B). Five wrong passwords lock an account for five minutes, and one address can try 100 logins an hour. Accounts are created with one-time setup links, valid seven days. Disabling an account, or a password change, ends its logins within a minute. Admins cannot lock themselves out. An authenticator app (TOTP) can be added to each login, and admins can require it of everyone who manages the archive; wrong codes count towards the lockout like wrong passwords. Ten recovery codes, each usable once. Turning the app off or making new codes asks for the password again, and wrong passwords there and on "Change password" count towards the lockout too. Tested in `TwoFactorTests`. |
| Forms | Every form carries an antiforgery token. Redirects after login and language change only go to addresses on this site. |
| Staff names and last login | `StaffProfiles` holds an optional display name and the time of the last login per account, written after a successful login (password, authenticator code or recovery code). Read only on the admin-only Accounts pages, never on a public page, feed or link preview, and not in the export or the activity log (which keeps the email address). Changing a name is logged. The row goes with the account (cascade). `AccountTests`. |
| Helpers | The `Helper` role (policy `Describe`) reaches only the item list, the item form, "Add many photos", the preview and the generated pictures under `/admin/media` (not the original). Everything else under `/Admin` needs `ManageArchive` (curator or admin) or `Administer`. A helper only gets draft items, in the list, the form, the preview and the pictures, never published, submitted, rejected or withdrawn ones: what visitors see changes only through a curator, and no contribution or contributor name or email reaches a helper (the contribution is not even loaded). The start page counts, the "already in the archive" file check and the reference-number check show a helper drafts only. Publishing, unpublishing, accepting, rejecting, deleting, withdrawing and removing a photo are refused with 403 on the server, not only hidden. Accepted: a helper can read "received from" and the internal notes of the items they open; the handbook says so. `HelperTests` (including a test that lists every `/Admin` address a helper can reach), `AccountTests`. |
| Import | Only curators (the `/Admin` policy). The sheet and ZIP are kept in `import-staging` on the media volume, which is never served by path, for a day at most. A sheet is read as strict UTF-8, at most 20 MB and 100,000 rows; a ZIP at most 4 GB and 200,000 entries; every entry is counted as it is unpacked into a temporary file, and a name inside it is never used in a path. A folder to import from is chosen from a listing of `import/`, links and hidden files are skipped. Files go through the same checks as an upload. One import at a time. Everything imported is a draft; the activity log records each item and the import, never the spreadsheet or the contributors' details. `ImportTests`. |
| Earlier texts | `ItemRevisions` keeps what a title, description, date, credit, original location or reference number said before a change, with the account that changed it (its name at the time) and when. The fields are a fixed list (`RevisionField`): never a contributor's name or email, the internal notes or where an item was received from, so removing a contributor's details never has to touch it. Who changed it is forgotten on the activity log's schedule (`ActivityLogKeptYears`); the text itself stays with the item. Shown on the item's admin page only; a curator may restore, a helper only reads. The rows go with their item (cascade, hidden while it is in the bin) and are not exported. `RevisionTests`, `ExportTests`. |
| Recordings | Only curators can add a recording (MP3, M4A, WAV, FLAC, recognised by their first bytes); contribution links refuse them. Visitors hear only a copy made by the app itself (`AudioTools`, no outside program): the sound is copied byte for byte, and ID3v2/ID3v1/APE tags (MP3), `udta`, `meta`, `uuid` and padding boxes and the dates in the headers (M4A), every chunk except `fmt `, `fact` and `data` (WAV), and every block except stream info and the seek table (FLAC) are left out. A file that is not built as its type says (impossible sizes, missing parts, fragmented M4A) fails and has no copy, so nothing is ever served untouched. The parser is managed code reading sizes against the file's length, with the header of an M4A limited to 64 MB; it runs on the single preview queue and a failure marks only that file. Served for published items only, from `/media/{id}/audio` with range requests, with its own copy (`audio/…` key); the original is only at `/admin/media/{id}/original` for curators. Accepted: the sound itself may contain a voice that identifies someone, which is what an oral history is; curators decide what to publish. `AudioTests`. |
| PDF files | Only curators can add a PDF; contribution links refuse it. A PDF is recognised by `%PDF-` and read by poppler (`pdfinfo`, `pdftoppm`), a large C++ parser, so it runs as separate processes, never inside the app: each call has a time limit (`Uploads:PdfTimeoutSeconds`, 60) and is killed with its children when it takes longer, the size of the drawn picture is set by us with `-scale-to` whatever size the page claims, at most 300 pages are drawn, and no part of the file or its name reaches a command line (arguments are passed as a list, the file is copied to a random name in an owner-only temporary folder that is removed afterwards). Pages are served only for published items; the PDF stays private. Accepted: a hostile PDF made by a curator could still exploit poppler, which runs with the app's rights in the container; build with `WITH_PDF=false` to remove it. `poppler-utils` comes from the distribution at build time and is not pinned to a version, so a rebuild of the same commit can ship another poppler (accepted; the base image digest is pinned). The text of a PDF (`pdftotext`, same limits, control characters removed, at most 200,000 characters) is only matched by search, never shown, and it is in the index only while the item is not in the bin and the PDF is not in the bin; a draft is found only by curators, as before. The preview queue is single, so a very long PDF delays other previews (at most 60 s per page); copies of a PDF left by a stopped app are removed at the next start. |
| Uploads | Files are recognised by their content (magic bytes), never by name or browser type, and stored under random names outside the web root. Originals are never changed or served to visitors. Previews are made by libvips, rotated upright and stripped of all metadata. Images over 250 megapixels get no preview (decompression bombs), also for GIF and AVIF (`Images_with_more_pixels_than_the_limit_get_no_preview`). GIF and AVIF (libheif with an AV1 decoder, from M23) are further decoders reachable from a contribution link; a GIF is read as its first frame only. There is no time limit on a decode, so a very large AVIF costs memory and time on a small server; the pixel limit and the three-attempt guard are the protection. Size limits per file and per request. A file that the preview maker fails on, in whatever way, costs only its own preview, and one that takes the app down is given up after three attempts instead of at every start (`PreviewFailureTests`). |
| Browser | Strict Content Security Policy (no inline script or style, no framing, no plugins; only the contribution form may show `blob:` images, for the photos someone has chosen on their own device, and a picture opened on its own has no scripts at all but lets the browser's picture view style itself), `nosniff`, `Referrer-Policy: same-origin` (setup links never leak to other sites), `X-Frame-Options`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`. No `Server` header. `SecurityHeaderTests` checks the headers and that no page contains inline script or style, so the policy can stay strict. |
| Beyond the website | The feed, sitemap and link previews hold nothing a public page does not show, and are switched off with "Let search engines list this archive". Contributions that agreed to "this website" only keep their picture and text out of them. |
| Output | Razor encodes all text. The CSV export prefixes cells that start with `=`, `+`, `-` or `@`, so contributed text cannot become a spreadsheet formula. |
| Database | All queries go through EF Core with parameters; the only raw SQL (`AccentFolding`) contains no input. Contributor search escapes `%` and `_`. |
| Spam | Unlisted contribution links, a honeypot field, a minimum fill time and a per-address limit. Spam gets the normal thank-you page. |
| Contribution links | A link is 128 random bits and unlisted unless a curator ticks "Invite visitors on the collection's page", which shows the link's heading and address on that collection's public page while it is open (off by default; the page says what it gives up). A closed link answers `410` with a page that shows nothing from the link, not even its heading, and takes nothing; an unknown token is `404`. The poster is a curator page and holds only what the contribution form shows anyway. |
| Notes from visitors | "Do you know more about this?" (`/items/{id}/tell`) is the one public form that stores anything, and is off by default. It only exists for published items, has a honeypot, a minimum fill time, a per-address limit and a 64 KB request limit, and is not indexed. A note holds an optional name and email address: curators only, never in the activity log (it names the item, not the visitor), feeds or any public page; curators can remove the details or delete the note, and the personal-data tools find it. |
| Transport | In production, Caddy terminates HTTPS with automatic certificates and redirects HTTP; the app sends HSTS for a year. Only Caddy is reachable; the app accepts forwarded headers only because nothing else can reach it, and accepts only the archive's own host name. Login cookies are `Secure`, `HttpOnly` and `SameSite=Lax`. |
| Containers | The app runs as a non-root user, without any Linux capability, and cannot gain privileges. The database and the app have no ports open to the internet. |
| Personal data | Contributor details are visible to curators only, removable on request, never written to the activity log or to logs (EF Core does not log parameter values). Exports include them only on request; each export is logged. |
| Errors | Visitors see a plain page with a reference number; details go to the log. |
| Repository | CI scans the whole history for secrets (gitleaks), builds with warnings as errors, and runs all tests and the backup-and-restore test. CodeQL analyses the C# code and the workflows; dependency review refuses pull requests that add vulnerable dependencies; Dependabot proposes updates weekly, packages and actions once a release is a week old (fixes for known vulnerabilities and new base images at once), and its patch and minor updates merge only after every required check has passed; OpenSSF Scorecard rates the setup. A weekly scan of the newest released image fails when .NET, libvips or the base image has a known high or critical vulnerability with a fix, which is when a bug-fix release is due. CI also runs axe on every main page in a browser, and an update from the previous release, including a failed one that has to go back. `main` only changes through pull requests with passing checks, and release tags cannot be moved or deleted. |
| Supply chain | GitHub Actions are pinned to commits, container images to digests and NuGet packages to content hashes (`packages.lock.json`), so a moved tag or changed package cannot change what runs. Workflows have read-only tokens unless a job needs more. Release images are built by the release workflow only after a maintainer approves it, and carry a software bill of materials and a signed build attestation (`gh attestation verify`). |

## Where the secrets are

| Secret | Where it is, and how it is protected |
|---|---|
| Passwords | Only as hashes in the database (ASP.NET Core Identity, PBKDF2 with a salt per password). |
| Login keys | The keys that sign login cookies, setup links and form tokens are in the database (`DataProtectionKeys`), so that logins survive a restore. Each is encrypted with AES-256-GCM with `COMMONAGE_LOGIN_KEY` (`Security/LoginKeys.cs`), which is in `.env` only, not in the database or the backups. Tested in `LoginKeyTests` and in the backup-and-restore test, which also checks that no key in the database can be read. |
| Authenticator secrets and recovery codes | In the database, encrypted with the login keys (`ProtectedUserStore`), so they need `COMMONAGE_LOGIN_KEY` as well. Recovery codes are shown once and kept nowhere else. |
| Setup links | Not stored. They are signed with the login keys, work once and for seven days, and a new one makes older ones invalid. |
| Database password, first admin's password, login key | In `.env` on the server, readable by its owner only (`chmod 600`), and never in the repository. Keep a copy apart from the backups. |

Someone with both the database and `.env`, such as an attacker on the server itself, can do anything the app can. Keeping the
server updated and its access limited is what protects against that.

## Known limits and open points

- **Two-factor login is optional** unless an admin requires it. Without it, a password manager and long passwords are what
  protects an account.
- **An admin can turn off anyone else's authenticator app**, because Commonage sends no emails and that is the way back in
  for someone who lost their phone. Together with a setup link, an admin can take over a curator's account. Admin accounts need
  the same care as the server.
- **The login page reveals whether an account exists** when it has been locked after wrong passwords ("too many
  failed attempts").
- **Deleted material stays for 30 days.** Items and photos in the bin keep their files and personal data until they are
  deleted for good, which curators can do at once when someone asks for removal.
- **The default privacy text is a template**, describing what Commonage stores. Each group has to adapt it and check its
  own legal duties, including whether it needs an imprint.
- **Without `COMMONAGE_LOGIN_KEY`, the login keys are stored unencrypted**, and whoever has the database or a backup could
  forge logins. Admins see a notice then, and `scripts/update.sh` adds the key to `.env`.
- **Anyone who knows an account's email address can keep it locked** by sending five wrong passwords every five minutes.
  The lock ends by itself five minutes after the last of them.
- **The limits per address count each IPv6 address on its own**, so someone with a whole IPv6 network can send in more
  contributions and try more logins than the limits say. The lockout of each account still holds.
- **`/health` tells anyone whether the archive needs attention** (a nearly full disk, an overdue backup), though not which.
- **Images are decoded by libvips**, a large C library. Keep the Docker image up to date by installing each new
  release ([deployment.md](deployment.md)) to get its security fixes.
- **The activity log and rejected or withdrawn contributions are kept indefinitely** unless an admin sets a time under
  "Settings" → "How long records are kept". The log holds curators' email addresses; rejected contributions hold
  contributors' names, email addresses and material that will not be published.

## Checking for vulnerable packages

```sh
dotnet list package --vulnerable --include-transitive
```

Dependabot does this continuously on the repository; running it before a release costs nothing. Bootstrap, jQuery and
jQuery Validation are copied into `wwwroot/lib` and are not seen by Dependabot: check them by hand before a
release.
