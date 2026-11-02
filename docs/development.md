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
