# Development

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

## Accounts, activity log, export and backups

How these work for the people running an archive is in [operations.md](operations.md). In the code:

- Admin-only pages are protected by the `Administer` policy in `Program.cs` (`/Admin/Users`, `/Admin/Export`,
  `/Admin/Activity`). Setup links come from `Identity/Accounts.cs`.
- Every change a curator or admin makes should call `AuditLog.Record` before `SaveChanges`, with a new
  `AuditAction` and its text in `Audit/AuditText.cs` if needed. Never put contributor details into an entry.
- `Export/ArchiveExporter.cs` writes the export. If you change its format, raise `ArchiveExporter.Version` and
  update operations.md.
- `scripts/backup.sh`, `scripts/restore.sh` and `scripts/test-backup-restore.sh` are POSIX `sh` and must keep working
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

## Dependency updates

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
2. After merging, tag the merge commit on `main` and push the tag:
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

## Tests

```sh
dotnet test
```

Integration tests start a throwaway PostgreSQL container, so Docker must be running.

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
