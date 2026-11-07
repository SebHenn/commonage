# Development

## How it fits together

One ASP.NET Core application with Razor Pages, one PostgreSQL database, and a folder of files. There are no other services, no
queue server and no build step for the front end: pages work without JavaScript, and a little of it makes them better.

| Folder in `src/Commonage.Web` | What is in it |
| --- | --- |
| `Pages` | Every screen. `Pages/Admin` is for curators and needs a login, `Pages/Admin/Users` for admins. Everything else is public. |
| `Domain` | What an archive is made of: items, photos, collections, contributions, dates, rights. No database or web code. |
| `Data` | The database through EF Core, its migrations, and the bin. Query filters keep what is in the bin out of every query. |
| `Search` | Public search and the curators' item list. |
| `Media` | Uploads, the background work on previews, and the only way to a stored file (`MediaEndpoints`). |
| `Contributions` | Contribution links and what protects them from spam. |
| `Identity`, `Security` | Accounts and roles, and security headers. |
| `Audit` | The activity log. An entry is saved together with the change it describes. |
| `Export` | The archive as open files. A test makes every new field a decision: exported, or left out for a reason. |
| `Operations` | Free disk space, the age of backups, and the archive's name and settings. |
| `Resources` | Translations. English is the key, and every other language has a file here. `SharedResource.resx` is the English base file for translators, written by the tests. |

What is special about it, and easy to break by accident:

- **Only published items are public.** Public pages go through `db.PublishedItems`, and `/media` serves nothing else.
- **Originals are private.** Visitors only ever see generated pictures, which carry no EXIF. So GPS positions stay private.
- **A contributor's personal details stay with curators.** They are never on a public page unless a curator chose so, and never in
  the activity log.
- **Nothing is deleted at once.** Items and photos go to the bin for 30 days first.
- **Text is written for any language.** No sentences built from parts, nothing stored in one language that a person sees later.

## Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL and for the tests)

## Run everything in Docker

```sh
cp .env.example .env    # set your own POSTGRES_PASSWORD
docker compose up --build
```

The app runs at http://localhost:8080. Log in with the `COMMONAGE_ADMIN_EMAIL` and
`COMMONAGE_ADMIN_PASSWORD` from your `.env` (use "Curator login" in the footer).

## Run the app from your IDE

Start only the database:

```sh
docker compose up -d db
```

Store the connection string in [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets).
They live in your user profile, outside the repository:

```sh
dotnet user-secrets --project src/Commonage.Web set "ConnectionStrings:Default" \
  "Host=localhost;Database=commonage;Username=commonage;Password=<your POSTGRES_PASSWORD>"
dotnet user-secrets --project src/Commonage.Web set "InitialAdmin:Email" "admin@example.org"
dotnet user-secrets --project src/Commonage.Web set "InitialAdmin:Password" "<a password of your choice>"
dotnet run --project src/Commonage.Web
```

The database schema is created and upgraded automatically when the app starts. The first
administrator is created from `InitialAdmin:*` only while the archive has no users.

To open a SQL shell: `docker compose exec db psql -U commonage`.

## Uploaded files

Originals and their previews are stored under `Storage:RootPath`. It defaults to
`src/Commonage.Web/App_Data/media` when you run from the IDE (ignored by git) and to the
`media-data` volume in Docker. The directory is never served directly; every file goes through
`Media/MediaEndpoints.cs`, which checks permissions. The per-file limit is
`Uploads:MaxFileSizeMegabytes` (default 200).

Previews are generated in the background with [NetVips](https://github.com/kleisauke/net-vips)
(libvips). They are rotated upright and contain no EXIF, so GPS coordinates stay in the private
original.

`wwwroot/js/uploads.js` sends curators' photos one per request, so a batch is not limited by the size
of a request and one lost connection does not lose it. A request that accepts `application/json` gets a
short answer for the queue (`UploadReply`); the same form without JavaScript sends all files at once, up
to 1 GB, and gets a page. Sending a file again is safe, because a file with the same SHA-256 is reported
instead of stored unless `addIdentical` is set. `/admin/media/status` tells the page which previews are
done. Contributors never hear about identical files: that would say what the archive holds.

A draft from "Add many photos" has an empty title. Nothing stored says "Untitled", because that would be
in one language and could pass for a real title. `ArchiveItem.ShownTitle` shows it in any language, and
`CanBePublished` is false until there is one.

## Search and dates

Visitors search at `/search`. Filters, sorting and paging are in `Search/ItemSearch.cs`; the date parser
is `Domain/DateRange.cs` and has its own unit tests in `DateRangeTests`.

Search ignores accents by passing documents and search text through the database function `commonage_fold`
(see `Data/AccentFolding.cs`). A migration creates it from built-in PostgreSQL functions; at startup the app
switches it to the `unaccent` extension if the server has it, and logs a warning if not. `AccentFoldingTests`
checks both versions with text in several languages and scripts. To see which search words PostgreSQL makes of
a text:

```sh
docker compose exec db psql -U commonage -c "select to_tsvector('simple', commonage_fold('Die Mühle an der Mühlenstraße'))"
```

## Contributions

Curators create contribution links under "Manage archive" → "Contribution links". The public form is
`Pages/Contribute/Index.cshtml`; what it receives is an item with status `Submitted` plus a `Contribution`
record. Spam protection is in `Contributions/SpamProtection.cs` and configured in the `Contributions`
section (`MinimumFillSeconds`, `MaxSubmissionsPerHour`). The tests switch both off in `CommonageAppFactory`
and test them separately in `ContributionTests`.

With JavaScript, `wwwroot/js/contribute.js` takes over sending, and the page's handler answers it in JSON: a link to the
thanks page, or the mistakes per field. That is why chosen photos survive a mistake. Without JavaScript the same handler
redirects or shows the page again.

- **Sent once.** A form carries a random `SendingKey`. `Contributions/SendingFingerprints.cs` hashes it with what was sent,
  leaving out name and email address, into `Contribution.SendingFingerprint`, which has a unique index. The same form sent
  again gets the first one's reference. Going back and changing something makes a new contribution. If storing a photo fails,
  the fingerprint is cleared again, so that sending again really sends. Once a request has arrived it is finished regardless
  of the connection, because half a contribution would then be recognised as the whole.
- **Drafts** live in `localStorage` under `commonage-contribution-draft:` plus the path, and only hold title, date and
  description. Expired ones, of any link, are removed when any contribution form opens, so a device that never opens one
  again keeps its draft until the browser's storage is cleared.
- **Pictures from the device** need `img-src blob:`. `SecurityHeaders` allows that on `/contribute` only.

## Beyond the website

`Search/PublicFeeds.cs` has the feed, the sitemap and `LinkPreview`. All of them follow `AllowSearchEngines`. Addresses come from
the request, so behind Caddy they depend on `ASPNETCORE_FORWARDEDHEADERS_ENABLED` in `compose.production.yaml`; `SharingTests`
tries that setting. Text goes into XML through `XmlText`, because one pasted control character would make a whole feed unreadable.
`ArchiveItem.MayTravel` decides what goes along with a link, and it needs the contribution to be loaded.

## Photos of an item

`MediaFile.Position` orders an item's photos, and the first is its cover. Anything that picks or lists photos must order by
`Position` and then `Id`, because a photo back from the bin can share a position. Moving and turning a photo must not save the
item itself: its `Version` changing would make a form that is open say that someone else changed it.

`MediaProcessor` makes the sizes in the background. `Rotation` is what curators asked for and `RenderedRotation` what the stored
files show, and files go on being served until the new ones replace them. The address stays the same, so the `ETag` includes
`RenderedRotation`. A turn that fails is given up, so that no page waits for it. `DerivativeVersion` and `HasLarge` say which sizes
exist. Raising `CurrentDerivativeVersion` makes older files catch up when nothing else is waiting, one pass per start of the app.

A large picture is opened as the picture itself, because the browser's own view already fits and zooms it on every device. That
view styles itself inline, so `/media` and `/admin/media` have their own `PicturePolicy`, with no script at all.

## Accounts, activity log, export and backups

How these work for the people running an archive is in [operations.md](operations.md). In the code:

- Admin-only pages are protected by the `Administer` policy in `Program.cs` (`/Admin/Users`, `/Admin/Export`,
  `/Admin/Activity`). Setup links come from `Identity/Accounts.cs`.
- Every change a curator or admin makes should call `AuditLog.Record` before `SaveChanges`, with a new
  `AuditAction` and its text in `Audit/AuditText.cs` if needed. Never put contributor details into an entry.
- `Export/ArchiveExporter.cs` writes the export. If you change its format, raise `ArchiveExporter.Version` and
  update operations.md.
- `scripts/backup.sh`, `scripts/restore.sh`, `scripts/update.sh` and their tests are POSIX `sh` and must keep working
  in Git Bash on Windows (`MSYS_NO_PATHCONV=1`, and paths through `cygpath -m` for native programs such as curl).
  Run the restore test with `sh scripts/test-backup-restore.sh`; it uses its own Compose project and ports.

Pages must not contain inline script, inline event handlers (`onclick`) or `style` attributes: the Content Security
Policy in `Security/SecurityHeaders.cs` blocks them, and `SecurityHeaderTests` fails if a page has any. Put behaviour
in `wwwroot/js/site.js` and styles in `wwwroot/css/site.css`. Forms whose action cannot be undone ask first with a
`data-confirm="..."` attribute, so the question can be any text in any language.

## Demo content and production setup

`Demo/DemoContent.cs` fills an empty archive with made-up content when `Demo:Language` is `en` or `de`
(`COMMONAGE_DEMO` in Docker); the pictures are drawn in `Demo/DemoImages.cs`. Keep both languages in step when you
change it. `compose.production.yaml` and `deploy/Caddyfile` are the HTTPS setup from [deployment.md](deployment.md);
to try it locally, set `COMMONAGE_DOMAIN=localhost` (Caddy then uses its own local certificate authority) and use
`;` instead of `:` in `COMPOSE_FILE` on Windows.

## Look and languages

`wwwroot/css/site.css` starts with the design's tokens, on top of Bootstrap's variables, so `/branding.css` can override
them. Use logical properties (`margin-inline-start`, `ms-*`), never left and right, and system fonts only. A new interface
language is its resource file and one line in `SupportedLanguages.cs`, see [translating.md](translating.md#a-new-language).

## Forms

A form with fields starts with `<partial name="_ErrorSummary" />`. `asp-validation-for` and `asp-for` then link messages
and fields by themselves (`Forms/FieldErrors.cs`, `site.js`). Other behaviour is switched on by attributes, handled in
`wwwroot/js/site.js`, because the Content Security Policy forbids inline script:

- `data-confirm`: asks before sending.
- `data-warn-unsaved`: warns before leaving with unsaved changes.
- `data-send-once`: a second click does nothing. For forms that upload, or that would otherwise look like a conflict.
- `data-date-result`: on a date field, says how the date is understood while it is typed.
- `data-choose-text` and `data-none-text`: a file picker in the page's language. Without JavaScript, the browser's own
  control stays.

`contribute.js` only runs on the contribution form, and `uploads.js` only on curators' upload forms.
- `data-required-message`: a required checkbox, which ASP.NET's validation leaves out.

Without JavaScript, `css/nojs.css` shows what would otherwise stay folded away.

## Tests

`dotnet test` runs everything. It needs Docker, for PostgreSQL, and Chrome installed, for the browser tests.

- `tests/Commonage.Tests` drives the app over HTTP. It covers rules and data, and is quick.
- `tests/Commonage.BrowserTests` drives the real app in Chrome with Playwright. It also tries pages from right to left
  and in a made-up language with much longer text. It runs axe on every main page at phone and desktop width, checks for sideways scrolling and anything the browser refused (such as a Content Security Policy
  violation), tests each JavaScript behaviour, and does the main tasks with JavaScript switched off. Add new pages to
  `AccessibilityTests`, and a test for each new behaviour in `wwwroot/js`.
- `scripts/test-backup-restore.sh` and `scripts/test-update.sh` run whole archives in Docker. The second needs ports 80
  and 443 free.

Before a release, also check by hand what automated tests cannot judge
([accessibility.md](accessibility.md#before-each-release)).

## Dependency updates

`packages.lock.json` pins every NuGet package by content hash, and CI and the Docker build refuse anything else. After
changing a package reference, run `dotnet restore` and commit the changed lock files. Dependabot updates them by itself.

Dependabot opens update pull requests every Monday. Patch and minor updates, and rebuilt base images with an
unchanged tag, merge by themselves once every required check has passed. Each change to `main` asks Dependabot to
rebase the next waiting update (`.github/workflows/dependabot-auto-merge.yml`), since `main` only accepts pull
requests that are up to date. An update whose checks fail stays open for a maintainer.

Major updates are never merged automatically. Read the release notes, check in the CI log that all tests ran (a test
runner that finds no tests still succeeds), then merge.

The automation uses the secret `DEPENDABOT_AUTOMERGE_TOKEN`, stored twice under Settings → Secrets and variables:
once under Actions and once under Dependabot. It is a maintainer's fine-grained token for this repository only, with
"Contents" and "Pull requests" set to read and write. GitHub Actions' own token cannot do this job: Dependabot ignores
its commands, and merges made with it do not run the checks on `main`. When the token expires, the workflow shows a
warning on each update and updates are merged by hand until a new token is stored.

## Releasing

1. In a pull request: raise `<Version>` in `Directory.Build.props`, give the `CHANGELOG.md` entry its date, and
   update the version in docs/deployment.md (`git checkout vX.Y.Z`). Check Bootstrap, jQuery and jQuery Validation in
   `wwwroot/lib` for security releases (Dependabot does not see them).
2. After merging, tag the merge commit on `main` and push the tag. Do this soon: bug-fix releases are how security fixes
   reach servers, including rebuilt base images from Dependabot.
   `git tag -a v0.1.1 -m "Commonage 0.1.1" && git push origin v0.1.1`.
3. The Release workflow waits for approval in the `release` environment (Actions tab → the run → "Review
   deployments"). Approving it builds and publishes the images for x86 and ARM, with SBOM and attestation.
4. Create the GitHub release from the CHANGELOG entry (`gh release create v0.1.1 --notes-file ...`).

Release tags cannot be moved or deleted once pushed; a mistake is fixed with the next patch version.

## Database changes

After changing an entity or `CommonageDbContext`, create a migration and commit it:

```sh
dotnet tool restore
dotnet ef migrations add <DescriptiveName> --project src/Commonage.Web --output-dir Data/Migrations
```

## Translations

UI text is written in English and used as the key, for example `@T["Save"]` in a page. German
translations live in `src/Commonage.Web/Resources/SharedResource.de.resx`. Text without a
translation is shown in English, so add the German entry whenever you add new UI text.

`TranslationTests` reads every text from the code: `T[...]`, `localizer[...]`, `.Plural(...)` and the `Name` and
`ErrorMessage` of attributes. Only a written-out text is found, so look one up with its words, not through a variable. The
tests fail when a language the interface offers lacks a text, when a translation is left over, empty or loses a `{0}`, and when
a count lacks a form its language needs.

They also keep `Resources/SharedResource.resx` up to date: the English base file that translators in Weblate work from. It
is not built into the application. After changing text, write it again and commit it:

```bash
UPDATE_TRANSLATION_BASE=1 dotnet test tests/Commonage.Tests --filter TranslationTests
```

A count in a sentence is written in ICU MessageFormat, so that every language can give it the right form:
`T.Plural("Up to {0, plural, one {# photo} other {# photos}}.", count)`. Label form, `Photos: {0}`, needs none.
[translating.md](translating.md) has the rest: Weblate, plurals and adding a language.

Dates are read in the archive's languages (`DateLanguages.Current`, set for each request) through `DateVocabulary`. Stored
dates are only days, so a new language never changes what is stored. `DateRangeBeforeM17` in the tests is the reader as it was
before languages came in, kept so that a test can check every date it accepted still means the same.

## Secrets and private data

This repository is going to be public. Never commit:

- passwords, API keys, tokens, connection strings with credentials, certificates (`.pfx`, `.pem`, `.key`)
- `.env` files (only `.env.example` with placeholder values belongs in git)
- real archive material: uploads, database dumps, backups, or anyone's personal data
- demo content you do not have the right to publish

Local configuration goes in user-secrets or `.env`, both ignored by git. CI scans the full history
with [gitleaks](https://github.com/gitleaks/gitleaks) on every push. To run the same scan locally:

```sh
docker run --rm -v "$PWD:/repo" ghcr.io/gitleaks/gitleaks:v8.24.2 git /repo --verbose --redact
```

If a secret is ever committed, deleting it in a new commit is not enough. Revoke or rotate it
first, then remove it from the history.
